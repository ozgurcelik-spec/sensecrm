using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Respawn;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;
using Sense.Crm.Tests.Shared.Workflows;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Sense.Crm.Tests.Shared.Fixtures;

/// <summary>
/// Entegrasyon testi altyapısı (K15): gerçek PostgreSQL (Testcontainers, postgres:17-alpine) + gerçek API host'u
/// (WebApplicationFactory&lt;Program&gt;, ortam "Testing"). Başlangıçta Migrator'ın kullandığı yolla tüm migration'lar
/// uygulanır; <see cref="ResetDatabaseAsync"/> Respawn ile şemaları boşaltır. Bir test koleksiyonu tek container paylaşır.
/// </summary>
public sealed class CrmApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestingEnvironment = "Testing";
    private static readonly string[] Schemas = ["identity", "sales", "activities", "workflows", "marketing", "commerce", "service", "platform", AuditDbContext.SchemaName];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("crm_test")
        .WithUsername("crm")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    private Respawner? _respawner;

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Host'u oluşturur (Program.cs çalışır) ve tüm şemaları migrate eder.
        await MigrationRunner.MigrateAllAsync(Services, NullLogger.Instance);

        // M7: test hostları da plan kataloğuna ihtiyaç duyar (varsayılan kayıt planı internal, aşağıda); Migrator'ın yaptığı senkron burada aynı kodla çalışır.
        // platform.plans Respawn'dan hariçtir (kayıtlar testler arasında kalır); kiracı hesapları sıfırlanır.
        using (var scope = Services.CreateScope())
        {
            var sync = await scope.ServiceProvider.GetRequiredService<PlanSynchronizer>().SyncAsync(default);
            sync.Succeeded.ShouldBeTrue(string.Join("; ", sync.Errors));
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = Schemas,
            TablesToIgnore = [new Respawn.Graph.Table("__ef_migrations_history"), new Respawn.Graph.Table("platform", "plans")],
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // C-SEC2 M3: denetim tabloları salt-eklemeli tetikleyicilerle korunur; test sıfırlaması (Respawn DELETE) yalnız süper kullanıcı oturumunda geçerli "superuser" işaretiyle yapılır.
        await using (var marker = new NpgsqlCommand("SELECT set_config('crm.audit_maintenance', 'superuser', false)", connection))
        {
            await marker.ExecuteNonQueryAsync();
        }

        await _respawner!.ResetAsync(connection);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestingEnvironment);
        builder.UseSetting("ConnectionStrings:Database", ConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", string.Empty);
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:LoginEmail:PermitLimit", "100000");
        builder.UseSetting("ProblemDetails:IncludeExceptionDetails", "true");

        // M7: mevcut testler plan kısıtlarından etkilenmemeli; varsayılan kayıt/açılış planı internal (limitsiz, tüm modüller açık, denemesiz).
        // Yaşam döngüsü testleri kendi Platform:* ayarını WithWebHostBuilder ile verir.
        builder.UseSetting("Platform:Signup:PlanCode", "internal");
        builder.UseSetting("Platform:Provisioning:DefaultPlanCode", "internal");
        builder.ConfigureServices(services =>
        {
            // Workflow motoru: gerçek Conductor yerine gerçek tanım + görev işleyicilerini çalıştıran sahte motor (tüm test projeleri).
            services.RemoveAll<IWorkflowEngine>();
            services.RemoveAll<IWorkflowDefinitionRegistrar>();
            services.AddSingleton<FakeWorkflowEngine>();
            services.AddSingleton<IWorkflowEngine>(sp => sp.GetRequiredService<FakeWorkflowEngine>());
            services.AddSingleton<IWorkflowDefinitionRegistrar>(sp => sp.GetRequiredService<FakeWorkflowEngine>());
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, FakeEngineDrainStartupFilter>();

            // Ortak audit şemasının sahibi context (API'de kayıtlı değil; migration için).
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = ConnectionString })
                .Build();
            services.AddAuditStore(configuration);
        });
    }
}

/// <summary>Testlerde <see cref="ApiTestClient.AddMemberAsync"/> ile açılmış üye: kimlik, e-posta, yetkili istemci ve güncel oturum.</summary>
public sealed record NewMember(Guid UserId, string Email, HttpClient Client, AuthResponse Auth);

/// <summary>Testlerde tekrar eden HTTP akışları.</summary>
public static class ApiTestClient
{
    public const string DefaultPassword = "Sifre.12345";
    public const string Base = "/api/v1";

    public static string UniqueEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@example.com";

    public static async Task<AuthResponse> SignUpAsync(this HttpClient client, string organizationName, string email, string locale = "tr")
    {
        var response = await client.PostAsJsonAsync($"{Base}/auth/signup", new
        {
            organizationName,
            displayName = "Test " + organizationName,
            email,
            password = DefaultPassword,
            locale,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    public static async Task<AuthResponse> LoginAsync(this HttpClient client, string email, string password = DefaultPassword)
    {
        var response = await client.PostAsJsonAsync($"{Base}/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>
    /// Yönetici istemcisiyle yeni hesaplı üye ekler (H4): sunucu geçici parola üretir (yanıtta <c>temporaryPassword</c>); test bu parolayla
    /// giriş yapıp parolayı <paramref name="password"/> (varsayılan <see cref="DefaultPassword"/>) olarak değiştirir (geçici parola
    /// zorlaması). Dönen istemci, değiştirilmiş parolayla açılan güncel oturumla yetkilidir.
    /// </summary>
    public static async Task<NewMember> AddMemberAsync(
        this CrmApiFactory factory, HttpClient adminClient, string displayName, Guid roleId, string? email = null, string password = DefaultPassword)
    {
        email ??= UniqueEmail("member");
        var added = await adminClient.PostAsJsonAsync($"{Base}/organization/members", new { email, displayName, roleId });
        var body = await added.Content.ReadAsStringAsync();
        added.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, body);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        var userId = json.RootElement.GetProperty("userId").GetGuid();
        var temporary = json.RootElement.GetProperty("temporaryPassword").GetString()!;

        var client = factory.CreateClient();
        var first = await client.LoginAsync(email, temporary);
        first.MustChangePassword.ShouldBeTrue();
        client.WithToken(first.AccessToken);
        var changed = await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = temporary, newPassword = password });
        changed.EnsureSuccessStatusCode();
        var auth = (await changed.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.WithToken(auth.AccessToken);
        return new NewMember(userId, email, client, auth);
    }

    /// <summary>Geçici parolayla ilk girişi yapar, zorunlu parola değişimini tamamlar (parola <see cref="DefaultPassword"/>) ve istemciyi güncel jetonla yetkilendirir.</summary>
    public static async Task<AuthResponse> ActivateAsync(this HttpClient client, string email, string temporaryPassword, string password = DefaultPassword)
    {
        var first = await client.LoginAsync(email, temporaryPassword);
        client.WithToken(first.AccessToken);
        var changed = await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = temporaryPassword, newPassword = password });
        changed.EnsureSuccessStatusCode();
        var auth = (await changed.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.WithToken(auth.AccessToken);
        return auth;
    }

    public static HttpClient WithToken(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>ProblemDetails yanıtının `code` uzantısını okur ve beklenen durum koduyla birlikte doğrular.</summary>
    public static async Task ShouldBeProblemAsync(this HttpResponseMessage response, System.Net.HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var json = System.Text.Json.JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().ShouldBe(code, body);
    }
}
