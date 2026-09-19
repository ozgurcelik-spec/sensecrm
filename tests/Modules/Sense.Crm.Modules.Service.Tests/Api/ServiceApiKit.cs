using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Service.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Org(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminName);

/// <summary>Elle yönetilen saat: <see cref="SetUtcNow"/> ileri VE geri alınabilir (FakeTimeProvider geri almayı reddeder).</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetUtcNow(DateTimeOffset value) => _now = value;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>Sahte saatli API host'u: aynı veritabanı, <see cref="TimeProvider"/> testin elinde.</summary>
internal sealed class ClockedHost : IDisposable
{
    private readonly WebApplicationFactory<Program> _host;

    public ClockedHost(CrmApiFactory factory, DateTimeOffset? start = null)
    {
        // Oturum açma/jeton işlemleri gerçek saate yakın bir anda yapılmalıdır (jeton doğrulaması gerçek saatle); sonra saat ileri/geri alınabilir.
        var now = DateTimeOffset.UtcNow;
        Clock = new TestClock(start ?? new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero));
        _host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        }));
    }

    public TestClock Clock { get; }

    public IServiceProvider Services => _host.Services;

    public HttpClient CreateClient() => _host.CreateClient();

    /// <summary>Yeni organizasyon açar: kayıt anındaki saat gerçek saate yakın kalır, ardından sahte saat <paramref name="then"/>'e alınır.</summary>
    public async Task<Org> NewOrgAsync(string name, DateTimeOffset? then = null, string locale = "tr")
    {
        var client = CreateClient();
        var auth = await client.SignUpAsync(name, UniqueEmail("admin"), locale);
        client.WithToken(auth.AccessToken);
        var me = await client.GetJsonAsync($"{Base}/me");
        if (then is { } at)
        {
            Clock.SetUtcNow(at);
        }

        return new Org(
            client,
            me.GetProperty("organization").GetProperty("id").GetGuid(),
            me.GetProperty("user").GetProperty("id").GetGuid(),
            me.GetProperty("user").GetProperty("displayName").GetString()!);
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>Entegrasyon testlerinde tekrar eden HTTP/DB yardımcıları.</summary>
internal static class ServiceApiKit
{
    public const string CasesPath = $"{Base}/cases";
    public const string SlaPath = $"{Base}/service/sla-policies";
    public const string ReportsPath = $"{Base}/reports/service";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Org> NewOrgAsync(this CrmApiFactory factory, string name, string locale = "tr")
    {
        var client = factory.CreateClient();
        var auth = await client.SignUpAsync(name, UniqueEmail("admin"), locale);
        client.WithToken(auth.AccessToken);
        var me = await client.GetJsonAsync($"{Base}/me");
        return new Org(
            client,
            me.GetProperty("organization").GetProperty("id").GetGuid(),
            me.GetProperty("user").GetProperty("id").GetGuid(),
            me.GetProperty("user").GetProperty("displayName").GetString()!);
    }

    public static Guid Id(this JsonElement element) => element.GetProperty("id").GetGuid();

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static bool Has(this JsonElement element, string property) => element.TryGetProperty(property, out _);

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<JsonElement> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<JsonElement> PostJsonAsync(this HttpClient client, string url, object? body, HttpStatusCode expected = HttpStatusCode.Created) =>
        client.SendJsonAsync(HttpMethod.Post, url, body, expected);

    public static Task<JsonElement> PutJsonAsync(this HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Put, url, body, expected);

    public static Task<JsonElement> DeleteJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.SendJsonAsync(HttpMethod.Delete, url, null, expected);

    /// <summary>Gövdeyi anonim nesne yerine sözlükten kurar (kısmi alanlar; null'lar dahil edilmez).</summary>
    public static Dictionary<string, object?> Body(object fields)
    {
        var body = new Dictionary<string, object?>();
        foreach (var property in fields.GetType().GetProperties())
        {
            body[property.Name] = property.GetValue(fields);
        }

        return body;
    }

    public static Task<JsonElement> CreateCaseAsync(this HttpClient client, string subject, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["subject"] = subject };
        if (extra is not null)
        {
            foreach (var (key, value) in Body(extra))
            {
                body[key] = value;
            }
        }

        return client.PostJsonAsync(CasesPath, body);
    }

    public static async Task<JsonElement> CreateAccountAsync(this HttpClient client, string name) =>
        await client.PostJsonAsync($"{Base}/accounts", new { name });

    public static async Task<JsonElement> CreateContactAsync(this HttpClient client, string lastName, Guid? accountId = null) =>
        await client.PostJsonAsync($"{Base}/contacts", Body(new { firstName = "Test", lastName, accountId }));

    public static Task<JsonElement> GetCaseAsync(this HttpClient client, Guid id) => client.GetJsonAsync($"{CasesPath}/{id}");

    public static Task<JsonElement> SetStatusAsync(this HttpClient client, Guid id, string status, string? note = null, HttpStatusCode expected = HttpStatusCode.NoContent) =>
        client.PostJsonAsync($"{CasesPath}/{id}/status", Body(new { status, resolutionNote = note }), expected);

    public static Task<JsonElement> CommentAsync(this HttpClient client, Guid id, string visibility, string body, HttpStatusCode expected = HttpStatusCode.Created) =>
        client.PostJsonAsync($"{CasesPath}/{id}/comments", new { visibility, body }, expected);

    public static async Task<List<JsonElement>> TimelineAsync(this HttpClient client, Guid id, string query = "")
    {
        var page = await client.GetJsonAsync($"{CasesPath}/{id}/timeline{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToList();
    }

    public static async Task<List<Guid>> ListIdsAsync(this HttpClient client, string query = "")
    {
        var page = await client.GetJsonAsync($"{CasesPath}{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Id()).ToList();
    }

    public static async Task<List<string>> ListNumbersAsync(this HttpClient client, string query = "")
    {
        var page = await client.GetJsonAsync($"{CasesPath}{query}");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Str("number")).ToList();
    }

    /// <summary>Verilen izinleri taşıyan yeni bir özel rol + o role sahip yeni üye açar; üye olarak oturum açmış istemciyi döner.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> AddMemberAsync(this CrmApiFactory factory, Org org, string displayName, params string[] permissions)
    {
        var role = await org.Admin.PostJsonAsync($"{Base}/organization/roles", new { name = "Role " + Guid.NewGuid().ToString("N")[..8], permissions });
        var email = UniqueEmail("member");
        var member = await org.Admin.PostJsonAsync($"{Base}/organization/members", new { email, displayName, roleId = role.Id() });
        var temporary = member.GetProperty("temporaryPassword").GetString()!;
        var client = factory.CreateClient();
        await client.ActivateAsync(email, temporary);
        return (client, member.GetProperty("userId").GetGuid());
    }

    /// <summary>Aynı sözleşmeyle (yalnız istemci) — sahte saatli host üzerinden üye ekler.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> AddMemberAsync(this ClockedHost host, Org org, string displayName, params string[] permissions)
    {
        var role = await org.Admin.PostJsonAsync($"{Base}/organization/roles", new { name = "Role " + Guid.NewGuid().ToString("N")[..8], permissions });
        var email = UniqueEmail("member");
        var member = await org.Admin.PostJsonAsync($"{Base}/organization/members", new { email, displayName, roleId = role.Id() });
        var temporary = member.GetProperty("temporaryPassword").GetString()!;
        var client = host.CreateClient();

        // Jeton doğrulaması gerçek saatle yapılır: oturum, sahte saat gerçek saate alınarak açılır, sonra saat eski değerine döner.
        var saved = host.Clock.GetUtcNow();
        host.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        try
        {
            await client.ActivateAsync(email, temporary);
        }
        finally
        {
            host.Clock.SetUtcNow(saved);
        }

        return (client, member.GetProperty("userId").GetGuid());
    }

    public static async Task ExecuteAsync(this CrmApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    public static async Task<T> ScalarAsync<T>(this CrmApiFactory factory, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(Ct);
        return (T)Convert.ChangeType(result!, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<OutboxMessage>> OutboxMessagesAsync<TContext>(this CrmApiFactory factory, string type, string payloadContains)
        where TContext : ModuleDbContext
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var messages = await db.OutboxMessages.AsNoTracking().Where(m => m.Type == type).ToListAsync(Ct);
        return messages.Where(m => m.Payload.Contains(payloadContains, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Worker'ın yaptığı işi taklit eder: modülün outbox'ını boşalana kadar işler.</summary>
    public static async Task DrainOutboxAsync<TContext>(this CrmApiFactory factory)
        where TContext : ModuleDbContext
    {
        for (var i = 0; i < 50; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<OutboxProcessor<TContext>>().ProcessAsync(Ct) == 0)
            {
                return;
            }
        }
    }

    public static async Task<ServiceDbContextScope> ServiceDbAsync(this CrmApiFactory factory)
    {
        await Task.CompletedTask;
        return new ServiceDbContextScope(factory.Services.CreateScope());
    }

    public static async Task ShouldBeValidationErrorAsync(this HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe("validation", body);
        json.RootElement.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(body);
    }

    public static string Iso(this DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTime Utc(this JsonElement element, string property) => element.GetProperty(property).GetDateTime().ToUniversalTime();
}

/// <summary>Testin doğrudan <see cref="ServiceDbContext"/> okuması için kapsam (kiracı filtresi atlanarak).</summary>
internal sealed class ServiceDbContextScope(IServiceScope scope) : IDisposable
{
    public ServiceDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();

    public void Dispose() => scope.Dispose();
}
