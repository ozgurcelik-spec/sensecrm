using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Integrations.Infrastructure;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Web.Filters;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Integrations.Tests.Api.Kit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Integrations.Tests.Api;

/// <summary>Sürüm politikası kanıtı: kullanımdan kaldırılan uç <c>Deprecation</c>/<c>Sunset</c>/<c>Link</c> başlıkları döner.</summary>
[ApiController]
[Route("api/v1/_probe")]
public sealed class DeprecatedProbeController : ControllerBase
{
    [HttpGet("old")]
    [Obsolete("probe")]
    [DeprecatedEndpoint("2027-06-30", "/api/v1/_probe/new")]
    public IActionResult Old() => Ok(new { ok = true });

    [HttpGet("new")]
    public IActionResult New() => Ok(new { ok = true });
}

/// <summary>OpenAPI belgesi: kimlik/izin, plan süzgeci, kapsam eşlemesi (belge = gerçek), sürüm politikası başlıkları.</summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiDocumentTests(CrmApiFactory factory)
{
    private const string DocPath = Base + "/integrations/openapi.json";

    [Fact]
    public async Task TheDocumentRequiresAuthenticationAndThePermission_AndIsNeverCached()
    {
        var t = await factory.NewTenantAsync("Belge Yetki " + Guid.NewGuid().ToString("N")[..6]);
        (await factory.CreateClient().GetAsync(DocPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var standard = await factory.AddMemberAsync(t.Admin, "Uye", await t.Admin.RoleIdAsync("Standard"));
        await (await standard.Client.GetAsync(DocPath, Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "forbidden");

        var response = await t.Admin.GetAsync(DocPath, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.CacheControl.Private.ShouldBeTrue();

        // Bir API anahtari yonetim izni tasiyamaz: belgeyi de indiremez.
        var (_, key, _) = await t.Admin.CreateKeyAsync("belge-anahtari", ["crm.leads.read"]);
        (await factory.CreateClient().WithBearer(key).GetAsync(DocPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OnlyPublicPathsAreDocumented_EveryOperationCarriesScopeAndSecurity_AndAllRefsResolve()
    {
        var (t, _) = await factory.TenantOnPlanAsync("Belge Icerik " + Guid.NewGuid().ToString("N")[..6], "internal");
        var doc = JsonNode.Parse(await (await t.Admin.GetAsync(DocPath, Ct)).Content.ReadAsStringAsync(Ct))!;
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();

        paths.ShouldContain("/api/v1/leads");
        paths.ShouldContain("/api/v1/leads/{id}");
        paths.ShouldContain("/api/v1/quotes");
        paths.ShouldContain("/api/v1/cases");
        foreach (var forbidden in new[] { "/auth", "/me", "/organization", "/platform", "/workflows", "/approvals", "/integrations", "/audit", "/permissions", "/subscription", "/onboarding", "/service/sla-policies", "/_probe" })
        {
            paths.Where(p => p.StartsWith("/api/v1" + forbidden, StringComparison.Ordinal)).ShouldBeEmpty($"management-plane path {forbidden} must not be documented");
        }

        foreach (var (path, item) in doc["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                operation!["x-required-scope"]!.GetValue<string>().ShouldStartWith("crm.", customMessage: $"{method} {path}");
                operation["security"]![0]!["bearerApiKey"].ShouldNotBeNull($"{method} {path}");
            }
        }

        doc["components"]!["securitySchemes"]!["bearerApiKey"]!["bearerFormat"]!.GetValue<string>().ShouldBe("crmk");
        var schemas = doc["components"]!["schemas"]?.AsObject().Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var reference in Refs(doc))
        {
            schemas.ShouldContain(reference, $"dangling $ref {reference}");
        }

        doc["info"]!["description"]!.GetValue<string>().ShouldContain("crmk_");
    }

    [Fact]
    public async Task ModulesClosedInThePlan_AreAbsentFromTheDocument()
    {
        await factory.EnsurePlanAsync("m8b_lean", """{"maxUsers":null,"maxRecords":{}}""", """{"workflows":false,"commerce":false,"service":false,"marketing":false,"integrations":true}""");
        var (t, _) = await factory.TenantOnPlanAsync("Belge Yalin " + Guid.NewGuid().ToString("N")[..6], "m8b_lean");
        var doc = JsonNode.Parse(await (await t.Admin.GetAsync(DocPath, Ct)).Content.ReadAsStringAsync(Ct))!;
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();
        paths.ShouldContain("/api/v1/leads");
        foreach (var closed in new[] { "/api/v1/quotes", "/api/v1/orders", "/api/v1/products", "/api/v1/cases", "/api/v1/campaigns", "/api/v1/reports/commerce", "/api/v1/reports/service", "/api/v1/reports/marketing" })
        {
            paths.Where(p => p.StartsWith(closed, StringComparison.Ordinal)).ShouldBeEmpty(closed);
        }
    }

    [Fact]
    public async Task TheDocumentIsTheTruth_EveryDocumentedReadWorksWithExactlyItsScope_AndIsForbiddenWithAnyOther()
    {
        var (t, _) = await factory.TenantOnPlanAsync("Belge Gercek " + Guid.NewGuid().ToString("N")[..6], "internal");
        var doc = JsonNode.Parse(await (await t.Admin.GetAsync(DocPath, Ct)).Content.ReadAsStringAsync(Ct))!;
        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal);
        async Task<HttpClient> KeyFor(params string[] scopes)
        {
            var id = string.Join("+", scopes.Order(StringComparer.Ordinal));
            if (!clients.TryGetValue(id, out var client))
            {
                client = factory.KeyClient((await t.Admin.CreateKeyAsync("k-" + clients.Count, scopes)).Key);
                clients[id] = client;
            }

            return client;
        }

        var covered = 0;
        var otherScope = "crm.activities.read";
        foreach (var (path, item) in doc["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                var scopes = operation!["x-required-scopes"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [operation["x-required-scope"]!.GetValue<string>()];
                var url = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", _ => Guid.NewGuid().ToString());
                var mismatched = scopes.Contains(otherScope) ? "crm.leads.read" : otherScope;
                if (method == "get")
                {
                    var allowed = await (await KeyFor(scopes)).GetAsync(url, Ct);
                    ((int)allowed.StatusCode).ShouldBeInRange(200, 499, $"GET {path}");
                    allowed.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, $"GET {path} with exactly {string.Join(",", scopes)}");
                    allowed.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, $"GET {path}");

                    // Dogrulama yetkiden once calisir (M2): zorunlu sorgu parametresi eksikse 400 doner ve 403 kanitlanamaz; digerlerinde tam 403 beklenir.
                    var denied = await (await KeyFor(mismatched)).GetAsync(url, Ct);
                    denied.StatusCode.ShouldBeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.BadRequest], $"GET {path} with only {mismatched}");
                    if (denied.StatusCode == HttpStatusCode.Forbidden)
                    {
                        covered++;
                    }
                }
                else
                {
                    // Yazma islemleri: dogrulama yetkiden once calistigindan 403 kanitlanamaz; belgelenen kapsamla 403 OLMAMALI.
                    using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);
                    var response = await (await KeyFor(scopes)).SendAsync(request, Ct);
                    response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, $"{method.ToUpperInvariant()} {path} with the documented scope");
                }
            }
        }

        covered.ShouldBeGreaterThan(15, "the truth test must exercise a real number of documented reads");
    }

    [Fact]
    public async Task DeprecatedEndpoints_ReturnDeprecationSunsetAndSuccessorHeaders()
    {
        await using var host = TestHost.Create(factory);
        var probeFactory = host.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddControllers().AddApplicationPart(typeof(DeprecatedProbeController).Assembly)));
        var client = probeFactory.CreateClient();
        var old = await client.GetAsync($"{Base}/_probe/old", Ct);
        old.StatusCode.ShouldBe(HttpStatusCode.OK);
        old.Headers.GetValues("Deprecation").ShouldBe(["true"]);
        old.Headers.GetValues("Sunset").Single().ShouldBe("Wed, 30 Jun 2027 00:00:00 GMT");
        old.Headers.GetValues("Link").Single().ShouldBe("</api/v1/_probe/new>; rel=\"successor-version\"");

        var current = await client.GetAsync($"{Base}/_probe/new", Ct);
        current.Headers.Contains("Deprecation").ShouldBeFalse();
        current.Headers.Contains("Sunset").ShouldBeFalse();
    }

    private static IEnumerable<string> Refs(JsonNode node)
    {
        const string Prefix = "#/components/schemas/";
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out var reference) && reference.StartsWith(Prefix, StringComparison.Ordinal))
                    {
                        yield return reference[Prefix.Length..];
                    }
                    else if (value is not null)
                    {
                        foreach (var inner in Refs(value))
                        {
                            yield return inner;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array.Where(i => i is not null))
                {
                    foreach (var inner in Refs(item!))
                    {
                        yield return inner;
                    }
                }

                break;
            default:
                break;
        }
    }
}

