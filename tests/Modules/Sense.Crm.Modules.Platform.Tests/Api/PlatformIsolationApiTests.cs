using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// ZORUNLU güvenlik testleri (M7): platform uçları kiracı yöneticisine kapalıdır (uçlar <c>EndpointDataSource</c>'tan yansımayla listelenir: yeni uç otomatik kapsanır),
/// geri alınan platform bayrağı JWT'de kalsa da işe yaramaz, platform yöneticisi başka kiracının iş verisine erişemez, bir kiracının abonelik/onboarding/me yanıtları
/// yalnız kendisini gösterir ve bir kiracının yöneticisi başka kiracıya hiçbir kiracı ucundan ulaşamaz.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class PlatformIsolationApiTests(CrmApiFactory factory)
{
    private const string PlatformRoutePrefix = "api/v{version:apiVersion}/platform";

    [GeneratedRegex(@"\{\*?(?<name>[^}:?=]+)(:[^}]+)?\??\}")]
    private static partial Regex RouteParameter();

    // ---- (a) tüm /platform/** uçları: kiracı yöneticisi 403, anonim 401 ----------------------------------------------------

    [Fact]
    public async Task EveryPlatformEndpoint_IsForbiddenForATenantAdministrator_AndUnauthorizedForAnonymousCallers()
    {
        var endpoints = PlatformEndpoints();
        var org = await factory.NewOrgAsync(Token("iso") + " tenant admin");
        var anonymous = factory.CreateClient();

        endpoints.Count.ShouldBeGreaterThanOrEqualTo(10, "yansıma ile bulunan platform uçları (boş geçmeyi önler)");
        var signatures = endpoints.Select(e => $"{e.Method} {e.Pattern}").ToList();
        signatures.ShouldContain($"GET {PlatformRoutePrefix}/organizations");
        signatures.ShouldContain($"POST {PlatformRoutePrefix}/organizations");
        signatures.ShouldContain($"PUT {PlatformRoutePrefix}/organizations/{{tenantId:guid}}/subscription");
        signatures.ShouldContain($"POST {PlatformRoutePrefix}/organizations/{{tenantId:guid}}/suspend");
        signatures.ShouldContain($"POST {PlatformRoutePrefix}/organizations/{{tenantId:guid}}/deletion-request");
        signatures.ShouldContain($"GET {PlatformRoutePrefix}/usage/export");
        signatures.ShouldContain($"GET {PlatformRoutePrefix}/plans");
        signatures.ShouldContain($"GET {PlatformRoutePrefix}/audit");

        var failures = new List<string>();
        foreach (var (method, pattern, url) in endpoints)
        {
            var asTenantAdmin = await org.Admin.SendRawAsync(new HttpMethod(method), url, BodyFor(method));
            var (adminStatus, adminCode) = await ReadProblemAsync(asTenantAdmin);
            if (adminStatus != HttpStatusCode.Forbidden || adminCode != "forbidden")
            {
                failures.Add($"tenant admin {method} {pattern}: {(int)adminStatus} {adminCode}");
            }

            var asAnonymous = await anonymous.SendRawAsync(new HttpMethod(method), url, BodyFor(method));
            var (anonymousStatus, anonymousCode) = await ReadProblemAsync(asAnonymous);
            if (anonymousStatus != HttpStatusCode.Unauthorized || anonymousCode != "auth.unauthenticated")
            {
                failures.Add($"anonymous {method} {pattern}: {(int)anonymousStatus} {anonymousCode}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATenantAdministrator_HoldingEveryTenantPermission_IsStillForbiddenOnPlatformEndpoints()
    {
        var org = await factory.NewOrgAsync(Token("all") + " all permissions");
        var me = await org.Admin.GetJsonAsync($"{Base}/me");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        var catalog = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => p.Str("key")).ToList();

        permissions.ShouldBe(catalog, ignoreOrder: true, "Administrator izin kataloğunun tamamına sahiptir");
        catalog.ShouldNotContain(key => key.StartsWith("platform.", StringComparison.Ordinal), "platform yetkisi kiracı izni değildir");
        (await org.Admin.GetAsync($"{PlatformBase}/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await org.Admin.GetAsync($"{PlatformBase}/organizations", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        me.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse();
    }

    // ---- (b) geri alınan bayrak ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARevokedPlatformFlag_IsCheckedInTheDatabase_SoTheOldTokenStopsWorking_AndRegrantingRestoresIt()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        var victim = await factory.SyncedOrgAsync(Token("rev") + " victim");
        (await platform.GetAsync($"{PlatformBase}/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await factory.SqlAsync("UPDATE identity.users SET is_platform_admin = FALSE WHERE id = @u", ("u", me.UserId))).ShouldBe(1);

        // Token'da platform bayrağı durur (JWT'ye güvenilmez): sorgular ve komutlar 403.
        await (await platform.GetAsync($"{PlatformBase}/plans", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await platform.GetAsync($"{PlatformBase}/organizations", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await platform.GetAsync(OrgUrl(victim.TenantId), Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await platform.SuspendRawAsync(victim.TenantId)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await platform.PutSubscriptionRawAsync(victim.TenantId, "internal", overrides: new { maxUsers = 1 })).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await platform.GetAsync($"{PlatformBase}/usage/export", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await factory.AuditCountAsync(victim.TenantId)).ShouldBe(0, "reddedilen komutlar hiçbir şey yazmaz");
        (await platform.GetJsonAsync($"{Base}/me")).GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse("hesabın kendi bilgisi güncel bayrağı gösterir");

        // Bayrak geri verilirse aynı token yeniden çalışır (önbellek/kalıcı ret yok).
        await factory.SqlAsync("UPDATE identity.users SET is_platform_admin = TRUE WHERE id = @u", ("u", me.UserId));
        (await platform.GetAsync($"{PlatformBase}/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ADeactivatedPlatformAdminAccount_CannotUseItsOldToken_OnPlatformEndpoints()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        (await platform.GetAsync($"{PlatformBase}/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await factory.SqlAsync("UPDATE identity.users SET is_active = FALSE WHERE id = @u", ("u", me.UserId));

        var response = await platform.GetAsync($"{PlatformBase}/plans", Ct);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    // ---- (c) platform yöneticisi başka kiracının iş verisine erişemez ------------------------------------------------------

    [Fact]
    public async Task APlatformAdmin_CannotReadOrWriteAnotherTenantsBusinessData_AndOnlySeesItsOwnOperatingOrganization()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        var victim = await factory.SyncedOrgAsync(Token("vic") + " victim data");
        var accountIds = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            accountIds.Add((await Create(victim.Admin, "accounts", new { name = $"Musteri Verisi {i}" })).GuidProp("id"));
        }

        var lead = await Create(victim.Admin, "leads", new { lastName = "Gizli Aday", company = "Musteri" });
        var kase = await Create(victim.Admin, "cases", new { subject = "Gizli talep" });
        var contact = await Create(victim.Admin, "contacts", new { lastName = "Gizli Kisi" });

        // Liste: yalnız kendi işletim organizasyonunun verisi (boş).
        var list = await platform.GetJsonAsync($"{Base}/accounts");
        list.GetProperty("totalCount").GetInt64().ShouldBe(0);
        list.GetProperty("items").EnumerateArray().Select(i => i.GuidProp("id")).ShouldBeEmpty();
        (await platform.GetJsonAsync($"{Base}/leads")).GetProperty("totalCount").GetInt64().ShouldBe(0);
        (await platform.GetJsonAsync($"{Base}/cases")).GetProperty("totalCount").GetInt64().ShouldBe(0);
        (await platform.GetJsonAsync($"{Base}/contacts")).GetProperty("totalCount").GetInt64().ShouldBe(0);

        // Kimlikle doğrudan erişim: okuma/güncelleme/silme 404 (kiracı filtresi).
        foreach (var path in new[] { $"accounts/{accountIds[0]}", $"leads/{lead.GuidProp("id")}", $"cases/{kase.GuidProp("id")}", $"contacts/{contact.GuidProp("id")}" })
        {
            await (await platform.GetAsync($"{Base}/{path}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
            await (await platform.DeleteAsync($"{Base}/{path}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        await (await platform.PutAsJsonAsync($"{Base}/accounts/{accountIds[0]}", new { name = "Ele gecirilmis" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Yazma: yeni kayıt platform yöneticisinin kendi organizasyonuna gider, kurbana hiçbir şey eklenmez/değişmez.
        var own = await Create(platform, "accounts", new { name = "Platform kaydi" });
        (await victim.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt64().ShouldBe(2);
        (await victim.Admin.GetJsonAsync($"{Base}/accounts/{accountIds[0]}")).Str("name").ShouldBe("Musteri Verisi 0");
        (await factory.ScalarAsync<Guid>("SELECT tenant_id FROM sales.accounts WHERE id = @i", ("i", own.GuidProp("id")))).ShouldBe(me.TenantId);

        // Kurban organizasyona geçiş: üyeliği yok.
        var switchResponse = await platform.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = victim.TenantId }, Ct);
        switchResponse.IsSuccessStatusCode.ShouldBeFalse(await switchResponse.Content.ReadAsStringAsync(Ct));
        (await platform.GetJsonAsync($"{Base}/me")).GetProperty("organization").GuidProp("id").ShouldBe(me.TenantId);

        // Platform uçları yalnız hesap/plan/sayaç gösterir: iş verisi alanı yok.
        var detail = await platform.DetailAsync(victim.TenantId);
        var json = detail.GetRawText();
        json.ShouldNotContain("Musteri Verisi");
        json.ShouldNotContain("Gizli");
        detail.EnumerateObject().Select(p => p.Name).Intersect(new[] { "accounts", "leads", "contacts", "cases", "users", "members" }).ShouldBeEmpty();
    }

    // ---- (d) bir kiracının abonelik/onboarding/me yanıtları yalnız kendisini gösterir ------------------------------------------

    [Fact]
    public async Task SubscriptionOnboardingAndMe_ShowOnlyTheCallersOwnTenant()
    {
        // Kayıt sayaçları önbellekli (yumuşak limit); bu test canlı sayı ister.
        await using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Platform:Usage:CacheSeconds", "0"));
        var platform = await host.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_iso_a_m7", """{"maxUsers":11,"maxRecords":{"sales":111}}""", AllModulesOn);
        await factory.EnsurePlanAsync("console_iso_b_m7", """{"maxUsers":12,"maxRecords":{"sales":222}}""", AllModulesOff);
        var a = await host.SyncedOrgAsync(Token("isa") + " tenant A");
        var b = await host.SyncedOrgAsync(Token("isb") + " tenant B");
        var standard = (await b.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Standard").GuidProp("id");
        await b.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/members", new { email = UniqueEmail("bmember"), displayName = "B Uyesi", roleId = standard }, HttpStatusCode.Created);
        await platform.PutSubscriptionAsync(a.TenantId, "console_iso_a_m7");
        await platform.PutSubscriptionAsync(b.TenantId, "console_iso_b_m7");
        for (var i = 0; i < 2; i++)
        {
            await Create(a.Admin, "accounts", new { name = $"A Firma {i}" });
        }

        for (var i = 0; i < 5; i++)
        {
            await Create(b.Admin, "accounts", new { name = $"B Firma {i}" });
        }

        // A profilini tamamlar; B'ninki değişmez.
        var organization = await a.Admin.GetJsonAsync($"{Base}/organization");
        await a.Admin.SendJsonAsync(
            HttpMethod.Put, $"{Base}/organization", new { name = organization.Str("name"), defaultLocale = organization.Str("defaultLocale"), timeZone = organization.Str("timeZone") }, HttpStatusCode.NoContent);

        var subscriptionA = await a.Admin.GetJsonAsync($"{Base}/subscription");
        subscriptionA.Str("planCode").ShouldBe("console_iso_a_m7");
        subscriptionA.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(11);
        subscriptionA.GetProperty("limits").GetProperty("maxRecords").GetProperty("sales").GetInt32().ShouldBe(111);
        subscriptionA.GetProperty("usage").GetProperty("users").GetInt32().ShouldBe(1, "yalnız A'nın kullanıcısı");
        subscriptionA.GetProperty("usage").GetProperty("records").GetProperty("sales").GetInt64().ShouldBe(2, "yalnız A'nın kayıtları");
        subscriptionA.GetProperty("modules").GetProperty("workflows").GetBoolean().ShouldBeTrue();

        var subscriptionB = await b.Admin.GetJsonAsync($"{Base}/subscription");
        subscriptionB.Str("planCode").ShouldBe("console_iso_b_m7");
        subscriptionB.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(12);
        subscriptionB.GetProperty("usage").GetProperty("users").GetInt32().ShouldBe(2);
        subscriptionB.GetProperty("usage").GetProperty("records").GetProperty("sales").GetInt64().ShouldBe(5);
        subscriptionB.GetProperty("modules").GetProperty("workflows").GetBoolean().ShouldBeFalse();

        var meA = await a.Admin.GetJsonAsync($"{Base}/me");
        meA.GetProperty("organization").GuidProp("id").ShouldBe(a.TenantId);
        meA.GetProperty("organizations").EnumerateArray().Select(o => o.GuidProp("id")).ShouldBe([a.TenantId]);
        meA.GetProperty("subscription").Str("planCode").ShouldBe("console_iso_a_m7");
        meA.GetProperty("user").GuidProp("id").ShouldBe(a.AdminUserId);
        var meB = await b.Admin.GetJsonAsync($"{Base}/me");
        meB.GetProperty("organizations").EnumerateArray().Select(o => o.GuidProp("id")).ShouldBe([b.TenantId]);
        meB.GetProperty("subscription").Str("planCode").ShouldBe("console_iso_b_m7");

        var onboardingA = await a.Admin.GetJsonAsync($"{Base}/onboarding");
        var onboardingB = await b.Admin.GetJsonAsync($"{Base}/onboarding");
        onboardingA.GetProperty("items").EnumerateArray().Single(i => i.Str("key") == "profile").GetProperty("done").GetBoolean().ShouldBeTrue();
        onboardingB.GetProperty("items").EnumerateArray().Single(i => i.Str("key") == "profile").GetProperty("done").GetBoolean().ShouldBeFalse();
        onboardingB.GetProperty("items").EnumerateArray().Single(i => i.Str("key") == "invite_user").GetProperty("done").GetBoolean().ShouldBeTrue("B'nin kendi üyesi");
        onboardingA.GetProperty("items").EnumerateArray().Single(i => i.Str("key") == "invite_user").GetProperty("done").GetBoolean().ShouldBeFalse();
        onboardingB.GetProperty("items").GetArrayLength().ShouldBe(3, "B'de workflows kapalı: kural adımı yok");
        onboardingA.GetProperty("items").GetArrayLength().ShouldBe(4);

        // Sorgu/başlık ile başka kiracı kimliği verilse de yanıt çağıranın kendi kiracısıdır.
        var spoofed = await a.Admin.GetJsonAsync($"{Base}/subscription?tenantId={b.TenantId}&organizationId={b.TenantId}");
        spoofed.Str("planCode").ShouldBe("console_iso_a_m7");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/subscription");
        request.Headers.Add("X-Tenant-Id", b.TenantId.ToString());
        request.Headers.Add("X-Organization-Id", b.TenantId.ToString());
        var withHeaders = await a.Admin.SendAsync(request, Ct);
        withHeaders.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await withHeaders.Content.ReadFromJsonAsync<JsonElement>(Ct)).Str("planCode").ShouldBe("console_iso_a_m7");
        (await a.Admin.GetJsonAsync($"{Base}/onboarding?tenantId={b.TenantId}")).GetProperty("items").GetArrayLength().ShouldBe(4, "A'nın listesi (4 adım), B'ninki değil (3 adım)");
    }

    [Fact]
    public async Task ATenantAdmin_CannotReachAnotherTenantsRecordsMembersOrRoles_ThroughAnyTenantEndpoint()
    {
        var a = await factory.NewOrgAsync(Token("ca") + " tenant A");
        var b = await factory.NewOrgAsync(Token("cb") + " tenant B");
        var bAdminRole = (await b.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Administrator").GuidProp("id");
        var account = await Create(b.Admin, "accounts", new { name = "B hesabi" });
        var accountId = account.GuidProp("id");
        var paths = new Dictionary<string, Guid>
        {
            ["accounts"] = accountId,
            ["contacts"] = (await Create(b.Admin, "contacts", new { lastName = "B kisi" })).GuidProp("id"),
            ["leads"] = (await Create(b.Admin, "leads", new { lastName = "B aday", company = "B" })).GuidProp("id"),
            ["deals"] = (await Create(b.Admin, "deals", new { name = "B firsat", accountId, amount = 10m })).GuidProp("id"),
            ["activities"] = (await Create(b.Admin, "activities", new { type = "task", subject = "B gorev" })).GuidProp("id"),
            ["campaigns"] = (await Create(b.Admin, "campaigns", new { name = "B kampanya", type = "email" })).GuidProp("id"),
            ["cases"] = (await Create(b.Admin, "cases", new { subject = "B talep" })).GuidProp("id"),
            ["products"] = (await Create(b.Admin, "products", new { name = "B urun", unitPrice = 10m, taxRate = 20m })).GuidProp("id"),
            ["workflows/rules"] = (await Create(b.Admin, "workflows/rules", new { name = "B kural", kind = "leadAssignment", @params = new { assigneeRoleId = bAdminRole } })).GuidProp("id"),
        };
        var line = new { description = "Kalem", quantity = 1m, unitPrice = 10m, discountPercent = 0m, taxRate = 20m };
        paths["quotes"] = (await Create(b.Admin, "quotes", new { subject = "B teklif", accountId, lines = new[] { line } })).GuidProp("id");
        paths["orders"] = (await Create(b.Admin, "orders", new { subject = "B siparis", accountId, lines = new[] { line } })).GuidProp("id");

        foreach (var (path, id) in paths)
        {
            await (await a.Admin.GetAsync($"{Base}/{path}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
            await (await a.Admin.DeleteAsync($"{Base}/{path}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
            (await CountAsync(a.Admin, path)).ShouldBe(0, $"A'nın {path} listesi B'nin kaydını göstermez");
            (await b.Admin.GetAsync($"{Base}/{path}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, $"B'nin kaydı silinmedi: {path}");
        }

        // Üyelik/rol: B'nin yöneticisi ve rolü A'dan görünmez, değiştirilemez.
        await (await a.Admin.SendRawAsync(HttpMethod.Patch, $"{Base}/organization/members/{b.AdminUserId}", new { isActive = false })).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.SendRawAsync(HttpMethod.Put, $"{Base}/organization/roles/{bAdminRole}", new { name = "Ele gecirilmis", permissions = Array.Empty<string>() })).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.DeleteAsync($"{Base}/organization/roles/{bAdminRole}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await a.Admin.GetJsonAsync($"{Base}/organization/members")).EnumerateArray().Select(m => m.GuidProp("userId")).ShouldBe([a.AdminUserId]);
        (await a.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Select(r => r.GuidProp("id")).ShouldNotContain(bAdminRole);
        (await b.Admin.GetJsonAsync($"{Base}/organization/members")).EnumerateArray().Single().GetProperty("isActive").GetBoolean().ShouldBeTrue();

        // A'nın organizasyon uçları yalnız A'yı gösterir; başka organizasyona oturum geçişi yok.
        (await a.Admin.GetJsonAsync($"{Base}/organization")).GuidProp("id").ShouldBe(a.TenantId);
        var switchResponse = await a.Admin.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = b.TenantId }, Ct);
        switchResponse.IsSuccessStatusCode.ShouldBeFalse(await switchResponse.Content.ReadAsStringAsync(Ct));
    }

    // ---- yardımcılar -----------------------------------------------------------------------------------------------------------

    private static async Task<JsonElement> Create(HttpClient client, string path, object body) =>
        await client.SendJsonAsync(HttpMethod.Post, $"{Base}/{path}", body, HttpStatusCode.Created);

    /// <summary>Liste ucunun kayıt sayısı (sayfalı <c>totalCount</c> ya da düz dizi).</summary>
    private static async Task<long> CountAsync(HttpClient client, string path)
    {
        var body = await client.GetJsonAsync($"{Base}/{path}");
        return body.ValueKind == JsonValueKind.Array ? body.GetArrayLength() : body.GetProperty("totalCount").GetInt64();
    }

    private static object? BodyFor(string method) => method is "POST" or "PUT" or "PATCH" ? new { } : null;

    private static async Task<(HttpStatusCode Status, string? Code)> ReadProblemAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (response.StatusCode, null);
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            return (response.StatusCode, json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
        }
        catch (JsonException)
        {
            return (response.StatusCode, null);
        }
    }

    /// <summary>Host'un yönlendirme tablosundan tüm <c>/api/v1/platform/**</c> uçları (yöntem + şablon + rastgele kimliklerle çözümlenmiş URL).</summary>
    private List<(string Method, string Pattern, string Url)> PlatformEndpoints()
    {
        var dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var result = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith(PlatformRoutePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                if (!seen.Add($"{method} {pattern}"))
                {
                    continue;
                }

                var url = "/" + RouteParameter().Replace(
                    pattern,
                    match => match.Groups["name"].Value.Equals("version", StringComparison.OrdinalIgnoreCase) ? "1" : Guid.NewGuid().ToString());
                result.Add((method.ToUpperInvariant(), pattern, url));
            }
        }

        return result;
    }
}
