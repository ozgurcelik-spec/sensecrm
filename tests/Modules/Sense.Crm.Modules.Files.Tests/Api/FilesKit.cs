using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sense.Crm.Modules.Files.Infrastructure.Storage;
using Sense.Crm.Modules.Identity.Application.Provisioning;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Files.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Org(HttpClient Admin, Guid TenantId, Guid AdminUserId, string Email);

/// <summary>Yüklenecek bir multipart parçası (<c>FieldName</c> varsayılan <c>file</c>).</summary>
internal sealed record UploadItem(string FileName, byte[] Bytes, string? ContentType = null, string FieldName = "file");

/// <summary>Files HTTP testlerinin ortak yardımcıları.</summary>
internal static class FilesKit
{
    public const string FilesPath = $"{Base}/files";
    public const string PlatformBase = $"{Base}/platform";
    public const string PlatformPassword = "Platform.Sifre.12345";

    public static readonly string[] RecordTypes = ["account", "contact", "lead", "deal", "activity", "case", "quote", "order", "campaign"];

    /// <summary>Kayıt türü → (okuma, yazma) izin anahtarı (plan tablosu; test bağımsız kopyası).</summary>
    public static readonly Dictionary<string, (string Read, string Write)> Permissions = new(StringComparer.Ordinal)
    {
        ["account"] = ("crm.accounts.read", "crm.accounts.write"),
        ["contact"] = ("crm.contacts.read", "crm.contacts.write"),
        ["lead"] = ("crm.leads.read", "crm.leads.write"),
        ["deal"] = ("crm.deals.read", "crm.deals.write"),
        ["activity"] = ("crm.activities.read", "crm.activities.write"),
        ["case"] = ("crm.cases.read", "crm.cases.write"),
        ["quote"] = ("crm.quotes.read", "crm.quotes.write"),
        ["order"] = ("crm.orders.read", "crm.orders.write"),
        ["campaign"] = ("crm.campaigns.read", "crm.campaigns.write"),
    };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Organizasyon / JSON ---------------------------------------------------------------------------------------

    public static async Task<Org> NewOrgAsync(this WebApplicationFactory<Program> host, string name)
    {
        var email = UniqueEmail("admin");
        var client = host.CreateClient();
        var auth = await client.SignUpAsync(name, email);
        client.WithToken(auth.AccessToken);
        var me = await client.GetJsonAsync($"{Base}/me");
        return new Org(client, me.GetProperty("organization").GetProperty("id").GetGuid(), me.GetProperty("user").GetProperty("id").GetGuid(), email);
    }

