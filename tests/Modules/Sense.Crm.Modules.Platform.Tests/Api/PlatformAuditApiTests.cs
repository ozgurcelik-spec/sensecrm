using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// Platform denetimi (M7): her konsol komutu aynı işlemde tek <c>platform_audit_entries</c> satırı yazar (başarısız komut yazmaz), <c>details</c> eski→yeni taşır,
/// kiracı denetim tablosuna hiçbir satır düşmez, kiracı iş/kişisel verisi sızmaz; <c>GET /platform/audit</c> filtre/sayfalama; saklama temizliği.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformAuditApiTests(CrmApiFactory factory)
{
    private static readonly string[] AllowedDetailKeys =
    [
        "plan", "trialEndsOn", "overrides", "status", "mode", "reason", "retentionDays", "scheduledFor", "day", "usersActive", "usersPending",
        "from", "to", "rows", "requestId", "report", "step", "attempts",
    ];

    // ---- her komut tek satır ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryConsoleCommand_WritesExactlyOneAuditRow_AttributedToThePlatformAdmin_AndReadOnlyCallsWriteNone()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        await factory.EnsurePlanAsync("console_aud_m7", """{"maxUsers":9,"maxRecords":{}}""", AllModulesOn);
        var org = await factory.SyncedOrgAsync(Token("aud") + " audit");
        var tid = org.TenantId;

        // Salt okunur çağrılar denetlenmez.
        await platform.DetailAsync(tid);
        await platform.GetJsonAsync($"{PlatformBase}/organizations?q={org.Name}");
        await platform.GetJsonAsync($"{OrgUrl(tid)}/usage");
        await platform.GetJsonAsync($"{PlatformBase}/plans");
        await platform.GetJsonAsync($"{PlatformBase}/audit?tenantId={tid}");
        (await factory.AuditCountAsync(tid)).ShouldBe(0);

        // Değişiklik yok: yazma yok.
        await platform.PutSubscriptionAsync(tid, "internal");
        (await factory.AuditCountAsync(tid)).ShouldBe(0);

        var steps = new (string Action, Func<Task> Run)[]
        {
            ("subscription.changed", () => platform.PutSubscriptionAsync(tid, "console_aud_m7", null, new { maxUsers = 3 })),
            ("organization.suspended", () => platform.SuspendAsync(tid)),
            ("organization.reactivated", async () => (await platform.ReactivateRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.NoContent)),
            ("deletion.requested", async () => (await platform.RequestDeletionRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.OK)),
            ("deletion.cancelled", async () => (await platform.CancelDeletionRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.NoContent)),
            ("usage.refreshed", async () => (await platform.RefreshUsageRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.OK)),
        };

        var expectedTotal = 0;
        foreach (var (action, run) in steps)
        {
            await run();
            expectedTotal++;
            (await factory.AuditCountAsync(tid)).ShouldBe(expectedTotal, $"{action}: komut başına tam bir satır");
            (await factory.AuditCountAsync(tid, action)).ShouldBe(1, action);
        }

        // Dışa aktarma da denetlenir (hedef kiracı yok).
        var exportsBefore = await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE action = 'usage.exported'");
        var export = await platform.GetAsync($"{PlatformBase}/usage/export?from=2016-01-01&to=2016-01-31", Ct);
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE action = 'usage.exported'")).ShouldBe(exportsBefore + 1);

        // Satırlar: en yeni önce, aktör = komutu veren platform yöneticisi, hedef kiracı adı anlık görüntü.
        var page = await platform.GetJsonAsync($"{PlatformBase}/audit?tenantId={tid}");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.Str("action")).ShouldBe(["usage.refreshed", "deletion.cancelled", "deletion.requested", "organization.reactivated", "organization.suspended", "subscription.changed"]);
        foreach (var item in items)
        {
            item.GuidProp("actorUserId").ShouldBe(me.UserId);
            item.Str("actorEmail").ShouldBe(me.Email, StringCompareShould.IgnoreCase);
            item.GuidProp("targetTenantId").ShouldBe(tid);
            item.Str("targetTenantName").ShouldBe(org.Name);
            item.GetProperty("details").ValueKind.ShouldBe(JsonValueKind.Object);
        }

        items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).ShouldBe(items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).OrderDescending().ToList());

        // Olay + denetim birlikte yazılır (aynı işlem): olay üreten her komutun satırı vardır.
        (await factory.PlatformEventsAsync(tid, "PlanChanged")).Count.ShouldBe(1);
        (await factory.PlatformEventsAsync(tid, "TenantSuspended")).Count.ShouldBe(1);
        (await factory.PlatformEventsAsync(tid, "TenantReactivated")).Count.ShouldBe(1);
        (await factory.PlatformEventsAsync(tid, "TenantDeletionRequested")).Count.ShouldBe(1);
        (await factory.PlatformEventsAsync(tid, "TenantDeletionCancelled")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFailingCommand_WritesNoAuditRow_AndNoEvent()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("fail") + " failing");
        var tid = org.TenantId;
        var unknown = Guid.NewGuid();

        (await platform.ReactivateRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await platform.CancelDeletionRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await platform.PutSubscriptionRawAsync(tid, "console_no_such_plan_m7")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await platform.PutSubscriptionRawAsync(tid, "internal", TenantToday(-4).Day())).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await platform.PutSubscriptionRawAsync(tid, "internal", overrides: new { maxUsers = -1 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await platform.SuspendRawAsync(tid, reason: "")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await platform.SuspendRawAsync(tid, mode: "frozen")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await platform.RequestDeletionRawAsync(tid, retentionDays: 3)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await platform.SuspendRawAsync(unknown)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await platform.RequestDeletionRawAsync(unknown)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await platform.RefreshUsageRawAsync(unknown)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await platform.GetAsync($"{PlatformBase}/usage/export?from=2020-02-01&to=2019-02-01", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await factory.AuditCountAsync(tid)).ShouldBe(0);
        (await factory.AuditCountAsync(unknown)).ShouldBe(0);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.outbox_messages WHERE tenant_id = @t", ("t", tid))).ShouldBe(0, "başarısız komut olay da bırakmaz");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE action = 'usage.exported' AND details->>'from' = '2020-02-01'")).ShouldBe(0);

        // Tanımsız geçişler de satır yazmaz (silme bekleyen kiracıda).
        (await platform.RequestDeletionRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.AuditCountAsync(tid)).ShouldBe(1);
        (await platform.RequestDeletionRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await platform.RefreshUsageRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await platform.PutSubscriptionRawAsync(tid, "internal", overrides: new { maxUsers = 3 })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await platform.SuspendRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await factory.AuditCountAsync(tid)).ShouldBe(1);
        (await factory.PlatformEventsAsync(tid, "TenantDeletionRequested")).Count.ShouldBe(1);
    }

    // ---- details: eski -> yeni ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SubscriptionChanged_Details_CarryOldAndNewForPlanTrialAndOverrides()
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_aud_details_m7", """{"maxUsers":9,"maxRecords":{}}""", AllModulesOn);
        var org = await factory.SyncedOrgAsync(Token("dts") + " details");
        var trialOn = TenantToday(10);

        await platform.PutSubscriptionAsync(org.TenantId, "console_aud_details_m7", trialOn.Day(), new { maxUsers = 3 });
        await platform.PutSubscriptionAsync(org.TenantId, "console_aud_details_m7", null, new { maxUsers = 5 });

        var details = await factory.AuditDetailsAsync(org.TenantId, "subscription.changed");
        details.Count.ShouldBe(2);
        var first = details[0];
        first.GetProperty("plan").Str("old").ShouldBe("internal");
        first.GetProperty("plan").Str("new").ShouldBe("console_aud_details_m7");
        first.GetProperty("trialEndsOn").GetProperty("old").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("trialEndsOn").Str("new").ShouldBe(trialOn.Day());
        first.GetProperty("overrides").GetProperty("old").ValueKind.ShouldBe(JsonValueKind.Null);
        first.GetProperty("overrides").GetProperty("new").GetProperty("maxUsers").GetInt32().ShouldBe(3);

        var second = details[1];
        second.GetProperty("plan").Str("old").ShouldBe("console_aud_details_m7");
        second.GetProperty("plan").Str("new").ShouldBe("console_aud_details_m7");
        second.GetProperty("trialEndsOn").Str("old").ShouldBe(trialOn.Day());
        second.GetProperty("trialEndsOn").GetProperty("new").ValueKind.ShouldBe(JsonValueKind.Null);
        second.GetProperty("overrides").GetProperty("old").GetProperty("maxUsers").GetInt32().ShouldBe(3);
        second.GetProperty("overrides").GetProperty("new").GetProperty("maxUsers").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task LifecycleAudit_Details_CarryTheStatusTransitionsReasonsAndSchedule()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("lfc") + " lifecycle");
        var tid = org.TenantId;

        await platform.SuspendAsync(tid, "musteri talebi", "blocked");
        (await platform.ReactivateRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var requested = await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(tid)}/deletion-request", new { reason = "kapatma", retentionDays = 45 }, HttpStatusCode.OK);
        (await platform.CancelDeletionRawAsync(tid)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var refreshed = await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(tid)}/usage/refresh", null, HttpStatusCode.OK);

        var suspended = (await factory.AuditDetailsAsync(tid, "organization.suspended")).Single();
        suspended.GetProperty("status").Str("old").ShouldBe("active");
        suspended.GetProperty("status").Str("new").ShouldBe("suspended");
        suspended.Str("mode").ShouldBe("blocked");
        suspended.Str("reason").ShouldBe("musteri talebi");

        var reactivated = (await factory.AuditDetailsAsync(tid, "organization.reactivated")).Single();
        reactivated.GetProperty("status").Str("old").ShouldBe("suspended");
        reactivated.GetProperty("status").Str("new").ShouldBe("active");

        var deletion = (await factory.AuditDetailsAsync(tid, "deletion.requested")).Single();
        deletion.GetProperty("status").Str("old").ShouldBe("active");
        deletion.GetProperty("status").Str("new").ShouldBe("pending_deletion");
        deletion.GetProperty("retentionDays").GetInt32().ShouldBe(45);
        deletion.Str("reason").ShouldBe("kapatma");
        deletion.GetProperty("scheduledFor").GetDateTimeOffset().ShouldBe(requested.GetProperty("scheduledFor").GetDateTimeOffset(), TimeSpan.FromSeconds(1));

        var cancelled = (await factory.AuditDetailsAsync(tid, "deletion.cancelled")).Single();
        cancelled.GetProperty("status").Str("old").ShouldBe("pending_deletion");
        cancelled.GetProperty("status").Str("new").ShouldBe("active");

        var usage = (await factory.AuditDetailsAsync(tid, "usage.refreshed")).Single();
        usage.GetProperty("usersActive").GetInt32().ShouldBe(refreshed.GetProperty("items")[0].GetProperty("usersActive").GetInt32());
        usage.GetProperty("usersPending").GetInt32().ShouldBe(0);
        usage.TryGetProperty("day", out _).ShouldBeTrue();
    }

    // ---- kiracı denetimine yazılmaz -------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlatformCommands_NeverWriteToTheTenantAuditLog_OrTheTenantsOwnAuditEndpoint()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        await factory.EnsurePlanAsync("console_aud_tenant_m7", """{"maxUsers":9,"maxRecords":{}}""", AllModulesOn);
        var org = await factory.SyncedOrgAsync(Token("tad") + " tenant audit");
        var ownAuditBefore = (await org.Admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100")).GetProperty("total").GetInt64();
        var opsAuditBefore = (await platform.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100")).GetProperty("total").GetInt64();
        const string CountSql = "SELECT count(*) FROM audit.audit_log_entries WHERE tenant_id = @a OR tenant_id = @b";
        var tableBefore = await factory.ScalarAsync<long>(CountSql, ("a", org.TenantId), ("b", me.TenantId));

        await platform.PutSubscriptionAsync(org.TenantId, "console_aud_tenant_m7", null, new { maxUsers = 3 });
        await platform.SuspendAsync(org.TenantId);
        (await platform.ReactivateRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await platform.RequestDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.CancelDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await platform.RefreshUsageRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await factory.AuditCountAsync(org.TenantId)).ShouldBe(6, "platform denetimi ayrı tabloda");
        (await factory.ScalarAsync<long>(CountSql, ("a", org.TenantId), ("b", me.TenantId))).ShouldBe(tableBefore, "audit.audit_log_entries'e platform satırı düşmez");
        (await org.Admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100")).GetProperty("total").GetInt64().ShouldBe(ownAuditBefore);
        (await platform.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100")).GetProperty("total").GetInt64().ShouldBe(opsAuditBefore, "platform yöneticisinin kendi organizasyonuna da yanlış satır düşmez");

        var audit = await org.Admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100");
        var platformTypes = new[] { "TenantAccount", "DeletionRequest", "Plan", "UsageSnapshot", "PlatformAuditEntry" };
        audit.GetProperty("items").EnumerateArray().Select(i => i.Str("entityType")).Intersect(platformTypes).ShouldBeEmpty();
        (await factory.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_log_entries WHERE entity_type IN ('TenantAccount','DeletionRequest','Plan','UsageSnapshot','PlatformAuditEntry')")).ShouldBe(0);
    }

    [Fact]
    public async Task AuditDetails_NeverContainTheTenantsBusinessOrPersonalData()
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_aud_pii_m7", """{"maxUsers":9,"maxRecords":{}}""", AllModulesOn);
        var t = Token("pii");
        var org = await factory.SyncedOrgAsync($"{t} pii org");
        var secretAccount = $"GizliMusteri-{t}";
        var secretContactEmail = $"kisi-{t}@ornek-musteri.example";
        var secretLead = $"GizliAday-{t}";
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/accounts", new { name = secretAccount }, HttpStatusCode.Created);
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/contacts", new { firstName = "Gizli", lastName = $"Kisi{t}", email = secretContactEmail }, HttpStatusCode.Created);
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = secretLead, company = $"Sirket-{t}" }, HttpStatusCode.Created);

        await platform.PutSubscriptionAsync(org.TenantId, "console_aud_pii_m7", TenantToday(9).Day(), new { maxUsers = 20 });
        await platform.SuspendAsync(org.TenantId, "guvenlik incelemesi");
        (await platform.ReactivateRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await platform.RefreshUsageRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.RequestDeletionRawAsync(org.TenantId, "kapatma", 30)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.CancelDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var allDetails = await factory.TextsAsync(
            "SELECT details::text FROM platform.platform_audit_entries WHERE target_tenant_id = @t ORDER BY occurred_at", ("t", org.TenantId));
        allDetails.Count.ShouldBe(6);
        var personal = new[] { secretAccount, secretContactEmail, secretLead, $"Kisi{t}", $"Sirket-{t}", org.AdminEmail, $"Test {org.Name}" };
        foreach (var json in allDetails)
        {
            foreach (var secret in personal)
            {
                json.ShouldNotContain(secret, Case.Insensitive);
            }

            var keys = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToList();
            keys.ShouldAllBe(k => AllowedDetailKeys.Contains(k), $"beklenmeyen details anahtarı: {json}");
        }
    }

    // ---- GET /platform/audit ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListAudit_FiltersByTenantActionAndActor_NewestFirst_AndPages()
    {
        var adminA = await factory.PlatformAdminAsync();
        var adminB = await factory.PlatformAdminAsync();
        var a = await adminA.WhoAmIAsync();
        var b = await adminB.WhoAmIAsync();
        await factory.EnsurePlanAsync("console_aud_filter_m7", """{"maxUsers":9,"maxRecords":{}}""", AllModulesOn);
        var t1 = await factory.SyncedOrgAsync(Token("fl1") + " one");
        var t2 = await factory.SyncedOrgAsync(Token("fl2") + " two");

        await adminA.SuspendAsync(t1.TenantId, "birinci");
        await adminB.ReactivateRawAsync(t1.TenantId);
        await adminA.PutSubscriptionAsync(t1.TenantId, "console_aud_filter_m7");
        await adminB.SuspendAsync(t2.TenantId, "ikinci");

        var forT1 = await adminA.GetJsonAsync($"{PlatformBase}/audit?tenantId={t1.TenantId}");
        forT1.GetProperty("totalCount").GetInt64().ShouldBe(3);
        forT1.GetProperty("items").EnumerateArray().Select(i => i.Str("action")).ShouldBe(["subscription.changed", "organization.reactivated", "organization.suspended"], "en yeni önce");

        async Task<List<string>> Actions(string query) =>
            (await adminA.GetJsonAsync($"{PlatformBase}/audit?{query}")).GetProperty("items").EnumerateArray().Select(i => i.Str("action")).ToList();

        (await Actions($"tenantId={t1.TenantId}&action=organization.suspended")).ShouldBe(["organization.suspended"]);
        (await Actions($"tenantId={t1.TenantId}&actorUserId={b.UserId}")).ShouldBe(["organization.reactivated"]);
        (await Actions($"actorUserId={a.UserId}")).ShouldBe(["subscription.changed", "organization.suspended"], "A yalnız iki komut verdi");
        (await Actions($"actorUserId={b.UserId}&action=organization.suspended")).ShouldBe(["organization.suspended"]);
        (await Actions($"tenantId={t2.TenantId}&actorUserId={a.UserId}")).ShouldBeEmpty();
        (await Actions($"tenantId={Guid.NewGuid()}")).ShouldBeEmpty();

        // Sayfalama: sayfa boyu 2, toplam 3, en yeni önce.
        var page1 = await adminA.GetJsonAsync($"{PlatformBase}/audit?tenantId={t1.TenantId}&pageSize=2&page=1");
        var page2 = await adminA.GetJsonAsync($"{PlatformBase}/audit?tenantId={t1.TenantId}&pageSize=2&page=2");
        page1.GetProperty("items").GetArrayLength().ShouldBe(2);
        page2.GetProperty("items").GetArrayLength().ShouldBe(1);
        page2.GetProperty("totalCount").GetInt64().ShouldBe(3);
        page2.GetProperty("items")[0].Str("action").ShouldBe("organization.suspended");

        // Gün süzgeci (UTC günü, dahil).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (await Actions($"tenantId={t1.TenantId}&from={today.AddDays(-1).Day()}&to={today.AddDays(1).Day()}")).Count.ShouldBe(3);
        (await Actions($"tenantId={t1.TenantId}&from={today.AddDays(2).Day()}")).ShouldBeEmpty();
        (await Actions($"tenantId={t1.TenantId}&to={today.AddDays(-2).Day()}")).ShouldBeEmpty();
        var reversed = await adminA.GetAsync($"{PlatformBase}/audit?from={today.Day()}&to={today.AddDays(-1).Day()}", Ct);
        (await reversed.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("to", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ListAudit_DayFilter_IsInclusiveOfBothDaysInUtc()
    {
        var platform = await factory.PlatformAdminAsync();
        var synthetic = Guid.NewGuid();
        var moments = new[]
        {
            new DateTime(2020, 1, 15, 23, 59, 59, DateTimeKind.Utc),
            new DateTime(2020, 1, 16, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2020, 1, 16, 23, 59, 59, DateTimeKind.Utc),
            new DateTime(2020, 1, 17, 0, 0, 0, DateTimeKind.Utc),
        };
        foreach (var moment in moments)
        {
            await InsertAuditAsync(synthetic, moment);
        }

        async Task<List<DateTimeOffset>> Times(string query) =>
            (await platform.GetJsonAsync($"{PlatformBase}/audit?tenantId={synthetic}&{query}")).GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).ToList();

        (await Times("from=2020-01-16&to=2020-01-16")).ShouldBe([new DateTimeOffset(moments[2]), new DateTimeOffset(moments[1])]);
        (await Times("from=2020-01-16")).Count.ShouldBe(3);
        (await Times("to=2020-01-16")).Count.ShouldBe(3);
        (await Times(string.Empty)).Count.ShouldBe(4);
        (await Times("pageSize=2&page=2")).ShouldBe([new DateTimeOffset(moments[1]), new DateTimeOffset(moments[0])]);
    }

    // ---- saklama temizliği ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetentionCleanup_TheSnapshotJobPurgesAuditRowsOlderThanTheConfiguredRetention_AndKeepsTheRest()
    {
        var synthetic = Guid.NewGuid();
        await using var host = new ConsoleClockHost(factory, b => b.UseSetting("Platform:Audit:RetentionDays", "30"));
        var now = host.Clock.GetUtcNow().UtcDateTime;
        var tooOld = await InsertAuditAsync(synthetic, now.AddDays(-31));
        var justInside = await InsertAuditAsync(synthetic, now.AddDays(-30).AddHours(1));
        var recent = await InsertAuditAsync(synthetic, now.AddDays(-5));
        var recentGlobal = await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE occurred_at > now() - interval '1 day'");

        // Varsayılan yapılandırma (1825 gün) 31 günlük satırı korur.
        await factory.Services.GetRequiredService<UsageSnapshotJob>().RunOnceAsync(Ct);
        (await AuditExistsAsync(tooOld)).ShouldBeTrue("varsayılan saklama 5 yıl");

        var run = await host.Services.GetRequiredService<UsageSnapshotJob>().RunOnceAsync(Ct);

        run.AuditPurged.ShouldBeGreaterThanOrEqualTo(1);
        (await AuditExistsAsync(tooOld)).ShouldBeFalse("30 günden eski satır silinir");
        (await AuditExistsAsync(justInside)).ShouldBeTrue();
        (await AuditExistsAsync(recent)).ShouldBeTrue();
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE occurred_at > now() - interval '1 day'")).ShouldBe(recentGlobal, "yakın tarihli denetim satırları kalır");
    }

    private async Task<Guid> InsertAuditAsync(Guid tenantId, DateTime occurredAtUtc)
    {
        var id = Guid.CreateVersion7();
        (await factory.SqlAsync(
            """
            INSERT INTO platform.platform_audit_entries (id, occurred_at, action, target_tenant_id, target_tenant_name, details)
            VALUES (@id, @at, 'subscription.changed', @t, 'Sentetik', '{}'::jsonb)
            """,
            ("id", id), ("at", occurredAtUtc), ("t", tenantId))).ShouldBe(1);
        return id;
    }

    private async Task<bool> AuditExistsAsync(Guid id) =>
        await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE id = @id", ("id", id)) == 1;
}
