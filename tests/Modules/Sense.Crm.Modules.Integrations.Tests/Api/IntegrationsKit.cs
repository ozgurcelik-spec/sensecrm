using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sense.Crm.Modules.Identity.Application.Provisioning;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Infrastructure;
using Sense.Crm.Modules.Integrations.Infrastructure.Delivery;
using Sense.Crm.Modules.Integrations.Tests.Unit;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Integrations.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record Tenant(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminEmail);

/// <summary>Sahte taşıyıcı: çağrıları kaydeder, sıradaki sonucu (varsayılan 200) döner; bloklayıcı mod eşzamanlılık testleri içindir.</summary>
internal sealed class FakeTransport : IWebhookTransport
{
    private readonly object _gate = new();
    private int _inFlight;

    public List<TransportRequest> Calls { get; } = [];

    public Func<TransportRequest, TransportResult> Responder { get; set; } = _ => Ok();

    public TimeSpan Delay { get; set; }

    public int MaxObservedConcurrency { get; private set; }

    public static TransportResult Ok(int status = 200, byte[]? body = null) => new(TransportStatus.Ok, status, body ?? [], null, TimeSpan.FromMilliseconds(3), null);

    public async Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add(request);
            _inFlight++;
            MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _inFlight);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            return Responder(request);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }

    public string Header(TransportRequest request, string name) => request.Headers[name];
}

/// <summary>Sahte çözümleyici: ana bilgisayar başına sıradaki cevap (kuyruk boşsa son cevap).</summary>
internal sealed class FakeDns : IDnsResolver
{
    private readonly Dictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPAddress[]> _last = new(StringComparer.Ordinal);

    public int Calls { get; private set; }

    public IPAddress[] Default { get; set; } = [IPAddress.Parse("93.184.216.34")];

    public void Enqueue(string host, params IPAddress[] addresses)
    {
        if (!_answers.TryGetValue(host, out var queue))
        {
            queue = new Queue<IPAddress[]>();
            _answers[host] = queue;
        }

        queue.Enqueue(addresses);
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        Calls++;
        if (_answers.TryGetValue(host, out var queue) && queue.Count > 0)
        {
            _last[host] = queue.Dequeue();
        }

        return Task.FromResult<IReadOnlyList<IPAddress>>(_last.TryGetValue(host, out var answer) ? answer : Default);
    }
}

/// <summary>Günlük yakalayıcı: sır sızıntısı testleri (ham anahtar/whsec günlükte olmamalı).</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines)
            {
                owner.Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
            }
        }
    }
}

/// <summary>Test host'u: sahte taşıyıcı/çözümleyici/saat/titreşim, test IP'si (X-Test-Ip), günlük yakalayıcı ve isteğe bağlı ek ayarlar.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private TestHost(WebApplicationFactory<Program> factory, TestClock clock, FakeTransport transport, FakeDns dns, CapturingLoggerProvider logs)
    {
        Factory = factory;
        Clock = clock;
        Transport = transport;
        Dns = dns;
        Logs = logs;
    }

    public WebApplicationFactory<Program> Factory { get; }

    public TestClock Clock { get; }

    public FakeTransport Transport { get; }

    public FakeDns Dns { get; }

    public CapturingLoggerProvider Logs { get; }

    public IServiceProvider Services => Factory.Services;

    public static TestHost Create(CrmApiFactory root, params (string Key, string Value)[] settings)
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var transport = new FakeTransport();
        var dns = new FakeDns();
        var logs = new CapturingLoggerProvider();
        var factory = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Integrations:ApiKeys:CacheSeconds", "0");

            // Varsayilan test host'unda eszamanlilik kotasi tetiklenmesin (adalet testleri kendi degerlerini verir).
            builder.UseSetting("Integrations:Webhooks:MaxConcurrentPerHost", "50");
            builder.UseSetting("Integrations:Webhooks:MaxConcurrentPerTenant", "50");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(l => l.AddProvider(logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddIntegrationsWorkerServices();
                services.RemoveAll<IWebhookTransport>();
                services.AddSingleton<IWebhookTransport>(transport);
                services.RemoveAll<IDnsResolver>();
                services.AddSingleton<IDnsResolver>(dns);
                services.RemoveAll<IJitter>();
                services.AddSingleton<IJitter>(new FixedJitter());
                services.AddSingleton<IStartupFilter, TestIpStartupFilter>();
            });
        });

        // Onceki testlerden kalan (baska kiracilarin) kuyruk satirlari bu host'un sahte tasiyicisina karismasin.
        root.SqlAsync("DELETE FROM integrations.delivery_queue").GetAwaiter().GetResult();
        return new TestHost(factory, clock, transport, dns, logs);
    }

    public ValueTask DisposeAsync() => Factory.DisposeAsync();

    public async Task<int> RunDispatcherAsync()
    {
        var total = 0;
        for (var i = 0; i < 20; i++)
        {
            var n = await Factory.Services.GetRequiredService<WebhookDispatcher>().RunOnceAsync(Kit.Ct);
            total += n;
            if (n == 0)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Tek tur (kota/adalet testleri için).</summary>
    public Task<int> RunDispatcherOnceAsync() => Factory.Services.GetRequiredService<WebhookDispatcher>().RunOnceAsync(Kit.Ct);

    private sealed class TestIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Ip", out var ip) && IPAddress.TryParse(ip.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await pipeline();
            });
            next(app);
        };
    }
}

