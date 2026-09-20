using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Infrastructure.Entitlements;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// İlk kurulum kontrol listesi (M7, <c>GET /onboarding</c>, <c>POST /onboarding/dismiss</c>): dört adım veri değişince tamamlanır, tamamlanan adım kalıcıdır,
/// <c>workflows</c> kapalıysa adım çıkar, kapatılmış/tamamlanmış listede sayım yapılmaz (sayaç dekoratörüyle doğrulanır), eski kiracı kapalı gelir.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OnboardingApiTests(CrmApiFactory factory)
{
    private static readonly string[] AllKeys = ["profile", "invite_user", "create_lead", "create_workflow_rule"];

    [Fact]
    public async Task ANewSignupOrganization_StartsWithFourStepsAndNoneDone()
    {
        var org = await factory.NewOrgAsync(Token("onb") + " fresh");

        var onboarding = await org.Admin.GetJsonAsync($"{Base}/onboarding");

        onboarding.GetProperty("dismissed").GetBoolean().ShouldBeFalse();
        onboarding.GetProperty("completedCount").GetInt32().ShouldBe(0);
        onboarding.GetProperty("totalCount").GetInt32().ShouldBe(4);
        Steps(onboarding).ShouldBe(AllKeys.Select(k => (k, false)).ToList());
    }

    [Fact]
    public async Task EachStepCompletesWhenItsDataChanges_AndCompletedStepsStayDoneAfterTheRecordsAreDeleted()
    {
        var org = await factory.NewOrgAsync(Token("stp") + " steps");
        var standard = await RoleIdAsync(org.Admin, "Standard");
        var admin = await RoleIdAsync(org.Admin, "Administrator");

        // 1) profil: ilk başarılı PUT /organization
        var organization = await org.Admin.GetJsonAsync($"{Base}/organization");
        await org.Admin.SendJsonAsync(
            HttpMethod.Put, $"{Base}/organization", new { name = organization.Str("name"), defaultLocale = organization.Str("defaultLocale"), timeZone = organization.Str("timeZone") }, HttpStatusCode.NoContent);
        var afterProfile = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        Steps(afterProfile).ShouldBe([("profile", true), ("invite_user", false), ("create_lead", false), ("create_workflow_rule", false)]);
        afterProfile.GetProperty("completedCount").GetInt32().ShouldBe(1);

        // 2) kullanıcı davet: etkin + bekleyen >= 2
        var member = await factory.AddMemberAsync(org.Admin, "Ikinci Kullanici", standard);
        var afterInvite = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        Steps(afterInvite).ShouldBe([("profile", true), ("invite_user", true), ("create_lead", false), ("create_workflow_rule", false)]);
        afterInvite.GetProperty("completedCount").GetInt32().ShouldBe(2);

        // 3) ilk potansiyel müşteri
        var lead = await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = "Ilk Aday", company = "Sirket" }, HttpStatusCode.Created);
        var afterLead = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        Steps(afterLead).ShouldBe([("profile", true), ("invite_user", true), ("create_lead", true), ("create_workflow_rule", false)]);

        // 4) ilk iş akışı kuralı
        var rule = await org.Admin.SendJsonAsync(
            HttpMethod.Post, $"{Base}/workflows/rules", new { name = "Lead atama", kind = "leadAssignment", @params = new { assigneeRoleId = admin } }, HttpStatusCode.Created);
        var afterRule = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        Steps(afterRule).ShouldBe(AllKeys.Select(k => (k, true)).ToList());
        afterRule.GetProperty("completedCount").GetInt32().ShouldBe(4);
        afterRule.GetProperty("totalCount").GetInt32().ShouldBe(4);
        afterRule.GetProperty("dismissed").GetBoolean().ShouldBeFalse();
        (await factory.ScalarAsync<string>("SELECT array_to_string(onboarding_done, ',') FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId)))
            .Split(',').ShouldBe(AllKeys, ignoreOrder: true, "tamamlanan adımlar kalıcı yazılır");

        // Kayıtlar silinse / üye pasifleştirilse de "tamamlandı" kalır.
        await org.Admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/leads/{lead.GuidProp("id")}", null, HttpStatusCode.NoContent);
        await org.Admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/workflows/rules/{rule.GuidProp("id")}", null, HttpStatusCode.NoContent);
        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{member.UserId}", new { isActive = false }, HttpStatusCode.NoContent);
        Steps(await org.Admin.GetJsonAsync($"{Base}/onboarding")).ShouldBe(AllKeys.Select(k => (k, true)).ToList());
    }

    [Fact]
    public async Task ACompletedStepThatWasObservedOnce_StaysDoneAfterItsRecordIsDeleted()
    {
        var org = await factory.NewOrgAsync(Token("obs") + " observed");
        var lead = await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = "Gorulen", company = "Sirket" }, HttpStatusCode.Created);
        Steps(await org.Admin.GetJsonAsync($"{Base}/onboarding")).ShouldContain(("create_lead", true));

        await org.Admin.SendJsonAsync(HttpMethod.Delete, $"{Base}/leads/{lead.GuidProp("id")}", null, HttpStatusCode.NoContent);

        Steps(await org.Admin.GetJsonAsync($"{Base}/onboarding")).ShouldContain(("create_lead", true));
    }

    [Fact]
    public async Task InviteUserStep_CountsAPendingInvitation_AsWellAsAnActiveMember()
    {
        var host = await factory.NewOrgAsync(Token("inv") + " host");
        var other = await factory.NewOrgAsync(Token("inv") + " other");
        var standard = await RoleIdAsync(host.Admin, "Standard");

        var invited = await host.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/members", new { email = other.AdminEmail, displayName = "Davetli", roleId = standard }, HttpStatusCode.Created);

        invited.Str("status").ShouldBe("pending");
        Steps(await host.Admin.GetJsonAsync($"{Base}/onboarding")).ShouldContain(("invite_user", true));
        Steps(await other.Admin.GetJsonAsync($"{Base}/onboarding")).ShouldContain(("invite_user", false), "davet edilen hesabın kendi organizasyonu etkilenmez");
    }

    [Fact]
    public async Task WhenTheWorkflowsModuleIsOff_TheRuleStepIsAbsent_AndTheTotalDrops()
    {
        var platform = await factory.PlatformAdminAsync();
        await factory.EnsurePlanAsync("console_onb_nowf_m7", NoLimits, """{"workflows":false,"commerce":true,"service":true,"marketing":true}""");
        var org = await factory.SyncedOrgAsync(Token("nwf") + " no workflows");
        var standard = await RoleIdAsync(org.Admin, "Standard");
        await platform.PutSubscriptionAsync(org.TenantId, "console_onb_nowf_m7");

        var onboarding = await org.Admin.GetJsonAsync($"{Base}/onboarding");

        onboarding.GetProperty("totalCount").GetInt32().ShouldBe(3);
        Steps(onboarding).ShouldBe([("profile", false), ("invite_user", false), ("create_lead", false)]);

        // Üç adım tamamlanınca liste tamamdır (kapalı modülün adımı beklenmez).
        var organization = await org.Admin.GetJsonAsync($"{Base}/organization");
        await org.Admin.SendJsonAsync(
            HttpMethod.Put, $"{Base}/organization", new { name = organization.Str("name"), defaultLocale = organization.Str("defaultLocale"), timeZone = organization.Str("timeZone") }, HttpStatusCode.NoContent);
        await factory.AddMemberAsync(org.Admin, "Ikinci", standard);
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = "Aday", company = "Sirket" }, HttpStatusCode.Created);
        var complete = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        complete.GetProperty("completedCount").GetInt32().ShouldBe(3);
        complete.GetProperty("totalCount").GetInt32().ShouldBe(3);

        // Modül açılınca dördüncü adım listeye girer (henüz tamamlanmamış).
        await platform.PutSubscriptionAsync(org.TenantId, "internal");
        var reopened = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        reopened.GetProperty("totalCount").GetInt32().ShouldBe(4);
        reopened.GetProperty("completedCount").GetInt32().ShouldBe(3);
        Steps(reopened).ShouldBe([("profile", true), ("invite_user", true), ("create_lead", true), ("create_workflow_rule", false)]);
    }

    // ---- sayım yapılmaması (sayaç dekoratörü) -----------------------------------------------------------------------------------

    [Fact]
    public async Task DismissIsIdempotent_AndAfterwardsGetPerformsNoCounting()
    {
        var probe = new MeterProbe();
        await using var host = CountingHost(probe);
        var org = await host.NewOrgAsync(Token("dsm") + " dismiss");

        var first = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        first.GetProperty("dismissed").GetBoolean().ShouldBeFalse();
        probe.CollectsFor(org.TenantId).ShouldBe(1, "kapatılmamış ve tamamlanmamış liste sayılır");

        (await org.Admin.PostAsync($"{Base}/onboarding/dismiss", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await org.Admin.PostAsync($"{Base}/onboarding/dismiss", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        for (var i = 0; i < 3; i++)
        {
            var dismissed = await org.Admin.GetJsonAsync($"{Base}/onboarding");
            dismissed.GetProperty("dismissed").GetBoolean().ShouldBeTrue();
            dismissed.GetProperty("totalCount").GetInt32().ShouldBe(4);
        }

        probe.CollectsFor(org.TenantId).ShouldBe(1, "kapatıldıktan sonra hiç sayım yok");

        // Kapatılan liste yeni veriyi de izlemez (sayım yok = ilerleme yok).
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = "Aday", company = "Sirket" }, HttpStatusCode.Created);
        var afterData = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        Steps(afterData).ShouldContain(("create_lead", false));
        probe.CollectsFor(org.TenantId).ShouldBe(1);
    }

    [Fact]
    public async Task WhenEveryStepIsDone_GetPerformsNoCounting()
    {
        var probe = new MeterProbe();
        await using var host = CountingHost(probe);
        var org = await host.NewOrgAsync(Token("all") + " all done");
        var standard = await RoleIdAsync(org.Admin, "Standard");
        var admin = await RoleIdAsync(org.Admin, "Administrator");
        var organization = await org.Admin.GetJsonAsync($"{Base}/organization");
        await org.Admin.SendJsonAsync(
            HttpMethod.Put, $"{Base}/organization", new { name = organization.Str("name"), defaultLocale = organization.Str("defaultLocale"), timeZone = organization.Str("timeZone") }, HttpStatusCode.NoContent);
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/organization/members", new { email = UniqueEmail("ikinci"), displayName = "Ikinci", roleId = standard }, HttpStatusCode.Created);
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/leads", new { lastName = "Aday", company = "Sirket" }, HttpStatusCode.Created);
        await org.Admin.SendJsonAsync(HttpMethod.Post, $"{Base}/workflows/rules", new { name = "Kural", kind = "leadAssignment", @params = new { assigneeRoleId = admin } }, HttpStatusCode.Created);

        var complete = await org.Admin.GetJsonAsync($"{Base}/onboarding");
        complete.GetProperty("completedCount").GetInt32().ShouldBe(4);
        var collectsAfterCompletion = probe.CollectsFor(org.TenantId);
        collectsAfterCompletion.ShouldBe(1);

        for (var i = 0; i < 3; i++)
        {
            Steps(await org.Admin.GetJsonAsync($"{Base}/onboarding")).ShouldBe(AllKeys.Select(k => (k, true)).ToList());
        }

        probe.CollectsFor(org.TenantId).ShouldBe(collectsAfterCompletion, "hepsi tamamken sayım yapılmaz");
    }

    [Fact]
    public async Task ABackfilledOldTenant_IsDismissed_AndIsNeverCounted()
    {
        var probe = new MeterProbe();
        await using var host = CountingHost(probe);
        var org = await host.SyncedOrgAsync(Token("bfd") + " backfilled");
        (await factory.SqlAsync("DELETE FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(1);
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccountBackfill>().RunAsync(Ct);
        }

        var onboarding = await org.Admin.GetJsonAsync($"{Base}/onboarding");

        onboarding.GetProperty("dismissed").GetBoolean().ShouldBeTrue();
        onboarding.GetProperty("completedCount").GetInt32().ShouldBe(0);
        probe.CollectsFor(org.TenantId).ShouldBe(0, "eski (backfill) kiracı için sayım yapılmaz");
    }

    // ---- yetki / salt okunur kip ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Onboarding_RequiresTheSettingsManagePermission_AndAnonymousIsRejected()
    {
        var org = await factory.NewOrgAsync(Token("prm") + " permissions");
        var standard = await RoleIdAsync(org.Admin, "Standard");
        var member = await factory.AddMemberAsync(org.Admin, "Standart Uye", standard);

        await (await member.Client.GetAsync($"{Base}/onboarding", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await member.Client.PostAsync($"{Base}/onboarding/dismiss", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await factory.CreateClient().GetAsync($"{Base}/onboarding", Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }

    [Fact]
    public async Task Dismiss_IsAWrite_SoAReadOnlyTenantGetsTenantSuspended_WhileReadingStillWorks()
    {
        var platform = await factory.PlatformAdminAsync();
        var org = await factory.SyncedOrgAsync(Token("rdo") + " read only");
        await platform.SuspendAsync(org.TenantId);

        await (await org.Admin.PostAsync($"{Base}/onboarding/dismiss", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        (await org.Admin.GetJsonAsync($"{Base}/onboarding")).GetProperty("dismissed").GetBoolean().ShouldBeFalse();
    }

    // ---- yardımcılar ---------------------------------------------------------------------------------------------------------------

    private static List<(string Key, bool Done)> Steps(JsonElement onboarding) =>
        onboarding.GetProperty("items").EnumerateArray().Select(i => (i.Str("key"), i.GetProperty("done").GetBoolean())).ToList();

    private static async Task<Guid> RoleIdAsync(HttpClient admin, string roleName) =>
        (await admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == roleName).GuidProp("id");

    /// <summary><c>IUsageMeter.CollectAsync</c> çağrılarını kiracı başına sayan dekoratörlü host (sayım yapılıp yapılmadığını kanıtlar).</summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CountingHost(MeterProbe probe) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUsageMeter>();
            services.AddScoped<UsageMeter>();
            services.AddScoped<IUsageMeter>(sp => new CountingUsageMeter(sp.GetRequiredService<UsageMeter>(), probe));
        }));

    private sealed class MeterProbe
    {
        private readonly ConcurrentDictionary<Guid, int> _collects = new();

        public void Hit(Guid tenantId) => _collects.AddOrUpdate(tenantId, 1, (_, current) => current + 1);

        public int CollectsFor(Guid tenantId) => _collects.TryGetValue(tenantId, out var count) ? count : 0;
    }

    private sealed class CountingUsageMeter(IUsageMeter inner, MeterProbe probe) : IUsageMeter
    {
        public Task<UsageCollection> CollectAsync(Guid tenantId, CancellationToken ct)
        {
            probe.Hit(tenantId);
            return inner.CollectAsync(tenantId, ct);
        }

        public Task<(int Active, int Pending)> CountUsersAsync(CancellationToken ct) => inner.CountUsersAsync(ct);

        public Task<RecordCounts> GetRecordCountsAsync(CancellationToken ct) => inner.GetRecordCountsAsync(ct);
    }
}