    public static Guid Id(this JsonElement element) => element.GetProperty("id").GetGuid();

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static bool Has(this JsonElement element, string property) => element.TryGetProperty(property, out _);

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"GET {url}: {body}");
        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<JsonElement> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{method} {url}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<JsonElement> DeleteJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Delete, url, null, expected);

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", body);
        var json = JsonDocument.Parse(body).RootElement.Clone();
        json.Str("code").ShouldBe(code, body);
        return json;
    }

    // ---- Kayıtlar (9 tür) ------------------------------------------------------------------------------------------

    public static async Task<Guid> NewRecordAsync(this HttpClient admin, string recordType, string? tag = null)
    {
        tag ??= Guid.NewGuid().ToString("N")[..6];
        JsonElement created;
        switch (recordType)
        {
            case "account":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/accounts", new { name = $"Firma {tag}" }, HttpStatusCode.Created);
                break;
            case "contact":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/contacts", new { firstName = "Ada", lastName = $"Kisi {tag}" }, HttpStatusCode.Created);
                break;
            case "lead":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = $"Aday {tag}", company = $"Sirket {tag}" }, HttpStatusCode.Created);
                break;
            case "deal":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/deals", new { name = $"Firsat {tag}", accountId = await admin.NewRecordAsync("account", tag), amount = 1000m }, HttpStatusCode.Created);
                break;
            case "activity":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/activities", new { type = "task", subject = $"Gorev {tag}" }, HttpStatusCode.Created);
                break;
            case "case":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/cases", new { subject = $"Destek {tag}" }, HttpStatusCode.Created);
                break;
            case "quote":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/quotes", new { subject = $"Teklif {tag}", accountId = await admin.NewRecordAsync("account", tag), lines = new[] { Line(tag) } }, HttpStatusCode.Created);
                break;
            case "order":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/orders", new { subject = $"Siparis {tag}", accountId = await admin.NewRecordAsync("account", tag), lines = new[] { Line(tag) } }, HttpStatusCode.Created);
                break;
            case "campaign":
                created = await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/campaigns", new { name = $"Kampanya {tag}", type = "email" }, HttpStatusCode.Created);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null);
        }

        return created.Id();
    }

    private static object Line(string tag) => new { description = $"Kalem {tag}", quantity = 1m, unitPrice = 100m, discountPercent = 0m, taxRate = 20m };

    /// <summary>Kaydı yönetici istemcisiyle siler (yumuşak silme).</summary>
    public static Task DeleteRecordAsync(this HttpClient admin, string recordType, Guid id)
    {
        var path = recordType switch
        {
            "account" => "accounts",
            "contact" => "contacts",
            "lead" => "leads",
            "deal" => "deals",
            "activity" => "activities",
            "case" => "cases",
            "quote" => "quotes",
            "order" => "orders",
            "campaign" => "campaigns",
            _ => throw new ArgumentOutOfRangeException(nameof(recordType)),
        };
        return admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/{path}/{id}", null, HttpStatusCode.NoContent);
    }

    // ---- Roller ----------------------------------------------------------------------------------------------------

    public static async Task<Guid> NewRoleAsync(this HttpClient admin, string name, params string[] permissions) =>
        (await admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/roles", new { name = $"{name}-{Guid.NewGuid():N}"[..Math.Min(48, name.Length + 9)], permissions }, HttpStatusCode.Created)).Id();

    public static async Task<NewMember> NewMemberAsync(this CrmApiFactory factory, Org org, string displayName, params string[] permissions)
    {
        var role = await org.Admin.NewRoleAsync(displayName, permissions);
        return await factory.AddMemberAsync(org.Admin, displayName, role);
    }

    // ---- Yükleme / indirme -----------------------------------------------------------------------------------------

    public static MultipartFormDataContent Multipart(params UploadItem[] items)
    {
        var content = new MultipartFormDataContent("crm-test-boundary-" + Guid.NewGuid().ToString("N"));
        foreach (var item in items)
        {
            var part = new ByteArrayContent(item.Bytes);
            if (item.ContentType is not null)
            {
                part.Headers.ContentType = MediaTypeHeaderValue.Parse(item.ContentType);
            }

            content.Add(part, item.FieldName, item.FileName);
        }

        return content;
    }

    public static Task<HttpResponseMessage> UploadAsync(this HttpClient client, string? recordType, Guid? recordId, params UploadItem[] items)
    {
        var url = $"{FilesPath}?recordType={recordType}&recordId={recordId}";
        return client.PostAsync(url, Multipart(items), Ct);
    }

    public static async Task<JsonElement> UploadOkAsync(this HttpClient client, string recordType, Guid recordId, string fileName, byte[] bytes, string? contentType = null)
    {
        using var response = await client.UploadAsync(recordType, recordId, new UploadItem(fileName, bytes, contentType));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        var json = JsonDocument.Parse(body).RootElement.Clone();
        json.GetProperty("failed").GetArrayLength().ShouldBe(0);
        return json.GetProperty("items")[0];
    }

    public static Task<HttpResponseMessage> ContentAsync(this HttpClient client, Guid id, string? disposition = null, string? range = null, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{FilesPath}/{id}/content" + (disposition is null ? string.Empty : $"?disposition={disposition}"));
        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return client.SendAsync(request, Ct);
    }

    public static async Task<JsonElement> ListAsync(this HttpClient client, string recordType, Guid recordId, string query = "", HttpStatusCode expected = HttpStatusCode.OK) =>
        await client.GetJsonAsync($"{FilesPath}?recordType={recordType}&recordId={recordId}{query}", expected);

    // ---- SQL / host ------------------------------------------------------------------------------------------------

    public static async Task<int> SqlAsync(this CrmApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task<T> ScalarAsync<T>(this CrmApiFactory factory, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static InMemoryObjectStore Store(this WebApplicationFactory<Program> host) => host.Services.GetRequiredService<InMemoryObjectStore>();

    /// <summary>Kiracının nesne deposundaki anahtarları (ham).</summary>
    public static List<string> ObjectKeys(this WebApplicationFactory<Program> host, Guid tenantId) =>
        host.Store().Keys.Where(k => k.StartsWith(tenantId.ToString("D") + "/", StringComparison.Ordinal)).ToList();

    // ---- Platform (plan / abonelik) --------------------------------------------------------------------------------

    public static async Task<HttpClient> PlatformAdminAsync(this WebApplicationFactory<Program> host)
    {
        var email = UniqueEmail("platform");
        using (var scope = host.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapper>()
                .EnsureAsync(email, PlatformPassword, "Platform Yonetici", organizationName: null, Ct);
            result.Outcome.ShouldBe(PlatformAdminOutcome.Created);
        }

        var client = host.CreateClient();
        return client.WithToken((await client.LoginAsync(email, PlatformPassword)).AccessToken);
    }

    public static string AllModulesOn => """{"workflows":true,"commerce":true,"service":true,"marketing":true}""";

    public static string AllModulesOff => """{"workflows":false,"commerce":false,"service":false,"marketing":false}""";

    /// <summary>Test planı (idempotent): <c>maxStorageMb</c> sonlu ya da <c>null</c> (sınırsız).</summary>
    public static async Task<string> EnsurePlanAsync(this CrmApiFactory factory, string prefix, int? maxStorageMb, string? modulesJson = null)
    {
        var code = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(31, prefix.Length + 9)];
        var storage = maxStorageMb is null ? "null" : maxStorageMb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var limits = "{\"maxUsers\":null,\"maxStorageMb\":" + storage + ",\"maxRecords\":{}}";
        await factory.SqlAsync(
            """
            INSERT INTO platform.plans (code, name, is_active, sort_order, trial_days, limits, modules, created_at)
            VALUES (@code, @code, TRUE, 900, NULL, @limits::jsonb, @modules::jsonb, now())
            """,
            ("code", code), ("limits", limits), ("modules", modulesJson ?? AllModulesOn));
        return code;
    }

    public static Task<JsonElement> PutSubscriptionAsync(this HttpClient platform, Guid tenantId, string planCode, object? overrides = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        platform.SendJsonAsync(HttpMethod.Put, $"{PlatformBase}/organizations/{tenantId}/subscription", new { planCode, overrides }, expected);

    /// <summary>Tüm modül outbox'larını boşaltır (olay zincirleri tamamlansın).</summary>
    public static async Task DrainOutboxesAsync(this WebApplicationFactory<Program> host)
    {
        Type[] contexts;
        using (var scope = host.Services.CreateScope())
        {
            contexts = scope.ServiceProvider.GetServices<ModuleDbContext>().Select(c => c.GetType()).Distinct().ToArray();
        }

        for (var round = 0; round < 5; round++)
        {
            var processed = 0;
            foreach (var context in contexts)
            {
                using var scope = host.Services.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService(typeof(OutboxProcessor<>).MakeGenericType(context));
                var method = processor.GetType().GetMethod(nameof(OutboxProcessor<ModuleDbContext>.ProcessAsync))!;
                processed += await (Task<int>)method.Invoke(processor, [Ct])!;
            }

            if (processed == 0)
            {
                return;
            }
        }
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
