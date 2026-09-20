using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Application.Provisioning;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// Platform konsolu (M7): organizasyon listesi/detayı, abonelik değiştirme, askı, silme talebi, plan kataloğu, organizasyon açma ve
/// <c>OrganizationCreated/Updated</c> → hesap. Veritabanı testler arasında sıfırlanmaz; her test benzersiz belirteçli kendi organizasyonlarını açar ve
/// listeleri <c>q</c> ile daraltır (kapsama doğrulaması, eşitlik değil).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformConsoleApiTests(CrmApiFactory factory)
{
    // ---- GET /platform/organizations: filtre, etkin durum tutarlılığı ------------------------------------------------------

    [Fact]
    public async Task List_EffectiveStatus_IsTheSameInTheRow_TheDetail_AndTheStatusFilter()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("sts");
        var active = await factory.SyncedOrgAsync($"{t} active");
        var trial = await factory.SyncedOrgAsync($"{t} trial");
        var expired = await factory.SyncedOrgAsync($"{t} expired");
        var suspended = await factory.SyncedOrgAsync($"{t} suspended");
        var pending = await factory.SyncedOrgAsync($"{t} pending");
        var deleted = await factory.SyncedOrgAsync($"{t} deleted");

        // Saat bağımsız kurulum: deneme bitişleri veritabanı saatine göre ±gün (sahte saat gerekmez).
        await factory.SetAccountAsync(factory, trial.TenantId, "trial_ends_on = current_date + 5, trial_ends_at = now() + interval '5 days'");
        await factory.SetAccountAsync(factory, expired.TenantId, "trial_ends_on = current_date - 3, trial_ends_at = now() - interval '2 days'");
        await factory.SetAccountAsync(factory, suspended.TenantId, "trial_ends_on = current_date - 3, trial_ends_at = now() - interval '2 days'");
        await platform.SuspendAsync(suspended.TenantId); // askı, deneme bitişinden önceliklidir
        (await platform.RequestDeletionRawAsync(pending.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await factory.SetAccountAsync(factory, deleted.TenantId, "status = 'deleted'");

        var expectations = new (TestOrg Org, string Status, string Access)[]
        {
            (active, "active", "full"),
            (trial, "trial", "full"),
            (expired, "trial_expired", "readOnly"),
            (suspended, "suspended", "readOnly"),
            (pending, "pending_deletion", "none"),
            (deleted, "deleted", "none"),
        };

        var list = await platform.GetJsonAsync($"{PlatformBase}/organizations?q={t}&pageSize=100");
        list.GetProperty("totalCount").GetInt64().ShouldBe(6);
        foreach (var (org, status, access) in expectations)
        {
            var row = list.GetProperty("items").EnumerateArray().Single(r => r.GuidProp("tenantId") == org.TenantId);
            row.Str("status").ShouldBe(status, $"liste satırı: {org.Name}");
            row.Str("accessLevel").ShouldBe(access);

            var detail = await platform.DetailAsync(org.TenantId);
            detail.Str("status").ShouldBe(status, $"detay: {org.Name}");
            detail.Str("accessLevel").ShouldBe(access);

            // Aynı ifade filtrede de kullanılır: yalnız bu durumdaki organizasyon döner.
            (await platform.ListIdsAsync($"q={t}&status={status}")).ShouldBe([org.TenantId]);
        }

        var trialRow = list.GetProperty("items").EnumerateArray().Single(r => r.GuidProp("tenantId") == trial.TenantId);
        trialRow.Str("trialEndsOn").ShouldBe(await factory.ScalarAsync<string>("SELECT trial_ends_on::text FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", trial.TenantId)));
        var activeRow = list.GetProperty("items").EnumerateArray().Single(r => r.GuidProp("tenantId") == active.TenantId);
        activeRow.TryGetProperty("trialEndsOn", out _).ShouldBeFalse("null alanlar yazılmaz");
        activeRow.TryGetProperty("usage", out _).ShouldBeFalse("anlık görüntü yoksa usage yazılmaz");
        activeRow.GetProperty("isSystem").GetBoolean().ShouldBeFalse();
        activeRow.Str("planCode").ShouldBe("internal");
        activeRow.Str("source").ShouldBe("signup");
        activeRow.Str("name").ShouldBe($"{t} active");
    }

    [Fact]
    public async Task List_FiltersByPlan_AndSource_AndRejectsUnknownFilterValues()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("flt");
        await factory.EnsurePlanAsync("console_flt_a_m7", """{"maxUsers":9,"maxRecords":{}}""", AllModulesOn);
        await factory.EnsurePlanAsync("console_flt_b_m7", """{"maxUsers":9,"maxRecords":{}}""", AllModulesOff);
        var a = await factory.SyncedOrgAsync($"{t} a");
        var b = await factory.SyncedOrgAsync($"{t} b");
        await platform.PutSubscriptionAsync(a.TenantId, "console_flt_a_m7");
        await platform.PutSubscriptionAsync(b.TenantId, "console_flt_b_m7");

        var created = await platform.SendJsonAsync(
            HttpMethod.Post,
            $"{PlatformBase}/organizations",
            new { organizationName = $"{t} c", adminDisplayName = "Yonetici C", adminEmail = UniqueEmail("flt-c"), adminPassword = (string?)null, locale = "tr" },
            HttpStatusCode.Created);
        var c = created.GuidProp("organizationId");
        await factory.DrainOutboxesAsync();

        (await platform.ListIdsAsync($"q={t}&planCode=console_flt_a_m7")).ShouldBe([a.TenantId]);
        (await platform.ListIdsAsync($"q={t}&planCode=console_flt_b_m7")).ShouldBe([b.TenantId]);
        (await platform.ListIdsAsync($"q={t}&planCode=internal")).ShouldBe([c]);
        (await platform.ListIdsAsync($"q={t}&source=platform")).ShouldBe([c]);
        (await platform.ListIdsAsync($"q={t}&source=signup")).ShouldBe([a.TenantId, b.TenantId], ignoreOrder: true);
        (await platform.ListIdsAsync($"q={t}&source=signup&planCode=console_flt_b_m7")).ShouldBe([b.TenantId]);
        (await platform.ListIdsAsync($"q={t}&planCode=does_not_exist")).ShouldBeEmpty();

        var badStatus = await platform.GetAsync($"{PlatformBase}/organizations?status=bogus", Ct);
        (await badStatus.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("status", out _).ShouldBeTrue();
        var badSource = await platform.GetAsync($"{PlatformBase}/organizations?source=bogus", Ct);
        (await badSource.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("source", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task List_Search_MatchesNameAndSlug_AndTreatsLikeWildcardsLiterally()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("lik");
        var percent = await factory.SyncedOrgAsync($"{t}100% off");
        var plain = await factory.SyncedOrgAsync($"{t}100x off");
        var underscore = await factory.SyncedOrgAsync($"{t}snake_case");
        var letter = await factory.SyncedOrgAsync($"{t}snakeXcase");
        var backslash = await factory.SyncedOrgAsync($@"{t}back\slash");
        var all = new[] { percent, plain, underscore, letter, backslash }.Select(o => o.TenantId).ToHashSet();

        (await platform.ListIdsAsync($"q={t}")).ShouldBe(all, ignoreOrder: true, "belirteç ad/slug içinde geçen hepsini bulur");
        (await platform.ListIdsAsync($"q={Uri.EscapeDataString(t + "100%")}")).ShouldBe([percent.TenantId], "% joker değil, harfi harfine");
        (await platform.ListIdsAsync($"q={Uri.EscapeDataString(t + "snake_case")}")).ShouldBe([underscore.TenantId], "_ joker değil, harfi harfine");
        (await platform.ListIdsAsync($"q={Uri.EscapeDataString(t + @"back\slash")}")).ShouldBe([backslash.TenantId], @"\ kaçış karakteri de harfi harfine");
        (await platform.ListIdsAsync($"q={t.ToUpperInvariant()}")).ShouldBe(all, ignoreOrder: true, "arama büyük/küçük harf duyarsız");

        // Slug ile arama: ad "T100% off" → slug "t100-off".
        var slug = (await platform.DetailAsync(plain.TenantId)).Str("slug");
        (await platform.ListIdsAsync($"q={Uri.EscapeDataString(slug)}")).ShouldContain(plain.TenantId);
    }

    // ---- GET /platform/organizations: sıralama ve sayfalama ---------------------------------------------------------------

    [Fact]
    public async Task List_SortsByName_ByDefault_AndByCreatedAt_Plan_AndStatus()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("srt");
        await factory.EnsurePlanAsync("console_srt_b_m7", NoLimits, AllModulesOn);
        await factory.EnsurePlanAsync("console_srt_s_m7", NoLimits, AllModulesOn);
        var b = await factory.SyncedOrgAsync($"{t} B");
        var a = await factory.SyncedOrgAsync($"{t} A");
        var c = await factory.SyncedOrgAsync($"{t} C");

        // createdAt: C < A < B (açılış sırasından bağımsız, elle).
        await factory.SetAccountAsync(factory, c.TenantId, "tenant_created_at = @v", ("v", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await factory.SetAccountAsync(factory, a.TenantId, "tenant_created_at = @v", ("v", new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        await factory.SetAccountAsync(factory, b.TenantId, "tenant_created_at = @v", ("v", new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
        // plan adı: console_srt_b_m7 (ad = kod) < console_srt_s_m7; durum: active < suspended < trial.
        await platform.PutSubscriptionAsync(a.TenantId, "console_srt_s_m7");
        await platform.PutSubscriptionAsync(b.TenantId, "console_srt_b_m7");
        await platform.PutSubscriptionAsync(c.TenantId, "console_srt_b_m7");
        await factory.SetAccountAsync(factory, a.TenantId, "trial_ends_on = current_date + 5, trial_ends_at = now() + interval '5 days'"); // trial
        await platform.SuspendAsync(c.TenantId); // suspended; b active

        (await platform.ListNamesAsync($"q={t}")).ShouldBe([$"{t} A", $"{t} B", $"{t} C"], "varsayılan: ada göre artan");
        (await platform.ListNamesAsync($"q={t}&sort=name")).ShouldBe([$"{t} A", $"{t} B", $"{t} C"]);
        (await platform.ListNamesAsync($"q={t}&sort=-name")).ShouldBe([$"{t} C", $"{t} B", $"{t} A"]);
        (await platform.ListNamesAsync($"q={t}&sort=createdAt")).ShouldBe([$"{t} C", $"{t} A", $"{t} B"]);
        (await platform.ListNamesAsync($"q={t}&sort=-createdAt")).ShouldBe([$"{t} B", $"{t} A", $"{t} C"]);
        (await platform.ListNamesAsync($"q={t}&sort=-plan,name")).First().ShouldBe($"{t} A", "plan adına göre azalan: console_srt_s_m7 önce");
        (await platform.ListNamesAsync($"q={t}&sort=plan,name")).ShouldBe([$"{t} B", $"{t} C", $"{t} A"]);
        (await platform.ListNamesAsync($"q={t}&sort=status")).ShouldBe([$"{t} B", $"{t} C", $"{t} A"], "active < suspended < trial");
        (await platform.ListNamesAsync($"q={t}&sort=-status")).ShouldBe([$"{t} A", $"{t} C", $"{t} B"]);
        (await platform.ListNamesAsync($"q={t}&sort=bogusField")).ShouldBe([$"{t} A", $"{t} B", $"{t} C"], "bilinmeyen alan yok sayılır, ada göre kararlı");
    }

    [Fact]
    public async Task List_SortByUsers_UsesTheLatestSnapshot_AndPutsOrganizationsWithoutOnesLastInBothDirections()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("usr");
        var none1 = await factory.SyncedOrgAsync($"{t} none1");
        var few = await factory.SyncedOrgAsync($"{t} few");
        var many = await factory.SyncedOrgAsync($"{t} many");
        var none2 = await factory.SyncedOrgAsync($"{t} none2");

        // few: eski gün 9 kullanıcı, en son gün 1 → "son anlık görüntü" 1 sayılır.
        await factory.InsertSnapshotAsync(few.TenantId, new DateOnly(2020, 5, 1), 9, 0, """{"sales.records": 1}""");
        await factory.InsertSnapshotAsync(few.TenantId, new DateOnly(2020, 5, 2), 1, 0, """{"sales.records": 1}""");
        await factory.InsertSnapshotAsync(many.TenantId, new DateOnly(2020, 5, 2), 5, 2, """{"sales.records": 7, "activities.records": 3}""");

        var ascending = await platform.ListIdsAsync($"q={t}&sort=users");
        ascending.Take(2).ShouldBe([few.TenantId, many.TenantId]);
        ascending.Skip(2).ShouldBe([none1.TenantId, none2.TenantId], ignoreOrder: true, "boşlar sonda");

        var descending = await platform.ListIdsAsync($"q={t}&sort=-users");
        descending.Take(2).ShouldBe([many.TenantId, few.TenantId]);
        descending.Skip(2).ShouldBe([none1.TenantId, none2.TenantId], ignoreOrder: true, "boşlar azalan sırada da sonda");

        // Satırda son anlık görüntü özeti: kullanıcılar + modül kayıt toplamları.
        var page = await platform.GetJsonAsync($"{PlatformBase}/organizations?q={t}&sort=-users");
        var row = page.GetProperty("items").EnumerateArray().Single(r => r.GuidProp("tenantId") == many.TenantId);
        var usage = row.GetProperty("usage");
        usage.Str("day").ShouldBe("2020-05-02");
        usage.GetProperty("usersActive").GetInt32().ShouldBe(5);
        usage.GetProperty("usersPending").GetInt32().ShouldBe(2);
        usage.GetProperty("records").GetProperty("sales").GetInt64().ShouldBe(7);
        usage.GetProperty("records").GetProperty("activities").GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task List_Paging_ReturnsStablePages_TotalCount_AndClampsThePageSize()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("pag");
        var orgs = new List<TestOrg>();
        for (var i = 1; i <= 5; i++)
        {
            orgs.Add(await factory.SyncedOrgAsync($"{t} n{i}"));
        }

        var seen = new List<string>();
        foreach (var (page, expected) in new[] { (1, 2), (2, 2), (3, 1), (4, 0) })
        {
            var result = await platform.GetJsonAsync($"{PlatformBase}/organizations?q={t}&pageSize=2&page={page}");
            result.GetProperty("page").GetInt32().ShouldBe(page);
            result.GetProperty("pageSize").GetInt32().ShouldBe(2);
            result.GetProperty("totalCount").GetInt64().ShouldBe(5);
            result.GetProperty("items").GetArrayLength().ShouldBe(expected);
            seen.AddRange(result.GetProperty("items").EnumerateArray().Select(r => r.Str("name")));
        }

        seen.ShouldBe([$"{t} n1", $"{t} n2", $"{t} n3", $"{t} n4", $"{t} n5"], "sayfalar birleşince tüm satırlar, tekrarsız ve sıralı");

        var clamped = await platform.GetJsonAsync($"{PlatformBase}/organizations?q={t}&pageSize=1000");
        clamped.GetProperty("pageSize").GetInt32().ShouldBeLessThanOrEqualTo(100);
    }

    // ---- GET /platform/organizations/{id} -------------------------------------------------------------------------------------

    [Fact]
    public async Task Detail_ShowsEffectiveLimits_Overrides_Suspension_AndDeletionSections()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        await factory.EnsurePlanAsync("console_detail_m7", """{"maxUsers":5,"maxRecords":{"sales":500,"activities":900}}""", AllModulesOff);
        var org = await factory.SyncedOrgAsync(Token("det") + " detail");

        (await platform.DetailAsync(org.TenantId)).TryGetProperty("planChangedAt", out _).ShouldBeFalse("plan hiç değişmediyse yazılmaz");

        await platform.PutSubscriptionAsync(
            org.TenantId,
            "console_detail_m7",
            overrides: new { maxUsers = 10, maxRecords = new Dictionary<string, int?> { ["sales"] = null }, modules = new { workflows = true } });

        var detail = await platform.DetailAsync(org.TenantId);
        detail.Str("planCode").ShouldBe("console_detail_m7");
        detail.Str("planName").ShouldBe("console_detail_m7");
        detail.Str("status").ShouldBe("active");
        detail.TryGetProperty("planChangedAt", out _).ShouldBeTrue();
        var limits = detail.GetProperty("limits");
        limits.GetProperty("maxUsers").GetInt32().ShouldBe(10, "istisna plandan önceliklidir");
        var maxRecords = limits.GetProperty("maxRecords");
        maxRecords.TryGetProperty("sales", out _).ShouldBeFalse("istisna null = sınırsız: yalnız sonlu limitler yazılır");
        maxRecords.GetProperty("activities").GetInt32().ShouldBe(900);
        limits.GetProperty("modules").GetProperty("workflows").GetBoolean().ShouldBeTrue();
        limits.GetProperty("modules").GetProperty("commerce").GetBoolean().ShouldBeFalse();
        var overrides = detail.GetProperty("overrides");
        overrides.GetProperty("maxUsers").GetInt32().ShouldBe(10);
        overrides.GetProperty("modules").GetProperty("workflows").GetBoolean().ShouldBeTrue();
        overrides.GetProperty("maxRecords").GetProperty("sales").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.TryGetProperty("suspension", out _).ShouldBeFalse();
        detail.TryGetProperty("deletion", out _).ShouldBeFalse();

        await platform.SuspendAsync(org.TenantId, "odeme sorunu", "blocked");
        var suspendedDetail = await platform.DetailAsync(org.TenantId);
        suspendedDetail.Str("status").ShouldBe("suspended");
        suspendedDetail.Str("accessLevel").ShouldBe("none");
        var suspension = suspendedDetail.GetProperty("suspension");
        suspension.Str("mode").ShouldBe("blocked");
        suspension.Str("reason").ShouldBe("odeme sorunu");
        suspension.TryGetProperty("at", out _).ShouldBeTrue();

        (await platform.ReactivateRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await platform.DetailAsync(org.TenantId)).TryGetProperty("suspension", out _).ShouldBeFalse("yeniden açınca askı bölümü kalkar");

        var request = await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", await platform.DeletionBodyAsync(org.TenantId, "musteri talebi", 45), HttpStatusCode.OK);
        var deletion = (await platform.DetailAsync(org.TenantId)).GetProperty("deletion");
        deletion.GuidProp("requestId").ShouldBe(request.GuidProp("requestId"));
        deletion.Str("status").ShouldBe("scheduled");
        deletion.Str("reason").ShouldBe("musteri talebi");
        deletion.GetProperty("retentionDays").GetInt32().ShouldBe(45);
        deletion.GetProperty("attempts").GetInt32().ShouldBe(0);
        deletion.Str("requestedByEmail").ShouldBe(me.Email, StringCompareShould.IgnoreCase);
        deletion.GetProperty("scheduledFor").GetDateTimeOffset().ShouldBe(request.GetProperty("scheduledFor").GetDateTimeOffset(), TimeSpan.FromMilliseconds(5));
        deletion.TryGetProperty("cancelledAt", out _).ShouldBeFalse();
        deletion.TryGetProperty("completedAt", out _).ShouldBeFalse();
        deletion.TryGetProperty("lastError", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Detail_ForAnUnknownTenant_Returns404NotFound()
    {
        var platform = await factory.PlatformAdminAsync();

        var response = await platform.GetAsync(OrgUrl(Guid.NewGuid()), Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    // ---- PUT /platform/organizations/{id}/subscription ---------------------------------------------------------------------

    [Fact]
    public async Task PutSubscription_ChangesPlanTrialAndOverrides_AndRecordsOnePlanChangedEventPerRealChange()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        await factory.EnsurePlanAsync("console_sub_m7", """{"maxUsers":4,"maxRecords":{}}""", AllModulesOff);
        var org = await factory.SyncedOrgAsync(Token("sub") + " sub");
        var trialOn = TenantToday(10);

        // Değişiklik yok (aynı plan/denemesiz/istisnasız): 200, olay yok, denetim yok.
        (await platform.PutSubscriptionAsync(org.TenantId, "internal")).GetProperty("overLimit").GetArrayLength().ShouldBe(0);
        (await factory.PlatformEventsAsync(org.TenantId, "PlanChanged")).ShouldBeEmpty("no-op PUT olay üretmez");
        (await factory.AuditCountAsync(org.TenantId, "subscription.changed")).ShouldBe(0);

        // Plan + deneme + istisna birlikte.
        var result = await platform.PutSubscriptionAsync(org.TenantId, "console_sub_m7", trialOn.Day(), new { maxUsers = 7 });
        result.GetProperty("overLimit").GetArrayLength().ShouldBe(0);
        var detail = await platform.DetailAsync(org.TenantId);
        detail.Str("planCode").ShouldBe("console_sub_m7");
        detail.Str("status").ShouldBe("trial");
        detail.Str("trialEndsOn").ShouldBe(trialOn.Day());
        detail.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(7);

        var events = await factory.PlatformEventsAsync(org.TenantId, "PlanChanged");
        var first = events.ShouldHaveSingleItem();
        first.Str("oldPlanCode").ShouldBe("internal");
        first.Str("newPlanCode").ShouldBe("console_sub_m7");
        first.Str("trialEndsOn").ShouldBe(trialOn.Day());
        first.GetProperty("overridesChanged").GetBoolean().ShouldBeTrue();
        first.GuidProp("actorUserId").ShouldBe(me.UserId);

        // Aynı istek tekrar: hiçbir şey değişmez.
        await platform.PutSubscriptionAsync(org.TenantId, "console_sub_m7", trialOn.Day(), new { maxUsers = 7 });
        (await factory.PlatformEventsAsync(org.TenantId, "PlanChanged")).Count.ShouldBe(1);
        (await factory.AuditCountAsync(org.TenantId, "subscription.changed")).ShouldBe(1);

        // Yalnız deneme tarihi değişir: olay var, OverridesChanged false.
        await platform.PutSubscriptionAsync(org.TenantId, "console_sub_m7", trialOn.AddDays(3).Day(), new { maxUsers = 7 });
        var afterTrial = await factory.PlatformEventsAsync(org.TenantId, "PlanChanged");
        afterTrial.Count.ShouldBe(2);
        afterTrial[1].GetProperty("overridesChanged").GetBoolean().ShouldBeFalse();
        afterTrial[1].Str("oldPlanCode").ShouldBe("console_sub_m7");

        // Yalnız istisna değişir: OverridesChanged true; "tam ve kalıcı değiştirme": denemesiz + istisnasız = temiz hâl.
        await platform.PutSubscriptionAsync(org.TenantId, "console_sub_m7", trialOn.AddDays(3).Day(), new { maxUsers = 8 });
        (await factory.PlatformEventsAsync(org.TenantId, "PlanChanged"))[2].GetProperty("overridesChanged").GetBoolean().ShouldBeTrue();
        await platform.PutSubscriptionAsync(org.TenantId, "internal");
        var clean = await platform.DetailAsync(org.TenantId);
        clean.Str("status").ShouldBe("active");
        clean.TryGetProperty("trialEndsOn", out _).ShouldBeFalse("trialEndsOn yok = denemesiz");
        clean.TryGetProperty("overrides", out _).ShouldBeFalse("overrides yok = temiz");
        (await factory.PlatformEventsAsync(org.TenantId, "PlanChanged")).Count.ShouldBe(4);
        (await factory.AuditCountAsync(org.TenantId, "subscription.changed")).ShouldBe(4);
    }

    [Fact]
    public async Task PutSubscription_WithAnUnknownOrInactivePlan_Returns404PlanNotFound_AndAnUnknownTenant404NotFound()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("pnf") + " plan");
        await factory.EnsurePlanAsync("console_inactive_m7", NoLimits, AllModulesOn);
        await factory.SqlAsync("UPDATE platform.plans SET is_active = FALSE WHERE code = 'console_inactive_m7'");

        await (await platform.PutSubscriptionRawAsync(org.TenantId, "console_no_such_plan")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "platform.plan_not_found");
        await (await platform.PutSubscriptionRawAsync(org.TenantId, "console_inactive_m7")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "platform.plan_not_found");
        await (await platform.PutSubscriptionRawAsync(Guid.NewGuid(), "internal")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        var missing = await platform.PutSubscriptionRawAsync(org.TenantId, null);
        (await missing.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("planCode", out _).ShouldBeTrue();
        (await platform.DetailAsync(org.TenantId)).Str("planCode").ShouldBe("internal", "başarısız istekler planı değiştirmez");
    }

    [Fact]
    public async Task PutSubscription_WithInvalidOverrides_ReturnsPerFieldValidationErrors()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("ovr") + " overrides");

        var response = await platform.PutSubscriptionRawAsync(
            org.TenantId,
            "internal",
            overrides: new
            {
                maxUsers = -1,
                maxRecords = new Dictionary<string, int> { ["not_a_module"] = 5, ["sales"] = -2 },
                modules = new Dictionary<string, object> { ["sales"] = true, ["workflows"] = "yes" },
                extra = 1,
            });

        var errors = (await response.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors");
        foreach (var field in new[]
                 {
                     "overrides.maxUsers", "overrides.maxRecords.not_a_module", "overrides.maxRecords.sales", "overrides.modules.sales",
                     "overrides.modules.workflows", "overrides.extra",
                 })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue($"beklenen alan hatası: {field}; gelen: {errors}");
        }

        var notObject = await platform.PutSubscriptionRawAsync(org.TenantId, "internal", overrides: "text");
        (await notObject.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("overrides", out _).ShouldBeTrue();

        var badShape = await platform.PutSubscriptionRawAsync(org.TenantId, "internal", overrides: new { maxRecords = 5, modules = "all" });
        var shapeErrors = (await badShape.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors");
        shapeErrors.TryGetProperty("overrides.maxRecords", out _).ShouldBeTrue();
        shapeErrors.TryGetProperty("overrides.modules", out _).ShouldBeTrue();

        (await platform.DetailAsync(org.TenantId)).TryGetProperty("overrides", out _).ShouldBeFalse("geçersiz istek hiçbir şey yazmaz");
    }

    [Fact]
    public async Task PutSubscription_RejectsAPastTrialDate_ButAcceptsAnUnchangedOne()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("trl") + " trial");
        var past = TenantToday(-3);

        var rejected = await platform.PutSubscriptionRawAsync(org.TenantId, "internal", past.Day());
        (await rejected.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("trialEndsOn", out _).ShouldBeTrue();
        (await platform.DetailAsync(org.TenantId)).TryGetProperty("trialEndsOn", out _).ShouldBeFalse();

        // Bugünün kendisi kabul edilir (gün dahil).
        (await platform.PutSubscriptionRawAsync(org.TenantId, "internal", TenantToday().Day())).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Değişmeyen (geçmişe dönmüş) deneme tarihi, yalnız istisna düzenlenirken serbesttir.
        await factory.SetAccountAsync(factory, org.TenantId, "trial_ends_on = @d, trial_ends_at = now() - interval '2 days'", ("d", past));
        (await platform.PutSubscriptionRawAsync(org.TenantId, "internal", past.Day(), new { maxUsers = 7 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await platform.DetailAsync(org.TenantId);
        detail.Str("status").ShouldBe("trial_expired");
        detail.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task PutSubscription_ReportsOverLimit_WhenCurrentUsageExceedsTheNewLimits_AndKeepsTheData()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("olm") + " over");
        var standard = (await org.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Standard").GuidProp("id");
        await factory.AddMemberAsync(org.Admin, "Uye Bir", standard);
        await factory.AddMemberAsync(org.Admin, "Uye Iki", standard);
        for (var i = 0; i < 2; i++)
        {
            await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/accounts", new { name = $"Firma {i}" }, HttpStatusCode.Created);
        }

        var result = await platform.PutSubscriptionAsync(org.TenantId, "internal", overrides: new { maxUsers = 1, maxRecords = new { sales = 1 } });

        var overLimit = result.GetProperty("overLimit").EnumerateArray().ToList();
        overLimit.Count.ShouldBe(2);
        overLimit[0].Str("limit").ShouldBe("users");
        overLimit[0].GetProperty("max").GetInt32().ShouldBe(1);
        overLimit[0].GetProperty("used").GetInt64().ShouldBe(3);
        overLimit[0].TryGetProperty("module", out _).ShouldBeFalse();
        overLimit[1].Str("limit").ShouldBe("records");
        overLimit[1].Str("module").ShouldBe("sales");
        overLimit[1].GetProperty("max").GetInt32().ShouldBe(1);
        overLimit[1].GetProperty("used").GetInt64().ShouldBe(2);

        (await platform.DetailAsync(org.TenantId)).GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(1, "plan yine de değişir");
        (await org.Admin.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt64().ShouldBe(2, "veri silinmez");
        var blocked = await org.Admin.SendRawAsync(HttpMethod.Post, $"{Base}/organization/members", new { email = UniqueEmail("late"), displayName = "Gec Uye", roleId = standard });
        await blocked.ShouldBeProblemAsync(HttpStatusCode.PaymentRequired, "plan.limit_exceeded");
    }

    // ---- askı / yeniden açma -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Suspend_Returns204_RecordsTheEvent_MakesTheTenantReadOnly_AndReactivateReverts()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        var org = await factory.SyncedOrgAsync(Token("sus") + " suspend");

        await platform.SuspendAsync(org.TenantId, "  guvenlik olayi  ");

        var detail = await platform.DetailAsync(org.TenantId);
        detail.Str("status").ShouldBe("suspended");
        detail.Str("accessLevel").ShouldBe("readOnly", "varsayılan kip readOnly");
        detail.GetProperty("suspension").Str("reason").ShouldBe("guvenlik olayi", "gerekçe kırpılır");
        var suspended = (await factory.PlatformEventsAsync(org.TenantId, "TenantSuspended")).ShouldHaveSingleItem();
        suspended.Str("reason").ShouldBe("guvenlik olayi");
        suspended.Str("mode").ShouldBe("readOnly");
        suspended.GuidProp("actorUserId").ShouldBe(me.UserId);
        (await factory.AuditCountAsync(org.TenantId, "organization.suspended")).ShouldBe(1);
        await (await org.Admin.SendRawAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "Yazma denemesi" })).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");

        (await platform.ReactivateRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await platform.DetailAsync(org.TenantId)).Str("status").ShouldBe("active");
        (await factory.PlatformEventsAsync(org.TenantId, "TenantReactivated")).ShouldHaveSingleItem().GuidProp("actorUserId").ShouldBe(me.UserId);
        (await factory.AuditCountAsync(org.TenantId, "organization.reactivated")).ShouldBe(1);
        (await org.Admin.SendRawAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "Yazma serbest" })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Suspend_InBlockedMode_RemovesAllAccess()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("blk") + " blocked");

        await platform.SuspendAsync(org.TenantId, "kural ihlali", "blocked");

        (await platform.DetailAsync(org.TenantId)).Str("accessLevel").ShouldBe("none");
        (await factory.PlatformEventsAsync(org.TenantId, "TenantSuspended")).ShouldHaveSingleItem().Str("mode").ShouldBe("blocked");
        await (await org.Admin.GetAsync($"{Base}/accounts", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
    }

    [Fact]
    public async Task Suspend_AndReactivate_RejectUndefinedTransitions_WithFromAndToArguments_AndInvalidInput()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("trn") + " transitions");

        var notSuspended = await platform.ReactivateRawAsync(org.TenantId);
        var reactivateArgs = (await notSuspended.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.invalid_transition")).GetProperty("args");
        reactivateArgs.Str("from").ShouldBe("active");
        reactivateArgs.Str("to").ShouldBe("active");

        await platform.SuspendAsync(org.TenantId);
        var twice = await platform.SuspendRawAsync(org.TenantId);
        var suspendArgs = (await twice.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.invalid_transition")).GetProperty("args");
        suspendArgs.Str("from").ShouldBe("suspended");
        suspendArgs.Str("to").ShouldBe("suspended");

        var noReason = await platform.SuspendRawAsync(org.TenantId, reason: "");
        (await noReason.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();
        var longReason = await platform.SuspendRawAsync(org.TenantId, reason: new string('x', 501));
        (await longReason.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();
        var badMode = await platform.SuspendRawAsync(org.TenantId, mode: "frozen");
        (await badMode.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("mode", out _).ShouldBeTrue();
        await (await platform.SuspendRawAsync(Guid.NewGuid())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await platform.ReactivateRawAsync(Guid.NewGuid())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        (await factory.PlatformEventsAsync(org.TenantId, "TenantSuspended")).Count.ShouldBe(1, "başarısız komutlar olay üretmez");
        (await factory.PlatformEventsAsync(org.TenantId, "TenantReactivated")).ShouldBeEmpty();
    }

    // ---- sistem kiracısı ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SystemTenant_CannotBeSuspended_DeletionRequested_OrHaveItsSubscriptionChanged()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("sys") + " system");
        using (var scope = factory.Services.CreateScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            var info = (await directory.FindAsync(org.TenantId, Ct))!;
            await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().EnsureSystemAsync(info, Ct);
            await scope.ServiceProvider.GetRequiredService<IPlatformUnitOfWork>().SaveChangesAsync(Ct);
        }

        (await platform.DetailAsync(org.TenantId)).GetProperty("isSystem").GetBoolean().ShouldBeTrue();

        await (await platform.SuspendRawAsync(org.TenantId)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");
        await (await platform.RequestDeletionRawAsync(org.TenantId)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");
        await (await platform.PutSubscriptionRawAsync(org.TenantId, "internal", overrides: new { maxUsers = 3 })).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");

        var detail = await platform.DetailAsync(org.TenantId);
        detail.Str("status").ShouldBe("active");
        detail.TryGetProperty("overrides", out _).ShouldBeFalse();
        (await factory.AuditCountAsync(org.TenantId)).ShouldBe(0, "reddedilen komutlar denetim satırı yazmaz");
    }

    [Fact]
    public async Task SystemTenant_FlaggedBySql_IsAlsoProtected()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("sq") + " system sql");
        await factory.SetAccountAsync(factory, org.TenantId, "is_system = TRUE");

        await (await platform.SuspendRawAsync(org.TenantId)).ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "platform.system_tenant_protected");
        (await platform.DetailAsync(org.TenantId)).GetProperty("isSystem").GetBoolean().ShouldBeTrue();
    }

    // ---- silme talebi (KVKK) ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletionRequest_UsesTheDefaultRetention_MakesTheTenantPendingDeletionImmediately_AndRejectsASecondRequest()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("del") + " deletion");

        var before = DateTimeOffset.UtcNow;
        var response = await platform.SendJsonAsync(HttpMethod.Post, $"{OrgUrl(org.TenantId)}/deletion-request", await platform.DeletionBodyAsync(org.TenantId, "  hesap kapatma  "), HttpStatusCode.OK);
        var after = DateTimeOffset.UtcNow;

        response.GuidProp("requestId").ShouldNotBe(Guid.Empty);
        var scheduledFor = response.GetProperty("scheduledFor").GetDateTimeOffset();
        scheduledFor.ShouldBeInRange(before.AddDays(30).AddSeconds(-5), after.AddDays(30).AddSeconds(5), "varsayılan bekleme 30 gün");

        var detail = await platform.DetailAsync(org.TenantId);
        detail.Str("status").ShouldBe("pending_deletion");
        detail.Str("accessLevel").ShouldBe("none");
        detail.GetProperty("deletion").Str("reason").ShouldBe("hesap kapatma");
        detail.GetProperty("deletion").GetProperty("retentionDays").GetInt32().ShouldBe(30);
        (await factory.PlatformEventsAsync(org.TenantId, "TenantDeletionRequested")).ShouldHaveSingleItem();
        (await factory.AuditCountAsync(org.TenantId, "deletion.requested")).ShouldBe(1);

        // Kiracı anında engellenir; /me durumu göstermek için çalışır.
        await (await org.Admin.GetAsync($"{Base}/organization/members", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        (await org.Admin.GetJsonAsync($"{Base}/me")).GetProperty("subscription").Str("status").ShouldBe("pending_deletion");

        var second = await platform.RequestDeletionRawAsync(org.TenantId);
        var args = (await second.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.invalid_transition")).GetProperty("args");
        args.Str("from").ShouldBe("pending_deletion");
        args.Str("to").ShouldBe("pending_deletion");
        (await factory.PlatformEventsAsync(org.TenantId, "TenantDeletionRequested")).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(90, true)]
    [InlineData(91, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public async Task DeletionRequest_AcceptsOnlyRetentionDaysBetween7And90(int retentionDays, bool accepted)
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("ret") + " retention");

        var response = await platform.RequestDeletionRawAsync(org.TenantId, retentionDays: retentionDays);

        if (accepted)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            var scheduledFor = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("scheduledFor").GetDateTimeOffset();
            (scheduledFor - DateTimeOffset.UtcNow).TotalDays.ShouldBe(retentionDays, 0.01);
        }
        else
        {
            var errors = (await response.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors");
            errors.TryGetProperty("retentionDays", out _).ShouldBeTrue();
            (await platform.DetailAsync(org.TenantId)).Str("status").ShouldBe("active", "geçersiz talep kiracıyı değiştirmez");
        }
    }

    [Fact]
    public async Task DeletionRequest_RequiresAReason_AndAnUnknownTenantIs404()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("dr") + " reason");

        var noReason = await platform.RequestDeletionRawAsync(org.TenantId, reason: "");
        (await noReason.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("reason", out _).ShouldBeTrue();
        await (await platform.RequestDeletionRawAsync(Guid.NewGuid())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await platform.CancelDeletionRawAsync(Guid.NewGuid())).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDeletion_WithinTheWindow_ReturnsTheTenantToItsPreviousStatus(bool wasSuspended)
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("cnl") + " cancel");
        if (wasSuspended)
        {
            await platform.SuspendAsync(org.TenantId);
        }

        (await platform.RequestDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.DetailAsync(org.TenantId)).Str("status").ShouldBe("pending_deletion");

        (await platform.CancelDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var detail = await platform.DetailAsync(org.TenantId);
        detail.Str("status").ShouldBe(wasSuspended ? "suspended" : "active");
        detail.Str("accessLevel").ShouldBe(wasSuspended ? "readOnly" : "full");
        detail.GetProperty("deletion").Str("status").ShouldBe("cancelled");
        detail.GetProperty("deletion").TryGetProperty("cancelledAt", out _).ShouldBeTrue();
        (await factory.PlatformEventsAsync(org.TenantId, "TenantDeletionCancelled")).ShouldHaveSingleItem();
        (await factory.AuditCountAsync(org.TenantId, "deletion.cancelled")).ShouldBe(1);

        // İptal edilmiş talep etkin sayılmaz: yeni talep açılabilir; ikinci iptalde etkin talep bulunmadığı için 409.
        (await platform.CancelDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await platform.RequestDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CancelDeletion_WithoutARequest_Returns409NotCancellable()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("nor") + " norequest");

        await (await platform.CancelDeletionRawAsync(org.TenantId)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "platform.deletion_not_cancellable");
    }

    [Fact]
    public async Task CancelDeletion_AfterTheScheduledDate_Returns409NotCancellable_AndKeepsTheTenantPending()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("late") + " late cancel");
        (await platform.RequestDeletionRawAsync(org.TenantId, retentionDays: 7)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.SqlAsync("UPDATE platform.deletion_requests SET scheduled_for = now() - interval '1 hour' WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(1);

        await (await platform.CancelDeletionRawAsync(org.TenantId)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "platform.deletion_not_cancellable");

        (await platform.DetailAsync(org.TenantId)).Str("status").ShouldBe("pending_deletion");
        (await factory.PlatformEventsAsync(org.TenantId, "TenantDeletionCancelled")).ShouldBeEmpty();
        (await factory.AuditCountAsync(org.TenantId, "deletion.cancelled")).ShouldBe(0);
    }

    [Fact]
    public async Task Subscription_OfAPendingDeletionTenant_CannotBeChanged_And409InvalidTransition()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("pdl") + " pending sub");
        (await platform.RequestDeletionRawAsync(org.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await platform.PutSubscriptionRawAsync(org.TenantId, "internal", overrides: new { maxUsers = 3 });

        var args = (await response.ProblemBodyAsync(HttpStatusCode.Conflict, "platform.invalid_transition")).GetProperty("args");
        args.Str("from").ShouldBe("pending_deletion");
    }

    // ---- GET /platform/plans -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plans_ListsTheCatalog_WithLimitsModulesAndTheAssignedCount()
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync(
            "console_plans_m7",
            """{"maxUsers":3,"maxRecords":{"sales":10}}""",
            """{"workflows":true,"commerce":false,"service":false,"marketing":true}""",
            trialDays: 7);
        var one = await factory.SyncedOrgAsync(Token("pln") + " one");
        var two = await factory.SyncedOrgAsync(Token("pln") + " two");
        var three = await factory.SyncedOrgAsync(Token("pln") + " three");
        foreach (var org in new[] { one, two, three })
        {
            await platform.PutSubscriptionAsync(org.TenantId, "console_plans_m7");
        }

        var plans = (await platform.GetJsonAsync($"{PlatformBase}/plans")).EnumerateArray().ToList();

        var plan = plans.Single(p => p.Str("code") == "console_plans_m7");
        plan.Str("name").ShouldBe("console_plans_m7");
        plan.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        plan.GetProperty("sortOrder").GetInt32().ShouldBe(900);
        plan.GetProperty("trialDays").GetInt32().ShouldBe(7);
        plan.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(3);
        plan.GetProperty("limits").GetProperty("maxRecords").GetProperty("sales").GetInt32().ShouldBe(10);
        plan.GetProperty("modules").GetProperty("workflows").GetBoolean().ShouldBeTrue();
        plan.GetProperty("modules").GetProperty("commerce").GetBoolean().ShouldBeFalse();
        plan.GetProperty("modules").GetProperty("marketing").GetBoolean().ShouldBeTrue();
        plan.GetProperty("assignedCount").GetInt32().ShouldBe(3);
        plans.Select(p => p.Str("code")).ShouldContain("internal");
        var sortOrders = plans.Select(p => p.GetProperty("sortOrder").GetInt32()).ToList();
        sortOrders.ShouldBe(sortOrders.Order().ToList(), "sortOrder'a göre sıralı");

        // Silinmiş hesaplar sayılmaz; silme bekleyen sayılır.
        await factory.SetAccountAsync(factory, three.TenantId, "status = 'deleted'");
        (await platform.RequestDeletionRawAsync(two.TenantId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var recount = (await platform.GetJsonAsync($"{PlatformBase}/plans")).EnumerateArray().Single(p => p.Str("code") == "console_plans_m7");
        recount.GetProperty("assignedCount").GetInt32().ShouldBe(2);
    }

    // ---- POST /platform/organizations --------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateOrganization_WithPlanAndTrial_AppliesThemToTheAccountOnceTheOutboxIsDrained()
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_create_m7", """{"maxUsers":3,"maxRecords":{}}""", AllModulesOff);
        var trialOn = TenantToday(10);

        var created = await platform.SendJsonAsync(
            HttpMethod.Post,
            $"{PlatformBase}/organizations",
            new
            {
                organizationName = Token("crt") + " created",
                adminDisplayName = "Yonetici",
                adminEmail = UniqueEmail("crt"),
                adminPassword = (string?)null,
                locale = "tr",
                planCode = "console_create_m7",
                trialEndsOn = trialOn.Day(),
            },
            HttpStatusCode.Created);
        var tenantId = created.GuidProp("organizationId");
        await factory.DrainOutboxesAsync();

        var detail = await platform.DetailAsync(tenantId);
        detail.Str("planCode").ShouldBe("console_create_m7");
        detail.Str("source").ShouldBe("platform");
        detail.Str("status").ShouldBe("trial");
        detail.Str("trialEndsOn").ShouldBe(trialOn.Day());
        detail.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(3);
        detail.GetProperty("limits").GetProperty("modules").GetProperty("workflows").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task CreateOrganization_WithoutAPlan_UsesTheDefaultProvisioningPlan()
    {
        var platform = await factory.PlatformAdminAsync();

        var created = await platform.SendJsonAsync(
            HttpMethod.Post,
            $"{PlatformBase}/organizations",
            new { organizationName = Token("dfl") + " default", adminDisplayName = "Yonetici", adminEmail = UniqueEmail("dfl"), adminPassword = (string?)null, locale = "tr" },
            HttpStatusCode.Created);
        await factory.DrainOutboxesAsync();

        var detail = await platform.DetailAsync(created.GuidProp("organizationId"));
        detail.Str("planCode").ShouldBe("internal", "test ortamı varsayılan açılış planı");
        detail.Str("source").ShouldBe("platform");
        detail.Str("status").ShouldBe("active");
    }

    [Theory]
    [InlineData("console_unknown_plan_m7")]
    [InlineData("console_create_inactive_m7")]
    public async Task CreateOrganization_WithAnUnknownOrInactivePlan_Returns400PlanCodeError_AndCreatesNothing(string planCode)
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_create_inactive_m7", NoLimits, AllModulesOn);
        await factory.SqlAsync("UPDATE platform.plans SET is_active = FALSE WHERE code = 'console_create_inactive_m7'");
        var name = Token("bad") + " rejected";
        var email = UniqueEmail("bad");

        var response = await platform.PostAsJsonAsync(
            $"{PlatformBase}/organizations",
            new { organizationName = name, adminDisplayName = "Yonetici", adminEmail = email, adminPassword = (string?)null, locale = "tr", planCode },
            Ct);

        (await response.ProblemBodyAsync(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").TryGetProperty("planCode", out _).ShouldBeTrue();
        (await factory.ScalarAsync<long>("SELECT count(*) FROM identity.tenants WHERE name = @n", ("n", name))).ShouldBe(0, "geçersiz plan organizasyon açmaz");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM identity.users WHERE normalized_email = @e", ("e", email.ToUpperInvariant()))).ShouldBe(0);
    }

    // ---- OrganizationCreated / OrganizationUpdated → hesap -----------------------------------------------------------------

    [Fact]
    public async Task OrganizationCreated_FromSignup_UsesTheConfiguredSignupPlan_AndItsTrial()
    {
        await factory.EnsurePlanAsync("console_signup_m7", """{"maxUsers":4,"maxRecords":{}}""", AllModulesOff, trialDays: 10);
        await using var host = new ConsoleClockHost(factory, b => b.UseSetting("Platform:Signup:PlanCode", "console_signup_m7"));
        var org = await host.Host.NewOrgAsync(Token("sgn") + " signup");
        var expectedTrialEnd = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(host.Clock.GetUtcNow().UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById(IstanbulZone))).AddDays(10);

        // Olay işlenmeden önce: tembel satır aynı kayıt planıyla açılır (limitsiz boşluk yok).
        var early = await org.Admin.GetJsonAsync($"{Base}/subscription");
        early.Str("planCode").ShouldBe("console_signup_m7");
        early.Str("status").ShouldBe("trial");
        early.Str("trialEndsOn").ShouldBe(expectedTrialEnd.Day());
        early.GetProperty("trialDaysLeft").GetInt32().ShouldBe(10);
        early.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(4);
        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("lazy");

        await host.Host.DrainOutboxesAsync();

        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("signup");
        (await factory.ScalarAsync<string>("SELECT plan_code FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("console_signup_m7");
        (await factory.ScalarAsync<DateTime>("SELECT trial_ends_on::timestamp FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId)))
            .ShouldBe(expectedTrialEnd.ToDateTime(TimeOnly.MinValue));
        (await org.Admin.GetJsonAsync($"{Base}/me")).GetProperty("subscription").Str("planName").ShouldBe("console_signup_m7");
    }

    [Fact]
    public async Task OrganizationCreated_FromThePlatform_CorrectsAnAlreadyCreatedLazyRow_WithThePlatformPlanAndTrial()
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_lazy_m7", """{"maxUsers":6,"maxRecords":{}}""", AllModulesOn);
        var trialOn = TenantToday(12);
        var created = await platform.SendJsonAsync(
            HttpMethod.Post,
            $"{PlatformBase}/organizations",
            new
            {
                organizationName = Token("lzy") + " lazy",
                adminDisplayName = "Yonetici",
                adminEmail = UniqueEmail("lzy"),
                adminPassword = (string?)null,
                locale = "tr",
                planCode = "console_lazy_m7",
                trialEndsOn = trialOn.Day(),
            },
            HttpStatusCode.Created);
        var tenantId = created.GuidProp("organizationId");

        // Olay işlenmeden yönetici/kiracı ilk kez plan bilgisi isterse tembel satır (kayıt planı) açılır.
        using (var scope = factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ITenantEntitlements>().GetAsync(tenantId, Ct)).PlanCode.ShouldBe("internal");
        }

        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId))).ShouldBe("lazy");

        await factory.DrainOutboxesAsync();

        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId))).ShouldBe("platform");
        var detail = await platform.DetailAsync(tenantId);
        detail.Str("planCode").ShouldBe("console_lazy_m7");
        detail.Str("trialEndsOn").ShouldBe(trialOn.Day());
        detail.Str("status").ShouldBe("trial");
    }

    [Fact]
    public async Task OrganizationCreated_IsIdempotent_ItNeverOverwritesAnAccountThatIsNoLongerLazy()
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_idem_m7", """{"maxUsers":6,"maxRecords":{}}""", AllModulesOn);
        var org = await factory.SyncedOrgAsync(Token("idm") + " idempotent");
        await platform.PutSubscriptionAsync(org.TenantId, "console_idem_m7");

        // Aynı olay yeniden teslim edilir (at-least-once): Platform işleyicisi hesabı değiştirmemelidir.
        using (var scope = factory.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetServices<IIntegrationEventHandler<OrganizationCreated>>().OfType<OrganizationCreatedAccountHandler>().Single();
            await handler.Handle(new OrganizationCreated(org.TenantId, org.Name, "tr", null, OrganizationOrigin.Signup), Ct);
            await handler.Handle(new OrganizationCreated(org.TenantId, org.Name, "tr", null, OrganizationOrigin.Signup), Ct);
        }

        (await platform.DetailAsync(org.TenantId)).Str("planCode").ShouldBe("console_idem_m7", "lazy olmayan satıra dokunulmaz");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(1);
    }

    [Fact]
    public async Task Backfill_GivesAnAccountlessTenantTheInternalPlan_AsBackfillSource_WithOnboardingDismissed()
    {
        var org = await factory.SyncedOrgAsync(Token("bkf") + " backfill");
        (await factory.SqlAsync("DELETE FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(1);

        using (var scope = factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<AccountBackfill>().RunAsync(Ct);
            created.ShouldBeGreaterThanOrEqualTo(1);
        }

        (await factory.ScalarAsync<string>("SELECT plan_code FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("internal");
        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("backfill");
        (await factory.ScalarAsync<string>("SELECT status FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("active");
        (await factory.ScalarAsync<bool>("SELECT trial_ends_at IS NULL AND trial_ends_on IS NULL FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBeTrue("denemesiz");
        (await factory.ScalarAsync<bool>("SELECT onboarding_dismissed_at IS NOT NULL FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBeTrue();
        (await factory.ScalarAsync<string>("SELECT name FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(org.Name);

        // Eski kiracı kısıtlanmaz ve onboarding görmez.
        var subscription = await org.Admin.GetJsonAsync($"{Base}/subscription");
        subscription.Str("planCode").ShouldBe("internal");
        subscription.Str("status").ShouldBe("active");
        (await org.Admin.GetJsonAsync($"{Base}/onboarding")).GetProperty("dismissed").GetBoolean().ShouldBeTrue();

        // İdempotent: ikinci koşu satırı yeniden oluşturmaz, mevcut satıra dokunmaz.
        using var again = factory.Services.CreateScope();
        (await again.ServiceProvider.GetRequiredService<AccountBackfill>().RunAsync(Ct)).ShouldBe(0);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(1);
    }

    [Fact]
    public async Task OrganizationUpdated_SyncsTheNameCopyShownInTheConsole_AfterTheOutboxIsDrained()
    {
        var platform = await factory.PlatformAdminAsync();
        var t = Token("ren");
        var org = await factory.SyncedOrgAsync($"{t} old name");
        var current = await org.Admin.GetJsonAsync($"{Base}/organization");

        await org.Admin.SendJsonAsync(
            HttpMethod.Put,
            $"{Base}/organization",
            new { name = $"{t} new name", defaultLocale = current.Str("defaultLocale"), timeZone = current.Str("timeZone") },
            HttpStatusCode.NoContent);

        (await platform.ListNamesAsync($"q={t}")).ShouldBe([$"{t} old name"], "olay işlenene kadar okuma kopyası eski ad");
        (await factory.PlatformEventsAsync(org.TenantId, "PlanChanged")).ShouldBeEmpty();
        (await factory.ScalarAsync<long>("SELECT count(*) FROM identity.outbox_messages WHERE tenant_id = @t AND type LIKE '%.OrganizationUpdated' AND processed_at IS NULL", ("t", org.TenantId))).ShouldBe(1);

        await factory.DrainOutboxesAsync();

        (await platform.ListNamesAsync($"q={t}")).ShouldBe([$"{t} new name"]);
        (await platform.DetailAsync(org.TenantId)).Str("name").ShouldBe($"{t} new name");
        (await platform.ListIdsAsync($"q={t}%20old")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheOperatingOrganizationOfAPlatformAdmin_IsASystemBootstrapTenant()
    {
        var platform = await factory.PlatformAdminAsync();
        var me = await platform.WhoAmIAsync();
        await factory.DrainOutboxesAsync();

        var detail = await platform.DetailAsync(me.TenantId);

        detail.Str("source").ShouldBe("bootstrap");
        detail.GetProperty("isSystem").GetBoolean().ShouldBeTrue("create-platform-admin organizasyonu sistem kiracısıdır");
        detail.Str("name").ShouldBe("Platform");
    }
}
