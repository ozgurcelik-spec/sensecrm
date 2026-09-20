using System.Net;
using System.Text.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Integrations.Tests.Api.Kit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Integrations.Tests.Api;

/// <summary>Webhook abonelikleri: CRUD sözleşmesi, sır bir kez, doğrulama/SSRF kayıt anı, ad çakışması, izin, kiracı izolasyonu, denetim maskesi.</summary>
[Collection(ApiCollection.Name)]
public sealed class WebhookApiTests(CrmApiFactory factory)
{
    private static object Body(string name, string url = "https://hooks.example.com/crm?token=s3cret", string[]? events = null, bool? enabled = null) =>
        new { name, url, eventTypes = events ?? ["lead.created", "deal.won"], description = "test", enabled };

    [Fact]
    public async Task Create_ReturnsTheSecretExactlyOnce_AndNeverStoresOrShowsItAgain()
    {
        var t = await factory.NewTenantAsync("Wh Sir " + Guid.NewGuid().ToString("N")[..6]);
        var (response, created) = await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("CRM Hook"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, created.ToString());
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        var secret = created.Str("secret");
        secret.ShouldStartWith("whsec_");
        created.Str("secretHint").ShouldBe("…" + secret[^4..]);
        created.GetProperty("secretVersion").GetInt32().ShouldBe(1);
        created.Str("host").ShouldBe("hooks.example.com");
        created.Str("health").ShouldBe("healthy");
        created.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        created.GetProperty("eventTypes").EnumerateArray().Select(e => e.GetString()).ShouldBe(["deal.won", "lead.created"]);

        var id = created.GuidProp("id");
        var got = await t.Admin.GetJsonAsync($"{Wh}/{id}");
        got.TryGetProperty("secret", out _).ShouldBeFalse();
        got.Str("secretHint").ShouldBe(created.Str("secretHint"));
        (await t.Admin.GetJsonAsync(Wh)).GetProperty("items").EnumerateArray().Single().TryGetProperty("secret", out _).ShouldBeFalse();

        // DB: ham sir yok (sifreli), URL sorgu dizgisi yalnizca abonelik satirinda.
        var cipherHasPlaintext = await factory.ScalarAsync<bool>(
            "SELECT position(convert_to(@s, 'UTF8') in secret_enc) > 0 FROM integrations.webhook_subscriptions WHERE id = @id", ("s", secret[6..]), ("id", id));
        cipherHasPlaintext.ShouldBeFalse();
        (await factory.ScalarAsync<int>("SELECT length(secret_enc) FROM integrations.webhook_subscriptions WHERE id = @id", ("id", id))).ShouldBe(12 + 16 + secret.Length);
    }

