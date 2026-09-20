using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sense.Crm.Modules.Integrations.Api;
using Sense.Crm.Modules.Integrations.Application.OpenApi;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Integrations.Tests.Api.Kit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Integrations.Tests.Api;

/// <summary>Paylaşılan test host'u (önbelleksiz anahtar arama, sahte saat, test IP'si).</summary>
internal static class SharedHosts
{
    private static readonly object Gate = new();
    private static TestHost? _keys;

    public static TestHost Keys(CrmApiFactory root)
    {
        lock (Gate)
        {
            return _keys ??= TestHost.Create(root);
        }
    }
}

/// <summary>API anahtarı: oluşturma, kimlik doğrulama (yalnız Bearer crmk_), yetki daralması, yükseltme engeli, iptal/süre/IP, kaçış yolları, sayaçlar.</summary>
[Collection(ApiCollection.Name)]
public sealed class ApiKeyApiTests(CrmApiFactory factory)
{
    private TestHost Host => SharedHosts.Keys(factory);

    private async Task<Tenant> NewTenantAsync(string name)
    {
        var t = await Host.Factory.NewTenantAsync(name + " " + Guid.NewGuid().ToString("N")[..6]);
        await Host.Factory.DrainOutboxesAsync();
        return t;
    }

    [Fact]
    public async Task Create_ReturnsTheRawKeyOnce_StoresOnlyTheSha256_AndListsOnlyThePrefix()
    {
        var t = await NewTenantAsync("Anahtar Olustur");
        var (id, key, prefix) = await t.Admin.CreateKeyAsync("entegrasyon", ["crm.leads.read", "crm.accounts.read"]);

        key.ShouldStartWith("crmk_" + t.TenantId.ToString("N") + "_" + prefix.Replace("crmk_", string.Empty, StringComparison.Ordinal) + "_");
        key.Length.ShouldBeLessThanOrEqualTo(128);
        prefix.ShouldStartWith("crmk_");

        var secret = key.Split('_', 4)[3];
        (await factory.ScalarAsync<string>("SELECT encode(secret_hash, 'hex') FROM integrations.api_keys WHERE id = @id", ("id", id))).ShouldBe(Sha256Hex(secret));
        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.api_keys WHERE id = @id AND position(convert_to(@s, 'UTF8') in secret_hash) > 0", ("id", id), ("s", secret))).ShouldBe(0);

        var got = await t.Admin.GetJsonAsync($"{Keys}/{id}");
        got.TryGetProperty("key", out _).ShouldBeFalse();
        got.TryGetProperty("secretHash", out _).ShouldBeFalse();
        got.Str("status").ShouldBe("active");
        got.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ShouldBe(["crm.accounts.read", "crm.leads.read"]);
        (got.GetProperty("expiresAt").GetDateTime() - DateTime.UtcNow).TotalDays.ShouldBeInRange(364, 366);
        JsonSerializer.Serialize(await t.Admin.GetJsonAsync(Keys)).ShouldNotContain(secret);
    }