internal static class ClientExtensions
{
    public static HttpClient WithBearer(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>KVKK imhası, saklama, kaynak olayların outbox davranışı ve kiracı izolasyonunun teslimat kuyruğundaki kanıtı.</summary>
[Collection(ApiCollection.Name)]
public sealed class RetentionErasureAndEventTests(CrmApiFactory factory)
{
    private async Task<(Tenant Tenant, Guid Hook, Guid Key, Guid Delivery)> SeedAsync(TestHost host, string name)
    {
        var t = await host.Factory.NewTenantAsync(name + " " + Guid.NewGuid().ToString("N")[..6]);
        await host.Factory.DrainOutboxesAsync();
        var hook = (await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "h", url = "https://hooks.example.com/x", eventTypes = new[] { "lead.created" } })).Body.GuidProp("id");
        var (keyId, key, _) = await t.Admin.CreateKeyAsync("k", ["crm.leads.read"]);
        await t.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        (await host.Factory.KeyClient(key).GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var flush = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Sense.Crm.Modules.Integrations.Api.ApiKeyUsageFlushService>().Single();
        await flush.FlushAsync(Ct);
        var delivery = await factory.ScalarAsync<Guid>("SELECT id FROM integrations.webhook_deliveries WHERE subscription_id = @s", ("s", hook));
        return (t, hook, keyId, delivery);
    }

    private async Task<Dictionary<string, long>> CountsAsync(Guid tenantId)
    {
        var result = new Dictionary<string, long>();
        foreach (var table in new[] { "webhook_subscriptions", "webhook_deliveries", "webhook_delivery_attempts", "api_keys", "api_key_usage_daily", "delivery_queue", "outbox_messages" })
        {
            result[table] = await factory.ScalarAsync<long>($"SELECT count(*) FROM integrations.{table} WHERE tenant_id = @t", ("t", tenantId));
        }

        return result;
    }

    [Fact]
    public async Task Erasure_RemovesEveryIntegrationsRowIncludingTheQueue_LeavesOtherTenantsIntact_AndIsIdempotent()
    {
        await using var host = TestHost.Create(factory);
        var a = await SeedAsync(host, "Imha A");
        var b = await SeedAsync(host, "Imha B");
        var attemptCount = await host.RunDispatcherAsync();
        attemptCount.ShouldBeGreaterThanOrEqualTo(2);
        var aBefore = await CountsAsync(a.Tenant.TenantId);
        var bBefore = await CountsAsync(b.Tenant.TenantId);
        aBefore["webhook_subscriptions"].ShouldBe(1);
        aBefore["api_keys"].ShouldBe(1);
        aBefore["api_key_usage_daily"].ShouldBeGreaterThanOrEqualTo(1);
        aBefore["webhook_delivery_attempts"].ShouldBeGreaterThanOrEqualTo(1);

        // Bekleyen teslimat (kuyruk satiri) icin ek bir olay: imha sirasinda ucusta kalmamali.
        await a.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        (await CountsAsync(a.Tenant.TenantId))["delivery_queue"].ShouldBe(1);

        using (var scope = host.Services.CreateScope())
        {
            var erasers = scope.ServiceProvider.GetServices<ITenantDataEraser>().Where(e => e.Name.Contains("integrations", StringComparison.Ordinal)).OrderBy(e => e.Order).ToList();
            erasers.Select(e => e.Name).ShouldContain("module:integrations:delivery_queue");
            erasers.Select(e => e.Name).ShouldContain("module:integrations");
            erasers.First().Order.ShouldBeLessThan(erasers.Last().Order, "queue rows go before the tables");
            foreach (var eraser in erasers)
            {
                await eraser.EraseAsync(a.Tenant.TenantId, 1000, Ct);
            }

            foreach (var eraser in erasers)
            {
                (await eraser.EraseAsync(a.Tenant.TenantId, 1000, Ct)).Total.ShouldBe(0, "idempotent");
            }
        }

        (await CountsAsync(a.Tenant.TenantId)).Values.ShouldAllBe(v => v == 0);
        (await CountsAsync(b.Tenant.TenantId)).ShouldBe(bBefore);
        (await host.Factory.KeyClient("crmk_" + a.Tenant.TenantId.ToString("N") + "_00000000_" + new string('a', 43)).GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Retention_DeletesOldFinishedDeliveries_ExpiredPreviousSecrets_AndOldUsage_ButKeepsPendingRows()
    {
        await using var host = TestHost.Create(factory);
        var s = await SeedAsync(host, "Saklama");
        await host.RunDispatcherAsync();
        var oldDelivery = s.Delivery;
        await factory.SqlAsync("UPDATE integrations.webhook_deliveries SET created_at = now() - interval '40 days' WHERE id = @id", ("id", oldDelivery));
        await factory.SqlAsync("UPDATE integrations.api_key_usage_daily SET day = (now() - interval '200 days')::date WHERE tenant_id = @t", ("t", s.Tenant.TenantId));
        await s.Tenant.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{s.Hook}/rotate-secret", new { graceHours = 1 }, HttpStatusCode.OK);
        await factory.SqlAsync("UPDATE integrations.webhook_subscriptions SET previous_secret_expires_at = now() - interval '1 hour' WHERE id = @id", ("id", s.Hook));

        // Bekleyen (kuyruktaki) eski satir silinmez.
        await s.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        var pendingId = await factory.ScalarAsync<Guid>("SELECT delivery_id FROM integrations.delivery_queue WHERE tenant_id = @t LIMIT 1", ("t", s.Tenant.TenantId));
        await factory.SqlAsync("UPDATE integrations.webhook_deliveries SET created_at = now() - interval '40 days' WHERE id = @id", ("id", pendingId));

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IntegrationsRetention>().RunAsync(Ct);
        }

        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.webhook_deliveries WHERE id = @id", ("id", oldDelivery))).ShouldBe(0);
        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.webhook_delivery_attempts WHERE delivery_id = @id", ("id", oldDelivery))).ShouldBe(0, "attempts cascade");
        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.webhook_deliveries WHERE id = @id", ("id", pendingId))).ShouldBe(1);
        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.api_key_usage_daily WHERE tenant_id = @t", ("t", s.Tenant.TenantId))).ShouldBe(0);
        (await factory.ScalarAsync<bool>("SELECT previous_secret_enc IS NULL FROM integrations.webhook_subscriptions WHERE id = @id", ("id", s.Hook))).ShouldBeTrue();
    }

    [Fact]
    public async Task SourceEvents_AreWrittenToTheOutboxExactlyOncePerCreation_NeverOnFailure_NeverByLeadConversion()
    {
        var t = await factory.NewTenantAsync("Kaynak Olay " + Guid.NewGuid().ToString("N")[..6]);
        Task<long> Count(string like) => factory.ScalarAsync<long>("SELECT count(*) FROM sales.outbox_messages WHERE tenant_id = @t AND type ILIKE @l", ("t", t.TenantId), ("l", like));

        var account = (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "Olay Firma" })).Body.GuidProp("id");
        (await Count("%AccountCreated%")).ShouldBe(1);
        var payload = await factory.ScalarAsync<string>("SELECT payload FROM sales.outbox_messages WHERE tenant_id = @t AND type ILIKE '%AccountCreated%'", ("t", t.TenantId));
        JsonDocument.Parse(payload).RootElement.GuidProp("accountId").ShouldBe(account);

        (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Count("%AccountCreated%")).ShouldBe(1, "a failed command writes nothing");

        (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/contacts", new { lastName = "Kisi", accountId = account })).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Count("%ContactCreated%")).ShouldBe(1);
        (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/contacts", new { lastName = "Kisi", accountId = Guid.NewGuid() })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Count("%ContactCreated%")).ShouldBe(1);

        var lead = await t.Admin.CreateLeadAsync();
        (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/leads/{lead}/convert", new { createDeal = false })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Count("%AccountCreated%")).ShouldBe(1, "lead conversion must not produce account.created");
        (await Count("%ContactCreated%")).ShouldBe(1, "lead conversion must not produce contact.created");
        (await Count("%LeadConverted%")).ShouldBe(1);

        (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/cases", new { subject = "Konu", accountId = account, priority = "urgent", channel = "email" })).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.outbox_messages WHERE tenant_id = @t AND type ILIKE '%CaseCreated%'", ("t", t.TenantId))).ShouldBe(1);
        (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/cases", new { subject = "" })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.outbox_messages WHERE tenant_id = @t AND type ILIKE '%CaseCreated%'", ("t", t.TenantId))).ShouldBe(1);
    }
}