internal static class Kit
{
    public const string Wh = Base + "/integrations/webhooks";
    public const string Keys = Base + "/integrations/api-keys";
    public const string Deliveries = Base + "/integrations/deliveries";
    public const string PlatformBase = Base + "/platform";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<HttpClient> PlatformAdminAsync(this WebApplicationFactory<Program> host)
    {
        var email = UniqueEmail("platform");
        using (var scope = host.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapper>().EnsureAsync(email, "Platform.Sifre.12345", "Platform Yonetici", organizationName: null, Ct);
            result.Outcome.ShouldBe(PlatformAdminOutcome.Created);
        }

        var client = host.CreateClient();
        return client.WithToken((await client.LoginAsync(email, "Platform.Sifre.12345")).AccessToken);
    }

    public static async Task<Tenant> NewTenantAsync(this WebApplicationFactory<Program> host, string name)
    {
        var email = UniqueEmail("admin");
        var client = host.CreateClient();
        var auth = await client.SignUpAsync(name, email);
        client.WithToken(auth.AccessToken);
        var me = await client.GetJsonAsync($"{Base}/me");
        return new Tenant(client, me.GetProperty("organization").GetProperty("id").GetGuid(), me.GetProperty("user").GetProperty("id").GetGuid(), email);
    }

    public static async Task DrainOutboxesAsync(this WebApplicationFactory<Program> host)
    {
        Type[] contexts;
        using (var scope = host.Services.CreateScope())
        {
            contexts = [.. scope.ServiceProvider.GetServices<ModuleDbContext>().Select(c => c.GetType()).Distinct()];
        }

        for (var round = 0; round < 6; round++)
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

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, body);
        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> SendAsync(this HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, string.IsNullOrEmpty(text) || !text.TrimStart().StartsWith('{') && !text.TrimStart().StartsWith('[') ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static async Task<JsonElement> SendOkAsync(this HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        var (response, json) = await client.SendAsync(method, url, body);
        response.StatusCode.ShouldBe(expected, json.ValueKind == JsonValueKind.Undefined ? string.Empty : json.ToString());
        return json;
    }

    public static async Task ShouldBeCodeAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe(code, body);
    }

    public static Guid GuidProp(this JsonElement element, string property) => element.GetProperty(property).GetGuid();

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

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

    public static async Task EnsurePlanAsync(this CrmApiFactory factory, string code, string limitsJson, string modulesJson)
    {
        await factory.SqlAsync(
            """
            INSERT INTO platform.plans (code, name, is_active, sort_order, trial_days, limits, modules, created_at)
            VALUES (@code, @code, TRUE, 900, NULL, @limits::jsonb, @modules::jsonb, now())
            ON CONFLICT (code) DO UPDATE SET limits = EXCLUDED.limits, modules = EXCLUDED.modules, is_active = TRUE
            """,
            ("code", code), ("limits", limitsJson), ("modules", modulesJson));
    }

    public static Task PutSubscriptionAsync(this HttpClient platform, Guid tenantId, string planCode) =>
        platform.SendOkAsync(HttpMethod.Put, $"{PlatformBase}/organizations/{tenantId}/subscription", new { planCode }, HttpStatusCode.OK);

    public static async Task<(Tenant Tenant, HttpClient Platform)> TenantOnPlanAsync(this WebApplicationFactory<Program> host, string name, string planCode)
    {
        var tenant = await host.NewTenantAsync(name);
        await host.DrainOutboxesAsync();
        var platform = await host.PlatformAdminAsync();
        await platform.PutSubscriptionAsync(tenant.TenantId, planCode);
        return (tenant, platform);
    }

    public static string AllModulesOn => """{"workflows":true,"commerce":true,"service":true,"marketing":true,"integrations":true}""";

    public static Task<Guid> RoleIdAsync(this HttpClient admin, string name) =>
        admin.GetJsonAsync($"{Base}/organization/roles").ContinueWith(t => t.Result.EnumerateArray().Single(r => r.Str("name") == name).GuidProp("id"), TaskScheduler.Default);

    public static async Task<Guid> CreateRoleAsync(this HttpClient admin, string name, params string[] permissions)
    {
        var json = await admin.SendOkAsync(HttpMethod.Post, $"{Base}/organization/roles", new { name, permissions }, HttpStatusCode.Created);
        return json.GuidProp("id");
    }

    public static async Task<Guid> CreateLeadAsync(this HttpClient client)
    {
        var response = await client.PostAsJsonAsync($"{Base}/leads", new { lastName = "Aday" + System.Guid.NewGuid().ToString("N")[..4], company = "Sirket" }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GuidProp("id");
    }

    /// <summary>Yönetici istemcisiyle anahtar oluşturur; (kimlik, ham anahtar, önek) döner.</summary>
    public static async Task<(Guid Id, string Key, string Prefix)> CreateKeyAsync(
        this HttpClient admin, string name, string[]? scopes = null, DateTime? expiresAt = null, string[]? cidrs = null)
    {
        var (response, json) = await admin.SendAsync(HttpMethod.Post, Keys, new { name, scopes = scopes ?? ["crm.leads.read"], expiresAt, allowedCidrs = cidrs });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, json.ToString());
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        return (json.GuidProp("id"), json.Str("key"), json.Str("prefix"));
    }

    /// <summary>Verilen ham anahtarla yetkili istemci (yalnız Bearer; isteğe bağlı test IP'si).</summary>
    public static HttpClient KeyClient(this WebApplicationFactory<Program> host, string key, string? ip = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        if (ip is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        }

        return client;
    }

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