    [Fact]
    public async Task Create_EnforcesScopeRules_LifetimeLimits_Cidrs_AndUniqueNames()
    {
        var t = await NewTenantAsync("Anahtar Kural");
        async Task<HttpResponseMessage> Post(object body) => (await t.Admin.SendAsync(HttpMethod.Post, Keys, body)).Response;

        // org.* ve crm.approvals.decide: yonetim duzlemi / karar yetkisi anahtara verilemez.
        foreach (var scope in new[] { "org.settings.manage", "org.integrations.manage", "org.users.manage", "org.roles.manage", "org.audit.read", "crm.approvals.decide" })
        {
            var (response, json) = await t.Admin.SendAsync(HttpMethod.Post, Keys, new { name = "bad-" + scope, scopes = new[] { scope } });
            await response.ShouldBeCodeAsync(HttpStatusCode.BadRequest, "api_key.scope_not_allowed");
            json.GetProperty("args").GetProperty("scope").GetString().ShouldBe(scope);
        }

        (await Post(new { name = "unknown", scopes = new[] { "crm.nothing.read" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { name = "empty", scopes = Array.Empty<string>() })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { name = "", scopes = new[] { "crm.leads.read" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { name = "past", scopes = new[] { "crm.leads.read" }, expiresAt = DateTime.UtcNow.AddMinutes(-1) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { name = "toolong", scopes = new[] { "crm.leads.read" }, expiresAt = DateTime.UtcNow.AddDays(731) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { name = "max", scopes = new[] { "crm.leads.read" }, expiresAt = DateTime.UtcNow.AddDays(729) })).StatusCode.ShouldBe(HttpStatusCode.Created);

        foreach (var cidrs in new[] { new[] { "0.0.0.0/0" }, ["::/0"], ["10.0.0.0/33"], ["not-a-cidr"], Enumerable.Range(1, 11).Select(i => $"10.0.{i}.0/24").ToArray() })
        {
            (await Post(new { name = "cidr" + Guid.NewGuid().ToString("N")[..4], scopes = new[] { "crm.leads.read" }, allowedCidrs = cidrs })).StatusCode.ShouldBe(HttpStatusCode.BadRequest, string.Join(",", cidrs));
        }

        (await Post(new { name = "dup", scopes = new[] { "crm.leads.read" } })).StatusCode.ShouldBe(HttpStatusCode.Created);
        await (await Post(new { name = "DUP", scopes = new[] { "crm.leads.read" } })).ShouldBeCodeAsync(HttpStatusCode.Conflict, "api_key.name_taken");
    }

    [Fact]
    public async Task AKeyCannotExceedItsCreator_NoAdministratorExemption_AndNarrowsInstantlyWhenTheRoleShrinks()
    {
        var t = await NewTenantAsync("Anahtar Yukseltme");
        var integrator = await t.Admin.CreateRoleAsync("Entegrator", "org.integrations.manage", "crm.leads.read", "crm.accounts.read");
        // Uye JWT'si kok host'a aittir (her host'un JWT imza anahtari ayri); anahtarlar ortak veritabaninda yasar.
        var member = await factory.AddMemberAsync(t.Admin, "Entegrator Uye", integrator);

        // Olusturan olmayan izinle anahtar: 403 role.permission_escalation.
        var (response, json) = await member.Client.SendAsync(HttpMethod.Post, Keys, new { name = "esc", scopes = new[] { "crm.deals.write" } });
        await response.ShouldBeCodeAsync(HttpStatusCode.Forbidden, "role.permission_escalation");
        json.GetProperty("args").GetProperty("scope").GetString().ShouldBe("crm.deals.write");
        (await member.Client.SendAsync(HttpMethod.Post, Keys, new { name = "mixed", scopes = new[] { "crm.leads.read", "crm.deals.write" } })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (keyId, key, _) = await member.Client.CreateKeyAsync("izinli", ["crm.leads.read", "crm.accounts.read"]);
        var client = Host.Factory.KeyClient(key);
        (await client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Rol daralinca anahtar ANINDA daralir (kapsam ∩ olusturanin o anki izinleri).
        await t.Admin.SendOkAsync(HttpMethod.Put, $"{Base}/organization/roles/{integrator}", new { name = "Entegrator", permissions = new[] { "org.integrations.manage", "crm.accounts.read" } }, HttpStatusCode.NoContent);
        await (await client.GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "forbidden");
        (await client.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var current = await client.GetJsonAsync($"{Keys}/current");
        current.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ShouldBe(["crm.accounts.read", "crm.leads.read"]);
        current.GetProperty("effectiveScopes").EnumerateArray().Select(s => s.GetString()).ShouldBe(["crm.accounts.read"]);

        // Olusturan pasiflesince: 401 api_key.owner_inactive; geri acilinca calisir.
        await t.Admin.SendOkAsync(HttpMethod.Patch, $"{Base}/organization/members/{member.UserId}", new { isActive = false }, HttpStatusCode.NoContent);
        await (await client.GetAsync($"{Base}/accounts", Ct)).ShouldBeCodeAsync(HttpStatusCode.Unauthorized, "api_key.owner_inactive");
        await t.Admin.SendOkAsync(HttpMethod.Patch, $"{Base}/organization/members/{member.UserId}", new { isActive = true }, HttpStatusCode.NoContent);
        (await client.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await t.Admin.GetJsonAsync($"{Keys}/{keyId}")).Str("status").ShouldBe("active");
    }

    [Fact]
    public async Task AuthenticationOnlyAcceptsASingleBearerCrmkHeader_AndEveryFailureLooksTheSame()
    {
        var t = await NewTenantAsync("Anahtar Auth");
        var other = await NewTenantAsync("Anahtar Auth B");
        var (_, key, _) = await t.Admin.CreateKeyAsync("k");
        var (_, otherKey, _) = await other.Admin.CreateKeyAsync("k");
        var parts = key.Split('_', 4);
        var otherParts = otherKey.Split('_', 4);

        static string Rebuild(string tenant, string prefix, string secret) => $"crmk_{tenant}_{prefix}_{secret}";
        var forgeries = new[]
        {
            key[..^1] + (key[^1] == 'a' ? "b" : "a"),                                     // yanlis sir
            Rebuild(parts[1], "ffffffff", parts[3]),                                        // bilinmeyen onek
            Rebuild(Guid.NewGuid().ToString("N"), parts[2], parts[3]),                      // bilinmeyen kiracı
            Rebuild(parts[1], parts[2], otherParts[3]),                                     // A'nin oneki + B'nin sirri
            Rebuild(parts[1], otherParts[2], otherParts[3]),                                // B'nin anahtari A'nin kiracı kimligiyle
            Rebuild(otherParts[1], parts[2], parts[3]),                                     // A'nin anahtari B'nin kiracı kimligiyle
            "crmk_short",
            key + "x",
            new string('a', 200),
        };
        var codes = new HashSet<string>();
        var bodies = new HashSet<string>();
        foreach (var forged in forgeries)
        {
            var response = await Host.Factory.KeyClient(forged).GetAsync($"{Base}/leads", Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, forged);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            codes.Add(json.RootElement.GetProperty("code").GetString()!);
            bodies.Add(json.RootElement.GetProperty("title").GetString()! + json.RootElement.GetProperty("detail").GetString());
        }

        codes.ShouldBe(["auth.unauthenticated"]);
        bodies.Count.ShouldBe(1, "unknown prefix, unknown tenant and wrong secret are indistinguishable");

        // X-Api-Key desteklenmez; Bearer olmayan seması; çoklu başlık; kucuk harf bearer.
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/leads"))
        {
            request.Headers.Add("X-Api-Key", key);
            (await Host.Factory.CreateClient().SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/leads"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {key}");
            (await Host.Factory.CreateClient().SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/leads"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", [$"Bearer {key}", $"Bearer {key}"]);
            (await Host.Factory.CreateClient().SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/leads"))
        {
            // Bir JWT + bir anahtar: JWT'ye kacis yok (anahtar semasi secilir, coklu baslik reddedilir).
            request.Headers.TryAddWithoutValidation("Authorization", [$"Bearer {key}", "Bearer " + t.Admin.DefaultRequestHeaders.Authorization!.Parameter]);
            (await Host.Factory.CreateClient().SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Gecerli anahtar; kucuk harfli "bearer" da kabul.
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/leads"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"bearer {key}");
            (await Host.Factory.CreateClient().SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // crmk_ olmayan Bearer JWT'ye gider (mevcut davranis).
        (await t.Admin.GetAsync($"{Base}/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadOnlyScopeReadsButCannotWrite_AndOtherResourcesAreForbidden()
    {
        var t = await NewTenantAsync("Anahtar Yetki");
        var (_, key, _) = await t.Admin.CreateKeyAsync("okuma", ["crm.leads.read"]);
        var client = Host.Factory.KeyClient(key);

        (await client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var (write, json) = await client.SendAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = "Aday", company = "Sirket" });
        await write.ShouldBeCodeAsync(HttpStatusCode.Forbidden, "forbidden");
        json.GetProperty("args").GetProperty("permission").GetString().ShouldBe("crm.leads.write");
        await (await client.GetAsync($"{Base}/accounts", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.GetAsync($"{Base}/reports/sales/funnel", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task WritesByAKey_AreAttributedToTheKey_InTheAuditTrail_AndOwnedByTheCreator()
    {
        var t = await NewTenantAsync("Anahtar Denetim");
        var (keyId, key, _) = await t.Admin.CreateKeyAsync("yazar", ["crm.accounts.write", "crm.accounts.read"]);
        var client = Host.Factory.KeyClient(key);

        var created = await client.SendAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "Anahtar Firmasi" });
        created.Response.StatusCode.ShouldBe(HttpStatusCode.Created, created.Body.ToString());
        var accountId = created.Body.GuidProp("id");
        created.Body.GuidProp("ownerUserId").ShouldBe(t.AdminUserId);

        var row = await factory.ScalarAsync<string>("SELECT api_key_id::text || '|' || user_display_name FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_type = 'Account' AND entity_id = @id", ("t", t.TenantId), ("id", accountId.ToString()));
        row.ShouldStartWith(keyId.ToString());
        row.ShouldEndWith("(API: yazar)");

        // JWT yazmasi: api_key_id bos.
        var viaJwt = (await t.Admin.SendAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "JWT Firmasi" })).Body.GuidProp("id");
        (await factory.ScalarAsync<bool>("SELECT api_key_id IS NULL FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_type = 'Account' AND entity_id = @id", ("t", t.TenantId), ("id", viaJwt.ToString()))).ShouldBeTrue();

        var audit = await t.Admin.GetJsonAsync($"{Base}/audit?entityType=Account&entityId={accountId}");
        audit.GetProperty("items").EnumerateArray().Single().GuidProp("apiKeyId").ShouldBe(keyId);
    }

    [Fact]
    public async Task Revoke_TakesEffectImmediately_ExpiryBoundaryIsExact_DeleteRequiresInactive()
    {
        var t = await NewTenantAsync("Anahtar Iptal");
        var (id, key, _) = await t.Admin.CreateKeyAsync("iptal");
        var client = Host.Factory.KeyClient(key);
        (await client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await (await t.Admin.SendAsync(HttpMethod.Delete, $"{Keys}/{id}")).Response.ShouldBeCodeAsync(HttpStatusCode.Conflict, "api_key.active");
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Keys}/{id}/revoke", null, HttpStatusCode.NoContent);
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Keys}/{id}/revoke", null, HttpStatusCode.NoContent);
        await (await client.GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Unauthorized, "api_key.revoked");
        (await t.Admin.GetJsonAsync($"{Keys}/{id}")).Str("status").ShouldBe("revoked");
        await t.Admin.SendOkAsync(HttpMethod.Patch, $"{Keys}/{id}", new { name = "yeni" }, HttpStatusCode.Conflict);

        // Sure: now >= expires_at -> expired; 1 sn once gecerli.
        var (id2, key2, _) = await t.Admin.CreateKeyAsync("sure");
        var client2 = Host.Factory.KeyClient(key2);
        var now = Host.Clock.GetUtcNow().UtcDateTime;
        await factory.SqlAsync("UPDATE integrations.api_keys SET expires_at = @at WHERE id = @id", ("at", now.AddSeconds(1)), ("id", id2));
        (await client2.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await factory.SqlAsync("UPDATE integrations.api_keys SET expires_at = @at WHERE id = @id", ("at", now), ("id", id2));
        await (await client2.GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Unauthorized, "api_key.expired");
        (await t.Admin.GetJsonAsync($"{Keys}/{id2}")).Str("status").ShouldBe("expired");
        await t.Admin.SendOkAsync(HttpMethod.Delete, $"{Keys}/{id2}", null, HttpStatusCode.NoContent);
        await t.Admin.SendOkAsync(HttpMethod.Delete, $"{Keys}/{id}", null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RevocationIsImmediateInProcess_EvenWithTheDefaultThirtySecondCache()
    {
        await using var cached = TestHost.Create(factory, ("Integrations:ApiKeys:CacheSeconds", "30"));
        var t = await cached.Factory.NewTenantAsync("Onbellek " + Guid.NewGuid().ToString("N")[..6]);
        var (id, key, _) = await t.Admin.CreateKeyAsync("onbellekli");
        var client = cached.Factory.KeyClient(key);
        (await client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Keys}/{id}/revoke", null, HttpStatusCode.NoContent);
        await (await client.GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Unauthorized, "api_key.revoked");
    }

    [Fact]
    public async Task IpAllowList_IsEnforced_ForV4V6_AndAMissingIpIsRejected()
    {
        var t = await NewTenantAsync("Anahtar Ip");
        var (_, key, _) = await t.Admin.CreateKeyAsync("ipli", ["crm.leads.read"], cidrs: ["203.0.113.0/24", "2001:db8:1::/48"]);
        (await Host.Factory.KeyClient(key, "203.0.113.77").GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Host.Factory.KeyClient(key, "2001:db8:1::5").GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Host.Factory.KeyClient(key, "::ffff:203.0.113.9").GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await Host.Factory.KeyClient(key, "198.51.100.1").GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "api_key.ip_not_allowed");
        await (await Host.Factory.KeyClient(key, "203.0.114.1").GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "api_key.ip_not_allowed");
        await (await Host.Factory.KeyClient(key).GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "api_key.ip_not_allowed");

        // IP kisitsiz anahtar her yerden calisir.
        var (_, open, _) = await t.Admin.CreateKeyAsync("acik", ["crm.leads.read"]);
        (await Host.Factory.KeyClient(open, "198.51.100.1").GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EveryEndpointOutsideThePublicCatalog_IsClosedToAKeyWithAllPermittedScopes()
    {
        var t = await NewTenantAsync("Anahtar Kacis");
        var scopes = Host.Factory.Services.GetRequiredService<Sense.Crm.Modules.Integrations.Application.IApiKeyScopeCatalog>().Allowed.ToArray();
        scopes.ShouldNotContain("crm.approvals.decide");
        scopes.Any(s => s.StartsWith("org.", StringComparison.Ordinal)).ShouldBeFalse();
        var (_, key, _) = await t.Admin.CreateKeyAsync("hepsi", scopes);
        var client = Host.Factory.KeyClient(key);

        var endpoints = Host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("api/v", StringComparison.Ordinal))
            .ToList();
        endpoints.Count.ShouldBeGreaterThan(150, "the inventory must actually cover the API surface");

        var violations = new List<string>();
        var checkedOutsideCatalog = 0;
        foreach (var endpoint in endpoints)
        {
            var raw = endpoint.RoutePattern.RawText!;
            var path = "/" + System.Text.RegularExpressions.Regex.Replace(raw, @"\{version[^}]*\}", "1", System.Text.RegularExpressions.RegexOptions.None);
            path = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", _ => Guid.NewGuid().ToString());
            // Katalog + anonim kimlik akisi (/auth/*) + onayli tek istisna: GET /integrations/api-keys/current ([ApiKeyAllowed]).
            if (PublicApiCatalog.Match(path) is not null
                || path.StartsWith("/api/v1/auth/", StringComparison.Ordinal)
                || path.EndsWith("/integrations/api-keys/current", StringComparison.Ordinal))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                checkedOutsideCatalog++;
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT" or "PATCH")
                {
                    request.Content = JsonContent.Create(new { });
                }

                var response = await client.SendAsync(request, Ct);
                if ((int)response.StatusCode is >= 200 and < 300)
                {
                    violations.Add($"{method} {path} -> {(int)response.StatusCode}");
                }
            }
        }

        checkedOutsideCatalog.ShouldBeGreaterThan(60);
        violations.ShouldBeEmpty("A key must never reach an endpoint outside the public catalog: " + string.Join("; ", violations));
    }

    [Fact]
    public async Task IdentityEscapeHatches_AreClosed_ForAKey_EvenWhenTheCreatorIsAnAdministrator()
    {
        var t = await NewTenantAsync("Anahtar Kacis 2");
        var (_, key, _) = await t.Admin.CreateKeyAsync("kacis", ["crm.leads.read", "crm.leads.write"]);
        var client = Host.Factory.KeyClient(key);

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"{Base}/auth/switch-organization", new { organizationId = t.TenantId }),
            (HttpMethod.Get, $"{Base}/me", null),
            (HttpMethod.Patch, $"{Base}/me", new { displayName = "hijack" }),
            (HttpMethod.Post, $"{Base}/me/password", new { currentPassword = "x", newPassword = "Yeni.Sifre.12345" }),
            (HttpMethod.Get, $"{Base}/me/invitations", null),
            (HttpMethod.Get, $"{Base}/organization", null),
            (HttpMethod.Get, $"{Base}/permissions", null),
            (HttpMethod.Get, $"{Base}/organization/members", null),
            (HttpMethod.Get, $"{Base}/organization/roles", null),
            (HttpMethod.Post, $"{Base}/organization/roles", new { name = "x", permissions = new[] { "crm.leads.read" } }),
            (HttpMethod.Get, Keys, null),
            (HttpMethod.Post, Keys, new { name = "child", scopes = new[] { "crm.leads.read" } }),
            (HttpMethod.Get, Wh, null),
            (HttpMethod.Get, $"{PlatformBase}/organizations", null),
            (HttpMethod.Get, $"{Base}/subscription", null),
        };
        foreach (var (method, path, body) in attempts)
        {
            var (response, _) = await client.SendAsync(method, path, body);
            ((int)response.StatusCode).ShouldBeInRange(400, 499, $"{method} {path}");
            response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
        }

        await (await client.GetAsync($"{Base}/me", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "api_key.not_allowed");
        (await client.SendAsync(HttpMethod.Post, $"{Base}/auth/switch-organization", new { organizationId = t.TenantId })).Response.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        // Yalnız anahtarla: /integrations/api-keys/current; JWT ile 403.
        (await client.GetAsync($"{Keys}/current", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await t.Admin.GetAsync($"{Keys}/current", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task APlatformAdministratorsKey_StillCannotReachThePlatformPlane()
    {
        var platform = await Host.Factory.PlatformAdminAsync();
        var me = await platform.GetJsonAsync($"{Base}/me");
        var orgId = me.GetProperty("organization").GetProperty("id").GetGuid();
        var (_, key, _) = await platform.CreateKeyAsync("platform-anahtari", ["crm.leads.read"]);
        var client = Host.Factory.KeyClient(key);
        (await client.GetAsync($"{PlatformBase}/organizations", Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        (await client.GetAsync($"{PlatformBase}/organizations/{orgId}", Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        (await client.GetAsync($"{PlatformBase}/plans", Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        (await platform.GetAsync($"{PlatformBase}/organizations", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Usage_IsCounted_ForReadsToo_FlushIsIdempotent_AndLastUseIsRecorded()
    {
        var t = await NewTenantAsync("Anahtar Kullanim");
        var (id, key, _) = await t.Admin.CreateKeyAsync("sayac", ["crm.leads.read"]);
        var client = Host.Factory.KeyClient(key);
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await client.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var flush = Host.Services.GetServices<IHostedService>().OfType<ApiKeyUsageFlushService>().Single();
        await flush.FlushAsync(Ct);
        await flush.FlushAsync(Ct);

        var usage = await t.Admin.GetJsonAsync($"{Keys}/{id}/usage");
        var today = usage.GetProperty("items").EnumerateArray().Single();
        today.GetProperty("requests").GetInt32().ShouldBe(4);
        today.GetProperty("errors").GetInt32().ShouldBe(1);
        today.GetProperty("throttled").GetInt32().ShouldBe(0);

        var got = await t.Admin.GetJsonAsync($"{Keys}/{id}");
        got.TryGetProperty("lastUsedAt", out _).ShouldBeTrue();
        (await t.Admin.SendAsync(HttpMethod.Get, $"{Keys}/{id}/usage?from=2026-01-01&to=2026-12-31")).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SecretsNeverAppearInLogs_OrProblemDetails()
    {
        var t = await NewTenantAsync("Anahtar Sizinti");
        var (_, key, _) = await t.Admin.CreateKeyAsync("sizinti");
        var secret = key.Split('_', 4)[3];
        var client = Host.Factory.KeyClient(key);
        await client.GetAsync($"{Base}/leads", Ct);
        var forged = await Host.Factory.KeyClient(key[..^1] + "Z").GetAsync($"{Base}/leads", Ct);
        (await forged.Content.ReadAsStringAsync(Ct)).ShouldNotContain(secret);
        var hook = (await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "l", url = "https://hooks.example.com/x", eventTypes = new[] { "lead.created" } })).Body;

        lock (Host.Logs.Lines)
        {
            Host.Logs.Lines.Where(l => l.Contains(secret, StringComparison.Ordinal) || l.Contains(hook.Str("secret"), StringComparison.Ordinal)).ShouldBeEmpty();
            Host.Logs.Lines.Where(l => l.Contains("whsec_", StringComparison.Ordinal) && l.Contains(hook.Str("secret")[6..], StringComparison.Ordinal)).ShouldBeEmpty();
        }
    }
}

/// <summary>Hız sınırı: anahtar başına, kiracı-anahtar toplamı, insan kovalarından ayrı; kimlik doğrulama başarısızlık azaltması.</summary>
[Collection(ApiCollection.Name)]
public sealed class ApiKeyRateLimitTests(CrmApiFactory factory)
{
    [Fact]
    public async Task PerKeyLimit_Returns429WithRetryAfter_AndDoesNotTouchOtherKeysOrTheHumanBucket()
    {
        await using var host = TestHost.Create(factory, ("RateLimiting:ApiKey:PermitLimit", "5"), ("RateLimiting:User:PermitLimit", "1000"));
        var t = await host.Factory.NewTenantAsync("Hiz " + Guid.NewGuid().ToString("N")[..6]);
        var (_, keyA, _) = await t.Admin.CreateKeyAsync("a");
        var (_, keyB, _) = await t.Admin.CreateKeyAsync("b");
        var a = host.Factory.KeyClient(keyA);
        var b = host.Factory.KeyClient(keyB);

        for (var i = 0; i < 5; i++)
        {
            (await a.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await a.GetAsync($"{Base}/leads", Ct);
        await limited.ShouldBeCodeAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await b.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Anahtar trafigi olusturanin insan (kullanici/kiracı) kovasini tuketmez.
        for (var i = 0; i < 10; i++)
        {
            (await t.Admin.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task TenantApiKeyAggregate_Caps_AllKeysTogether()
    {
        await using var host = TestHost.Create(factory, ("RateLimiting:ApiKey:PermitLimit", "100"), ("RateLimiting:ApiKeyTenant:PermitLimit", "6"));
        var t = await host.Factory.NewTenantAsync("Hiz Kiraci " + Guid.NewGuid().ToString("N")[..6]);
        var a = host.Factory.KeyClient((await t.Admin.CreateKeyAsync("a")).Key);
        var b = host.Factory.KeyClient((await t.Admin.CreateKeyAsync("b")).Key);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            codes.Add((await a.GetAsync($"{Base}/leads", Ct)).StatusCode);
            codes.Add((await b.GetAsync($"{Base}/leads", Ct)).StatusCode);
        }

        codes.Count(c => c == HttpStatusCode.OK).ShouldBe(6);
        codes.Count(c => c == HttpStatusCode.TooManyRequests).ShouldBe(2);
        (await t.Admin.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FailedAuthentications_AreThrottledPerIpAndTenant_WithoutTouchingOtherIps()
    {
        await using var host = TestHost.Create(factory, ("Integrations:ApiKeys:FailureThrottle:MaxFailures", "3"));
        var t = await host.Factory.NewTenantAsync("Hiz Auth " + Guid.NewGuid().ToString("N")[..6]);
        var (_, key, _) = await t.Admin.CreateKeyAsync("k");
        var forged = key[..^1] + (key[^1] == 'a' ? "b" : "a");

        for (var i = 0; i < 3; i++)
        {
            (await host.Factory.KeyClient(forged, "203.0.113.9").GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await (await host.Factory.KeyClient(forged, "203.0.113.9").GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");
        await (await host.Factory.KeyClient(key, "203.0.113.9").GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");
        (await host.Factory.KeyClient(key, "198.51.100.7").GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Factory.KeyClient(forged, "198.51.100.7").GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
