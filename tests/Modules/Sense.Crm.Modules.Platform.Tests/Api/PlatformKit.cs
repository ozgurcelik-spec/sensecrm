using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sense.Crm.Modules.Identity.Application.Provisioning;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>Tüm HTTP entegrasyon testleri tek PostgreSQL container'ı ve tek API host'unu paylaşır (sıralı çalışır).</summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CrmApiFactory>
{
    public const string Name = "Api";
}

/// <summary>Yeni bir organizasyon (kayıt) ve yöneticisi.</summary>
internal sealed record TestOrg(HttpClient Admin, Guid TenantId, Guid AdminUserId, string AdminEmail, string Name);

/// <summary>
/// Platform testlerinin ortak yardımcıları. Test ortamı varsayılanı (bkz. <c>CrmApiFactory</c>): kayıt/açılış planı <c>internal</c> (limitsiz, tüm modüller açık, denemesiz);
/// yaşam döngüsü/limit testleri kendi planlarını <see cref="EnsurePlanAsync"/> ile <c>platform.plans</c>'a ekler ve <c>PUT /platform/organizations/{id}/subscription</c> ile atar.
/// Outbox testlerde kendiliğinden boşalmaz: <see cref="DrainOutboxesAsync"/> ile elle boşaltılır.
/// </summary>
internal static class PlatformKit
{
    public const string PlatformPassword = "Platform.Sifre.12345";
    public const string PlatformBase = $"{Base}/platform";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Migrator <c>create-platform-admin</c> ile aynı mantıkla yeni bir platform yöneticisi açar ve oturum açmış istemciyi döner.</summary>
    public static async Task<HttpClient> PlatformAdminAsync(this WebApplicationFactory<Program> host)
    {
        var email = UniqueEmail("platform");
        using (var scope = host.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapper>()
                .EnsureAsync(email, PlatformPassword, "Platform Yönetici", organizationName: null, Ct);
            result.Outcome.ShouldBe(PlatformAdminOutcome.Created);
        }

        var client = host.CreateClient();
        return client.WithToken((await client.LoginAsync(email, PlatformPassword)).AccessToken);
    }

    /// <summary>Kendi kendine kayıt (test ortamında açık) ile yeni organizasyon ve yöneticisi.</summary>
    public static async Task<TestOrg> NewOrgAsync(this WebApplicationFactory<Program> host, string name)
    {
        var email = UniqueEmail("admin");
        var client = host.CreateClient();
        var auth = await client.SignUpAsync(name, email);
        client.WithToken(auth.AccessToken);
        var me = await client.GetJsonAsync($"{Base}/me");
        return new TestOrg(
            client,
            me.GetProperty("organization").GetProperty("id").GetGuid(),
            me.GetProperty("user").GetProperty("id").GetGuid(),
            email,
            name);
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.GetAsync(url, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, body);
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
        response.StatusCode.ShouldBe(expected, text);
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Platform yöneticisi ile abonelik atar (plan/deneme/istisna; tam ve kalıcı değiştirme).</summary>
    public static Task<JsonElement> PutSubscriptionAsync(this HttpClient platform, Guid tenantId, string planCode, string? trialEndsOn = null, object? overrides = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        platform.SendJsonAsync(HttpMethod.Put, $"{PlatformBase}/organizations/{tenantId}/subscription", new { planCode, trialEndsOn, overrides }, expected);

    /// <summary>Test planı ekler (idempotent): <paramref name="limitsJson"/> ve <paramref name="modulesJson"/> ham JSON.</summary>
    public static async Task EnsurePlanAsync(this CrmApiFactory factory, string code, string limitsJson, string modulesJson, int? trialDays = null)
    {
        await factory.SqlAsync(
            """
            INSERT INTO platform.plans (code, name, is_active, sort_order, trial_days, limits, modules, created_at)
            VALUES (@code, @code, TRUE, 900, @trial, @limits::jsonb, @modules::jsonb, now())
            ON CONFLICT (code) DO UPDATE SET limits = EXCLUDED.limits, modules = EXCLUDED.modules, trial_days = EXCLUDED.trial_days, is_active = TRUE
            """,
            ("code", code), ("trial", (object?)trialDays ?? DBNull.Value), ("limits", limitsJson), ("modules", modulesJson));
    }

    public static string AllModulesOn => """{"workflows":true,"commerce":true,"service":true,"marketing":true}""";

    public static string AllModulesOff => """{"workflows":false,"commerce":false,"service":false,"marketing":false}""";

    /// <summary>Ham SQL (parametreli); etkilenen satır sayısını döner.</summary>
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

    /// <summary>SQL ile hesap alanını değiştirir ve varlık önbelleğini geçersiz kılar (senaryo kurulumu; komut yolunu sınamayan testler için).</summary>
    public static async Task SetAccountAsync(this WebApplicationFactory<Program> host, CrmApiFactory factory, Guid tenantId, string setClause, params (string Name, object? Value)[] parameters)
    {
        var all = parameters.Append(("tid", tenantId)).ToArray();
        (await factory.SqlAsync($"UPDATE platform.tenant_accounts SET {setClause} WHERE tenant_id = @tid", all)).ShouldBe(1);
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEntitlementCache>().InvalidateAsync(tenantId, Ct);
    }

    /// <summary>Tüm modül outbox'larını (Identity → Platform → …) boşaltır: <c>OrganizationCreated</c> → Platform hesabı gibi olay zincirleri tamamlanır.</summary>
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

    public static Guid GuidProp(this JsonElement element, string property) => element.GetProperty(property).GetGuid();

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;
}
