using System.Net.Http.Headers;
using System.Net.Http.Json;
using Crm.Modules.Identity.Application;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Crm.Shared.Infrastructure.Persistence.Audit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Respawn;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Crm.Tests.Shared.Fixtures;

/// <summary>
/// Entegrasyon testi altyapısı (K15): gerçek PostgreSQL (Testcontainers, postgres:17-alpine) + gerçek API host'u
/// (WebApplicationFactory&lt;Program&gt;, ortam "Testing"). Başlangıçta Migrator'ın kullandığı yolla tüm migration'lar
/// uygulanır; <see cref="ResetDatabaseAsync"/> Respawn ile şemaları boşaltır. Bir test koleksiyonu tek container paylaşır.
/// </summary>
public sealed class CrmApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestingEnvironment = "Testing";
    private static readonly string[] Schemas = ["identity", AuditDbContext.SchemaName];

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

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = Schemas,
            TablesToIgnore = [new Respawn.Graph.Table("__ef_migrations_history")],
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
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
        builder.UseSetting("ProblemDetails:IncludeExceptionDetails", "true");
        builder.ConfigureServices(services =>
        {
            // Ortak audit şemasının sahibi context (API'de kayıtlı değil; migration için).
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = ConnectionString })
                .Build();
            services.AddAuditStore(configuration);
        });
    }
}

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
