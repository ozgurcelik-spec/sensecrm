using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// Platform konsolu, denetim, kullanım, onboarding ve izolasyon test sınıflarının ortak yardımcıları (M7). Ortak <c>PlatformKit</c>'e ek olarak yalnız bu dosyadaki
/// testlerin kullandığı, benzersiz adlı yardımcılar içerir. Veritabanı testler arasında sıfırlanmaz: her test benzersiz belirteçli kendi organizasyonlarını açar.
/// </summary>
internal static class ConsoleTestKit
{
    public const string IstanbulZone = "Europe/Istanbul";

    /// <summary>Limitsiz plan sınırları JSON'u (plan satırlarında <c>maxRecords</c> her zaman bulunur; Migrator böyle yazar).</summary>
    public const string NoLimits = """{"maxRecords":{}}""";

    /// <summary>Benzersiz, yalnız küçük harf/rakam içeren belirteç (organizasyon adında arama anahtarı olarak kullanılır).</summary>
    public static string Token(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    public static string OrgUrl(Guid tenantId) => $"{PlatformBase}/organizations/{tenantId}";

    /// <summary>
    /// Yeni organizasyon + <c>OrganizationCreated</c> olayını işler: hesap satırı <c>signup</c> kaynaklı olur. Sonradan yapılan plan/deneme değişikliklerinin
    /// gecikmiş olayla ezilmemesi için testler değişiklikten <b>önce</b> bu yardımcıyı kullanır.
    /// </summary>
    public static async Task<TestOrg> SyncedOrgAsync(this WebApplicationFactory<Program> host, string name)
    {
        var org = await host.NewOrgAsync(name);
        await host.DrainOutboxesAsync();
        return org;
    }

    /// <summary>Kiracının saat diliminde bugün (kiracı takvimi) + gün.</summary>
    public static DateOnly TenantToday(int addDays = 0, string zone = IstanbulZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone))).AddDays(addDays);

    public static string Day(this DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<HttpResponseMessage> SendRawAsync(this HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>ProblemDetails yanıtını durum + <c>code</c> ile doğrular ve gövdeyi (errors/args için) döner.</summary>
    public static async Task<JsonElement> ProblemBodyAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        var json = JsonDocument.Parse(text).RootElement.Clone();
        json.GetProperty("code").GetString().ShouldBe(code, text);
        return json;
    }

    public static Task<JsonElement> DetailAsync(this HttpClient platform, Guid tenantId) => platform.GetJsonAsync(OrgUrl(tenantId));

    /// <summary>C-SEC2: <c>blocked</c> askı step-up ister; yardımcı çağıran platform yöneticisinin (<see cref="PlatformKit.PlatformPassword"/>) parolasını her zaman gönderir (readOnly'de yok sayılır).</summary>
    public static Task<HttpResponseMessage> SuspendRawAsync(this HttpClient platform, Guid tenantId, string? reason = "test askisi", string? mode = null, string? currentPassword = PlatformPassword) =>
        platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(tenantId)}/suspend", new { reason, mode, currentPassword });

    public static async Task SuspendAsync(this HttpClient platform, Guid tenantId, string? reason = "test askisi", string? mode = null) =>
        (await platform.SuspendRawAsync(tenantId, reason, mode)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    public static Task<HttpResponseMessage> ReactivateRawAsync(this HttpClient platform, Guid tenantId) =>
        platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(tenantId)}/reactivate");

    public static async Task<HttpResponseMessage> RequestDeletionRawAsync(this HttpClient platform, Guid tenantId, string? reason = "test silme", int? retentionDays = null) =>
        await platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(tenantId)}/deletion-request", await platform.DeletionBodyAsync(tenantId, reason, retentionDays));

    /// <summary>
    /// C-SEC2 H2: silme talebi gövdesi — sunucudaki gerçek kiracı adı (<c>confirmTenantName</c>) ve çağıranın parolası (<c>currentPassword</c>) eklenir. Negatif testler
    /// <paramref name="confirmTenantName"/> / <paramref name="currentPassword"/> ile bilerek yanlış değer verir.
    /// </summary>
    public static async Task<object> DeletionBodyAsync(this HttpClient platform, Guid tenantId, string? reason = "test silme", int? retentionDays = null, string? confirmTenantName = null, string? currentPassword = PlatformPassword)
    {
        var name = confirmTenantName;
        if (name is null)
        {
            // Bilinmeyen kiracı (404 testleri): ad okunamaz, uydurma bir ad gönderilir.
            using var detail = await platform.GetAsync(OrgUrl(tenantId), Ct);
            name = detail.IsSuccessStatusCode ? JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct)).RootElement.Str("name") : "bilinmeyen";
        }

        return new { reason, retentionDays, confirmTenantName = name, currentPassword };
    }

    public static Task<HttpResponseMessage> CancelDeletionRawAsync(this HttpClient platform, Guid tenantId) =>
        platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(tenantId)}/deletion-request/cancel");

    public static Task<HttpResponseMessage> PutSubscriptionRawAsync(this HttpClient platform, Guid tenantId, string? planCode, string? trialEndsOn = null, object? overrides = null) =>
        platform.SendRawAsync(HttpMethod.Put, $"{OrgUrl(tenantId)}/subscription", new { planCode, trialEndsOn, overrides });

    public static Task<HttpResponseMessage> RefreshUsageRawAsync(this HttpClient platform, Guid tenantId) =>
        platform.SendRawAsync(HttpMethod.Post, $"{OrgUrl(tenantId)}/usage/refresh");

    /// <summary>Platform yöneticisinin kendi kullanıcı kimliği ve e-postası (<c>GET /me</c>).</summary>
    public static async Task<(Guid UserId, string Email, Guid TenantId)> WhoAmIAsync(this HttpClient client)
    {
        var me = await client.GetJsonAsync($"{Base}/me");
        var user = me.GetProperty("user");
        return (user.GuidProp("id"), user.Str("email"), me.GetProperty("organization").GuidProp("id"));
    }

    public static async Task<List<Guid>> ListIdsAsync(this HttpClient platform, string query)
    {
        var page = await platform.GetJsonAsync($"{PlatformBase}/organizations?{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.GuidProp("tenantId")).ToList();
    }

    /// <summary>Verilen sorgu için satırların adları (sırayla).</summary>
    public static async Task<List<string>> ListNamesAsync(this HttpClient platform, string query)
    {
        var page = await platform.GetJsonAsync($"{PlatformBase}/organizations?{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Str("name")).ToList();
    }

    // ---- veritabanı ----------------------------------------------------------------------------------------------------

    /// <summary>İlk sütunu metin olan satırları okur (<c>payload::text</c>, <c>details::text</c> vb.).</summary>
    public static async Task<List<string>> TextsAsync(this CrmApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString()!);
        }

        return result;
    }

    /// <summary>Platform outbox'ındaki (kiracıya ait) olay yükleri; <paramref name="typeSuffix"/> örn. <c>PlanChanged</c>.</summary>
    public static async Task<List<JsonElement>> PlatformEventsAsync(this CrmApiFactory factory, Guid tenantId, string typeSuffix)
    {
        var payloads = await factory.TextsAsync(
            "SELECT payload::text FROM platform.outbox_messages WHERE tenant_id = @t AND type LIKE @ty ORDER BY occurred_at",
            ("t", tenantId), ("ty", "%." + typeSuffix));
        return payloads.Select(p => JsonDocument.Parse(p).RootElement.Clone()).ToList();
    }

    public static Task<long> AuditCountAsync(this CrmApiFactory factory, Guid tenantId, string? action = null) =>
        factory.ScalarAsync<long>(
            "SELECT count(*) FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND (@a::text IS NULL OR action = @a)",
            ("t", tenantId), ("a", action));

    /// <summary>Platform denetim satırlarının <c>details</c> jsonb değerleri (eskiden yeniye).</summary>
    public static async Task<List<JsonElement>> AuditDetailsAsync(this CrmApiFactory factory, Guid tenantId, string action)
    {
        var rows = await factory.TextsAsync(
            "SELECT details::text FROM platform.platform_audit_entries WHERE target_tenant_id = @t AND action = @a ORDER BY occurred_at, id",
            ("t", tenantId), ("a", action));
        return rows.Select(r => JsonDocument.Parse(r).RootElement.Clone()).ToList();
    }

    /// <summary>Kiracının yalnız Platform tarafında tutulan hesabına <c>usage_snapshots</c> satırı yazar (kullanım serisi/CSV testleri için).</summary>
    public static Task<int> InsertSnapshotAsync(this CrmApiFactory factory, Guid tenantId, DateOnly day, int active, int pending, string metricsJson) =>
        factory.SqlAsync(
            """
            INSERT INTO platform.usage_snapshots (tenant_id, day, users_active, users_pending, metrics, taken_at)
            VALUES (@t, @d, @a, @p, @m::jsonb, now())
            ON CONFLICT (tenant_id, day) DO UPDATE SET users_active = EXCLUDED.users_active, users_pending = EXCLUDED.users_pending, metrics = EXCLUDED.metrics
            """,
            ("t", tenantId), ("d", day), ("a", active), ("p", pending), ("m", metricsJson));

    // ---- CSV ------------------------------------------------------------------------------------------------------------

    /// <summary>RFC 4180 ayrıştırıcı (tırnaklı alan, <c>""</c> kaçışı, tırnak içinde satır sonu, CRLF/LF satır ayırıcı).</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    quoted = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    i++;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    i += 2;
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    i++;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}

/// <summary>
/// Sahte saatli API host'u (aynı veritabanı; <see cref="TimeProvider"/> testin elinde). Oturum açma/jeton işlemleri gerçek saate yakın anda yapılır (jeton doğrulaması
/// gerçek saatle); sonra saat istenen ana alınır. Ek yapılandırma/servis kaydı için <paramref name="configure"/>/<paramref name="services"/> verilir.
/// </summary>
internal sealed class ConsoleClockHost : IAsyncDisposable
{
    public ConsoleClockHost(CrmApiFactory factory, Action<IWebHostBuilder>? configure = null, Action<IServiceCollection>? services = null)
    {
        var now = DateTimeOffset.UtcNow;
        Clock = new TestClock(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero));
        Host = factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(Clock);
                services?.Invoke(s);
            });
        });
    }

    public TestClock Clock { get; }

    public WebApplicationFactory<Program> Host { get; }

    public IServiceProvider Services => Host.Services;

    public HttpClient CreateClient() => Host.CreateClient();

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