    [Fact]
    public async Task Create_ValidationErrors_UseTheStandardShape()
    {
        var t = await factory.NewTenantAsync("Wh Dogrulama " + Guid.NewGuid().ToString("N")[..6]);
        var empty = await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "", url = "", eventTypes = Array.Empty<string>() });
        await empty.Response.ShouldBeCodeAsync(HttpStatusCode.BadRequest, "validation");
        empty.Body.GetProperty("errors").TryGetProperty("eventTypes", out _).ShouldBeTrue();
        empty.Body.GetProperty("errors").TryGetProperty("name", out _).ShouldBeTrue();

        foreach (var events in new[] { new[] { "ping" }, ["nope.created"], Enumerable.Range(0, 21).Select(i => "lead.created").ToArray() })
        {
            (await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("x" + Guid.NewGuid().ToString("N")[..5], events: events))).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Theory]
    [InlineData("http://hooks.example.com/x", "scheme")]
    [InlineData("https://user:pw@hooks.example.com/x", "userinfo")]
    [InlineData("https://10.0.0.5/x", "ip_literal")]
    [InlineData("https://169.254.169.254/latest/meta-data", "ip_literal")]
    [InlineData("https://intranet/x", "host")]
    [InlineData("https://db.internal/x", "host")]
    [InlineData("https://hooks.example.com:6379/x", "port")]
    public async Task Create_RejectsUnsafeUrls_WithTheDocumentedReason(string url, string reason)
    {
        var t = await factory.NewTenantAsync("Wh Ssrf " + Guid.NewGuid().ToString("N")[..6]);
        var (response, json) = await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("ssrf", url));
        await response.ShouldBeCodeAsync(HttpStatusCode.BadRequest, "webhook.url_invalid");
        json.GetProperty("args").GetProperty("reason").GetString().ShouldBe(reason);

        // Ayni kural PUT'ta da gecerli.
        var ok = await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("ok"));
        var (put, putJson) = await t.Admin.SendAsync(HttpMethod.Put, $"{Wh}/{ok.Body.GuidProp("id")}", Body("ok", url));
        await put.ShouldBeCodeAsync(HttpStatusCode.BadRequest, "webhook.url_invalid");
        putJson.GetProperty("args").GetProperty("reason").GetString().ShouldBe(reason);
    }

    [Fact]
    public async Task NameConflict_IsCaseInsensitive_PutReplacesFully_EnableDisableAreIdempotent_DeleteRemoves()
    {
        var t = await factory.NewTenantAsync("Wh Crud " + Guid.NewGuid().ToString("N")[..6]);
        var first = (await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("Muhasebe"))).Body;
        var id = first.GuidProp("id");
        (await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("MUHASEBE"))).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var conflict = await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("muhasebe"));
        await conflict.Response.ShouldBeCodeAsync(HttpStatusCode.Conflict, "webhook.name_taken");

        await t.Admin.SendOkAsync(HttpMethod.Put, $"{Wh}/{id}", new { name = "Muhasebe 2", url = "https://erp.example.com/h", eventTypes = new[] { "order.created" }, description = (string?)null, enabled = false }, HttpStatusCode.NoContent);
        var got = await t.Admin.GetJsonAsync($"{Wh}/{id}");
        got.Str("name").ShouldBe("Muhasebe 2");
        got.Str("host").ShouldBe("erp.example.com");
        got.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        got.Str("disabledReason").ShouldBe("manual");
        got.Str("health").ShouldBe("disabled");
        got.TryGetProperty("description", out _).ShouldBeFalse("PUT is a full replace");
        got.GetProperty("eventTypes").EnumerateArray().Select(e => e.GetString()).ShouldBe(["order.created"]);

        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{id}/enable", null, HttpStatusCode.NoContent);
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{id}/enable", null, HttpStatusCode.NoContent);
        (await t.Admin.GetJsonAsync($"{Wh}/{id}")).GetProperty("enabled").GetBoolean().ShouldBeTrue();
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{id}/disable", null, HttpStatusCode.NoContent);
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{id}/disable", null, HttpStatusCode.NoContent);
        (await t.Admin.GetJsonAsync($"{Wh}/{id}")).Str("disabledReason").ShouldBe("manual");

        var filtered = await t.Admin.GetJsonAsync($"{Wh}?enabled=false&eventType=order.created&q=erp&sort=-name");
        filtered.GetProperty("totalCount").GetInt32().ShouldBe(1);

        await t.Admin.SendOkAsync(HttpMethod.Delete, $"{Wh}/{id}", null, HttpStatusCode.NoContent);
        (await t.Admin.GetAsync($"{Wh}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RotateSecret_ReturnsAFreshSecretOnce_HonoursGraceBoundsAndBumpsTheVersion()
    {
        var t = await factory.NewTenantAsync("Wh Rotate " + Guid.NewGuid().ToString("N")[..6]);
        var created = (await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("rot"))).Body;
        var id = created.GuidProp("id");

        var (response, rotated) = await t.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{id}/rotate-secret", new { graceHours = 24 });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        rotated.Str("secret").ShouldNotBe(created.Str("secret"));
        rotated.GetProperty("secretVersion").GetInt32().ShouldBe(2);
        rotated.TryGetProperty("previousSecretExpiresAt", out var expires).ShouldBeTrue();
        expires.GetDateTime().ShouldBeGreaterThan(DateTime.UtcNow.AddHours(23));

        var noGrace = (await t.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{id}/rotate-secret", new { graceHours = 0 })).Body;
        noGrace.TryGetProperty("previousSecretExpiresAt", out _).ShouldBeFalse();
        noGrace.GetProperty("secretVersion").GetInt32().ShouldBe(3);

        foreach (var bad in new[] { -1, 169, 1000 })
        {
            (await t.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{id}/rotate-secret", new { graceHours = bad })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Govdesiz cagri varsayilan 24 saat.
        var defaulted = await t.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{id}/rotate-secret");
        defaulted.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        defaulted.Body.TryGetProperty("previousSecretExpiresAt", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ManagementRequiresThePermission_AndOtherTenantsRecordsAreNotFound()
    {
        var a = await factory.NewTenantAsync("Wh A " + Guid.NewGuid().ToString("N")[..6]);
        var b = await factory.NewTenantAsync("Wh B " + Guid.NewGuid().ToString("N")[..6]);
        var subId = (await a.Admin.SendAsync(HttpMethod.Post, Wh, Body("a-hook"))).Body.GuidProp("id");
        var keyId = (await a.Admin.CreateKeyAsync("a-key")).Id;

        (await factory.CreateClient().GetAsync(Wh, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var standard = await factory.AddMemberAsync(a.Admin, "Uye", await a.Admin.RoleIdAsync("Standard"));
        foreach (var path in new[] { Wh, Keys, Deliveries, $"{Base}/integrations/status", $"{Base}/integrations/webhook-events", $"{Base}/integrations/openapi.json" })
        {
            await (await standard.Client.GetAsync(path, Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        var probes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"{Wh}/{subId}", null),
            (HttpMethod.Put, $"{Wh}/{subId}", Body("x")),
            (HttpMethod.Post, $"{Wh}/{subId}/enable", null),
            (HttpMethod.Post, $"{Wh}/{subId}/disable", null),
            (HttpMethod.Post, $"{Wh}/{subId}/rotate-secret", new { graceHours = 1 }),
            (HttpMethod.Post, $"{Wh}/{subId}/test", null),
            (HttpMethod.Delete, $"{Wh}/{subId}", null),
            (HttpMethod.Get, $"{Keys}/{keyId}", null),
            (HttpMethod.Patch, $"{Keys}/{keyId}", new { name = "hijack" }),
            (HttpMethod.Post, $"{Keys}/{keyId}/revoke", null),
            (HttpMethod.Get, $"{Keys}/{keyId}/usage", null),
            (HttpMethod.Delete, $"{Keys}/{keyId}", null),
            (HttpMethod.Get, $"{Deliveries}/{Guid.NewGuid()}", null),
            (HttpMethod.Post, $"{Deliveries}/{Guid.NewGuid()}/redeliver", null),
        };
        foreach (var (method, path, body) in probes)
        {
            var (response, _) = await b.Admin.SendAsync(method, path, body);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
        }

        (await b.Admin.GetJsonAsync(Wh)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await b.Admin.GetJsonAsync(Keys)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await a.Admin.GetJsonAsync($"{Wh}/{subId}")).Str("name").ShouldBe("a-hook");
    }

    [Fact]
    public async Task AuditRows_MaskTheUrlAndSecret_ButShowTheHost()
    {
        var t = await factory.NewTenantAsync("Wh Audit " + Guid.NewGuid().ToString("N")[..6]);
        var created = (await t.Admin.SendAsync(HttpMethod.Post, Wh, Body("audited", "https://hooks.example.com/p?token=TOPSECRETQUERY"))).Body;
        var id = created.GuidProp("id");
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{id}/rotate-secret", new { graceHours = 1 }, HttpStatusCode.OK);

        var changes = await factory.ScalarAsync<string>(
            "SELECT string_agg(changes::text, ' | ') FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_type = 'WebhookSubscription' AND entity_id = @id", ("t", t.TenantId), ("id", id.ToString()));
        changes.ShouldContain("hooks.example.com");
        changes.ShouldNotContain("TOPSECRETQUERY");
        changes.ShouldNotContain(created.Str("secret"));
        changes.ShouldNotContain("whsec_");
        changes.ShouldContain("\"***\"");
        (await factory.ScalarAsync<int>("SELECT count(*) FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_type = 'WebhookSubscription' AND action = 'updated'", ("t", t.TenantId))).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task EventCatalog_ListsEveryTypeWithAnAvailabilityFlag_AndSamples()
    {
        var (t, _) = await factory.TenantOnPlanAsync("Wh Katalog " + Guid.NewGuid().ToString("N")[..6], "internal");
        var events = await t.Admin.GetJsonAsync($"{Base}/integrations/webhook-events");
        events.GetArrayLength().ShouldBe(11);
        events.EnumerateArray().Select(e => e.Str("type")).ShouldNotContain("ping");
        events.EnumerateArray().ShouldAllBe(e => e.GetProperty("available").GetBoolean());
        var lead = events.EnumerateArray().Single(e => e.Str("type") == "lead.created");
        lead.Str("group").ShouldBe("sales");
        lead.GetProperty("sample").GetProperty("data").GetProperty("source").GetString().ShouldBe("web");
        lead.GetProperty("sample").GetProperty("version").GetInt32().ShouldBe(1);

        var status = await t.Admin.GetJsonAsync($"{Base}/integrations/status");
        status.GetProperty("webhooksEnabled").GetBoolean().ShouldBeTrue();
        status.GetProperty("maxAttempts").GetInt32().ShouldBe(8);
        status.GetProperty("signatureToleranceSeconds").GetInt32().ShouldBe(300);
        status.GetProperty("apiKeys").GetProperty("defaultLifetimeDays").GetInt32().ShouldBe(365);
        status.GetProperty("apiKeys").GetProperty("maxLifetimeDays").GetInt32().ShouldBe(730);
        status.GetProperty("apiKeys").GetProperty("rateLimitPerMinute").GetInt32().ShouldBe(120);
    }
}

/// <summary>M7 uzantısı: <c>integrations</c> kapı modülü, <c>maxWebhooks</c>/<c>maxApiKeys</c> sert limitleri, askı.</summary>
[Collection(ApiCollection.Name)]
public sealed class IntegrationsPlanTests(CrmApiFactory factory)
{
    [Fact]
    public async Task ModuleDisabledPlan_BlocksEveryIntegrationsEndpoint_ReadsIncluded_AndResumesWhenTheModuleReturns()
    {
        await factory.EnsurePlanAsync("m8b_off", """{"maxUsers":null,"maxRecords":{}}""", """{"workflows":true,"commerce":true,"service":true,"marketing":true,"integrations":false}""");
        var (t, platform) = await factory.TenantOnPlanAsync("Plan Kapali " + Guid.NewGuid().ToString("N")[..6], "internal");
        var (id, key, _) = await t.Admin.CreateKeyAsync("k1");
        await platform.PutSubscriptionAsync(t.TenantId, "m8b_off");

        foreach (var path in new[] { Wh, Keys, Deliveries, $"{Base}/integrations/status", $"{Base}/integrations/webhook-events", $"{Base}/integrations/openapi.json" })
        {
            var (response, json) = await t.Admin.SendAsync(HttpMethod.Get, path);
            await response.ShouldBeCodeAsync(HttpStatusCode.Forbidden, "plan.module_disabled");
            json.GetProperty("args").GetProperty("module").GetString().ShouldBe("integrations");
        }

        var (created, _) = await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "x", url = "https://hooks.example.com", eventTypes = new[] { "lead.created" } });
        await created.ShouldBeCodeAsync(HttpStatusCode.Forbidden, "plan.module_disabled");

        // Anahtarla gelen istekler de ayni kodu alir (plan dusunce anahtar durur, silinmez).
        await (await factory.KeyClient(key).GetAsync($"{Base}/leads", Ct)).ShouldBeCodeAsync(HttpStatusCode.Forbidden, "plan.module_disabled");

        await platform.PutSubscriptionAsync(t.TenantId, "internal");
        (await factory.KeyClient(key).GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await t.Admin.GetJsonAsync($"{Keys}/{id}")).Str("status").ShouldBe("active");
    }

    [Fact]
    public async Task MaxWebhooks_IsAHardLimit_DisabledOnesCount_DeletingFreesASlot_SubscriptionEndpointReportsIt()
    {
        await factory.EnsurePlanAsync("m8b_two", """{"maxUsers":null,"maxWebhooks":2,"maxApiKeys":1,"maxRecords":{}}""", AllModulesOn);
        var (t, platform) = await factory.TenantOnPlanAsync("Limit Wh " + Guid.NewGuid().ToString("N")[..6], "m8b_two");
        var create = (string n) => t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = n, url = "https://hooks.example.com/" + n, eventTypes = new[] { "lead.created" }, enabled = n != "b" });

        var a = (await create("a")).Body.GuidProp("id");
        (await create("b")).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (third, json) = await create("c");
        await third.ShouldBeCodeAsync(HttpStatusCode.PaymentRequired, "plan.limit_exceeded");
        json.GetProperty("args").GetProperty("limit").GetString().ShouldBe("webhooks");
        json.GetProperty("args").GetProperty("max").GetInt32().ShouldBe(2);
        json.GetProperty("args").GetProperty("used").GetInt32().ShouldBe(2);

        var subscription = await t.Admin.GetJsonAsync($"{Base}/subscription");
        subscription.GetProperty("limits").GetProperty("maxWebhooks").GetInt32().ShouldBe(2);
        subscription.GetProperty("limits").GetProperty("maxApiKeys").GetInt32().ShouldBe(1);
        subscription.GetProperty("usage").GetProperty("webhooks").GetInt64().ShouldBe(2);

        await t.Admin.SendOkAsync(HttpMethod.Delete, $"{Wh}/{a}", null, HttpStatusCode.NoContent);
        (await create("c")).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ConcurrentCreates_NeverExceedTheLimit_AdvisoryLock()
    {
        await factory.EnsurePlanAsync("m8b_three", """{"maxUsers":null,"maxWebhooks":3,"maxApiKeys":3,"maxRecords":{}}""", AllModulesOn);
        var (t, _) = await factory.TenantOnPlanAsync("Limit Es " + Guid.NewGuid().ToString("N")[..6], "m8b_three");

        var whResults = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "w" + i, url = "https://hooks.example.com/" + i, eventTypes = new[] { "lead.created" } })));
        whResults.Count(r => r.Response.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        whResults.Count(r => r.Response.StatusCode == HttpStatusCode.PaymentRequired).ShouldBe(5);

        var keyResults = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => t.Admin.SendAsync(HttpMethod.Post, Keys, new { name = "k" + i, scopes = new[] { "crm.leads.read" } })));
        keyResults.Count(r => r.Response.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        keyResults.Count(r => r.Response.StatusCode == HttpStatusCode.PaymentRequired).ShouldBe(5);
        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.webhook_subscriptions WHERE tenant_id = @t", ("t", t.TenantId))).ShouldBe(3);
    }

    [Fact]
    public async Task ZeroMeansCannotCreate_RevokedKeysDoNotCount_OverLimitIsReportedOnPlanDowngrade()
    {
        await factory.EnsurePlanAsync("m8b_zero", """{"maxUsers":null,"maxWebhooks":0,"maxApiKeys":0,"maxRecords":{}}""", AllModulesOn);
        await factory.EnsurePlanAsync("m8b_one", """{"maxUsers":null,"maxWebhooks":1,"maxApiKeys":1,"maxRecords":{}}""", AllModulesOn);
        var (t, platform) = await factory.TenantOnPlanAsync("Limit Sifir " + Guid.NewGuid().ToString("N")[..6], "m8b_zero");

        (await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "w", url = "https://hooks.example.com/", eventTypes = new[] { "lead.created" } })).Response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await t.Admin.SendAsync(HttpMethod.Post, Keys, new { name = "k", scopes = new[] { "crm.leads.read" } })).Response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);

        await platform.PutSubscriptionAsync(t.TenantId, "m8b_one");
        var (keyId, _, _) = await t.Admin.CreateKeyAsync("only");
        (await t.Admin.SendAsync(HttpMethod.Post, Keys, new { name = "second", scopes = new[] { "crm.leads.read" } })).Response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        await t.Admin.SendOkAsync(HttpMethod.Post, $"{Keys}/{keyId}/revoke", null, HttpStatusCode.NoContent);
        (await t.Admin.SendAsync(HttpMethod.Post, Keys, new { name = "second", scopes = new[] { "crm.leads.read" } })).Response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Plan dusurme mevcut asimi silmez; overLimit raporlar.
        var (t2, platform2) = await factory.TenantOnPlanAsync("Limit Dus " + Guid.NewGuid().ToString("N")[..6], "internal");
        await t2.Admin.CreateKeyAsync("a");
        await t2.Admin.CreateKeyAsync("b");
        await platform2.PutSubscriptionAsync(t2.TenantId, "m8b_zero");
        var over = (await platform2.SendAsync(HttpMethod.Put, $"{PlatformBase}/organizations/{t2.TenantId}/subscription", new { planCode = "m8b_zero" })).Body.GetProperty("overLimit");
        over.EnumerateArray().Select(o => o.Str("limit")).ShouldContain("api_keys");
        (await t2.Admin.GetJsonAsync(Keys)).GetProperty("totalCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ReadOnlySuspension_AllowsReads_BlocksManagementWrites()
    {
        var (t, platform) = await factory.TenantOnPlanAsync("Askida " + Guid.NewGuid().ToString("N")[..6], "internal");
        var sub = (await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "w", url = "https://hooks.example.com/", eventTypes = new[] { "lead.created" } })).Body.GuidProp("id");
        await platform.SendOkAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{t.TenantId}/suspend", new { reason = "test", mode = "readOnly" }, HttpStatusCode.NoContent);

        (await t.Admin.GetAsync($"{Wh}/{sub}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await t.Admin.GetAsync(Deliveries, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "z", url = "https://hooks.example.com/", eventTypes = new[] { "lead.created" } })).Response.ShouldBeCodeAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        await (await t.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{sub}/rotate-secret", new { graceHours = 1 })).Response.ShouldBeCodeAsync(HttpStatusCode.Forbidden, "tenant.suspended");

        await platform.SendOkAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{t.TenantId}/reactivate", null, HttpStatusCode.NoContent);
        await platform.SendOkAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{t.TenantId}/suspend", new { reason = "test", mode = "blocked" }, HttpStatusCode.NoContent);
        (await t.Admin.GetAsync($"{Wh}/{sub}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
