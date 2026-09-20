using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Activities.Application.Activities;
using Sense.Crm.Modules.Commerce.Application.Products;
using Sense.Crm.Modules.Identity.Application.Members;
using Sense.Crm.Modules.Marketing.Application.Campaigns;
using Sense.Crm.Modules.Platform.Application.Provisioning;
using Sense.Crm.Modules.Sales.Application.Accounts;
using Sense.Crm.Modules.Service.Application.Cases;
using Sense.Crm.Modules.Workflows.Application.Rules;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Tests.Shared.Fixtures;
using Sense.Crm.Tests.Shared.Workflows;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// Conductor görev yoklayıcısının kapısı (<c>ConductorTaskPollingService.CheckEntitlementAsync</c>): kural burada saf bir işlevle yansıtılır çünkü Worker projesi
/// testlerden referanslanmaz ve kapı özel bir yöntemdir. Girdi, yoklayıcıyla <b>aynı</b> <see cref="ITenantEntitlements"/>'tır (gerçek, veritabanı destekli; kiracı bağlamsız kapsam).
/// </summary>
internal static class AConductorGate
{
    public const string Suspended = "tenant_suspended";
    public const string ModuleDisabled = "module_disabled";

    /// <summary>Erişim <c>full</c> değilse <c>tenant_suspended</c>; aksi halde <c>workflows</c> kapalıysa <c>module_disabled</c>; aksi null (görev çalışır).</summary>
    public static string? Verdict(EntitlementSnapshot snapshot, DateTimeOffset now)
    {
        var (_, access) = snapshot.Evaluate(now);
        if (access != AccessLevel.Full)
        {
            return Suspended;
        }

        return snapshot.IsModuleEnabled(GatedModules.Workflows) ? null : ModuleDisabled;
    }
}

/// <summary>Olay veri yolu (<c>InProcessEventBus</c>) ve Conductor kapısı: kapalı modül / salt okunur / askı / silme bekleyen kiracıda yeni otomasyon atlanır, sistem tohumlayıcıları çalışır.</summary>
[Collection(ApiCollection.Name)]
public sealed class EventBusAndConductorGateTests(CrmApiFactory factory)
{
    // ---- Senaryo kurulumu: tam yetkili kiracıda kayıtlar yazılır, olaylar bekletilir; sonra durum değişir, en son outbox boşaltılır -------------------------

    private sealed record Scenario(TestOrg Org, HttpClient Platform, Guid LeadId, Guid CampaignId);

    private static async Task<Guid> AdministratorRoleIdAsync(HttpClient admin) =>
        (await admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Administrator").GuidProp("id");

    /// <summary>
    /// Plan altında: atama kuralı + kampanya + kampanyada bir lead; lead oluşturulur (<c>LeadCreated</c>) ve dönüştürülür (<c>LeadConverted</c>). Olaylar henüz işlenmedi (outbox boşaltılmadı).
    /// </summary>
    private async Task<Scenario> PendingEventsAsync(string name, string planCode)
    {
        var (org, platform) = await factory.OrgOnPlanAsync(name, planCode);
        var client = org.Admin;
        var role = await AdministratorRoleIdAsync(client);
        (await client.PostAsJsonAsync($"{Base}/workflows/rules", new Dictionary<string, object?>
        {
            ["name"] = "Lead atama",
            ["kind"] = "leadAssignment",
            ["params"] = new Dictionary<string, object?> { ["assigneeRoleId"] = role },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var campaign = await client.PostAsJsonAsync($"{Base}/campaigns", new { name = "Kampanya", type = "email" }, Ct).CreatedIdAsync();
        var lead = await client.PostLeadAsync().CreatedIdAsync();
        (await client.PostAsJsonAsync($"{Base}/campaigns/{campaign}/members", new { memberType = "lead", memberIds = new[] { lead } }, Ct)).IsSuccessStatusCode.ShouldBeTrue();
        (await client.PostAsJsonAsync($"{Base}/leads/{lead}/convert", new { createDeal = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Scenario(org, platform, lead, campaign);
    }

    private async Task DrainAllAsync()
    {
        await factory.DrainOutboxesAsync();
        await factory.Services.GetRequiredService<FakeWorkflowEngine>().DrainAsync();
    }

    private Task<long> ExecutionCountAsync(Guid tenantId) =>
        factory.ScalarAsync<long>("SELECT count(*) FROM workflows.workflow_executions WHERE tenant_id = @t", ("t", tenantId));

    private Task<string> MembershipStatusAsync(Guid campaignId) =>
        factory.ScalarAsync<string>("SELECT status FROM marketing.campaign_members WHERE campaign_id = @c", ("c", campaignId));

    /// <summary>Atlanan işleyici hata değildir: Sales outbox mesajları işlenmiş, ölü/hatalı satır yok.</summary>
    private async Task ShouldHaveProcessedTheSalesOutboxCleanlyAsync(Guid tenantId)
    {
        (await factory.ScalarAsync<long>("SELECT count(*) FROM sales.outbox_messages WHERE tenant_id = @t AND type ILIKE '%Lead%'", ("t", tenantId))).ShouldBeGreaterThan(0);
        (await factory.ScalarAsync<long>(
            "SELECT count(*) FROM sales.outbox_messages WHERE tenant_id = @t AND (processed_at IS NULL OR is_dead OR error IS NOT NULL)", ("t", tenantId))).ShouldBe(0);
    }

    private async Task ApplyStateAsync(string state, Scenario scenario)
    {
        switch (state)
        {
            case "readOnly":
                await scenario.Platform.ASuspendAsync(scenario.Org.TenantId);
                break;
            case "blocked":
                await scenario.Platform.ASuspendAsync(scenario.Org.TenantId, "blocked");
                break;
            case "pending_deletion":
                await scenario.Platform.ARequestDeletionAsync(scenario.Org.TenantId);
                break;
            case "trial_expired":
                await factory.SetAccountAsync(factory, scenario.Org.TenantId, "trial_ends_on = DATE '2000-01-01', trial_ends_at = TIMESTAMPTZ '2000-01-01 00:00:00+00'");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    [Fact]
    public async Task FullyEntitledTenant_RunsBothAutomations_LeadCreatedStartsTheWorkflow_LeadConvertedConvertsTheCampaignMember()
    {
        var scenario = await PendingEventsAsync("Olay Kontrol", await factory.PlanAsync("evfull"));

        await DrainAllAsync();

        (await ExecutionCountAsync(scenario.Org.TenantId)).ShouldBe(1);
        (await MembershipStatusAsync(scenario.CampaignId)).ShouldBe("Converted");
        await ShouldHaveProcessedTheSalesOutboxCleanlyAsync(scenario.Org.TenantId);
    }

    [Theory]
    [InlineData("readOnly")]
    [InlineData("blocked")]
    [InlineData("trial_expired")]
    [InlineData("pending_deletion")]
    public async Task Handlers_AreSkipped_WhenTheTenantIsNotFullyEntitled_WithoutErrors_AndTheEventIsConsumed(string state)
    {
        var scenario = await PendingEventsAsync("Olay Atlanan " + state, await factory.PlanAsync("evskip"));
        await ApplyStateAsync(state, scenario);

        await DrainAllAsync();

        (await ExecutionCountAsync(scenario.Org.TenantId)).ShouldBe(0, "LeadCreated -> workflow atlanmalı");
        (await MembershipStatusAsync(scenario.CampaignId)).ShouldBe("Added", "LeadConverted -> kampanya üyeliği atlanmalı");
        await ShouldHaveProcessedTheSalesOutboxCleanlyAsync(scenario.Org.TenantId);
    }

    [Theory]
    [InlineData("workflows")]
    [InlineData("marketing")]
    public async Task OnlyTheHandlerOfADisabledModuleIsSkipped_TheOtherModuleKeepsWorking(string disabled)
    {
        var full = await factory.PlanAsync("evmodfull");
        var partial = await factory.PlanAsync(
            "evmod" + disabled[..2],
            modulesJson: $$"""{"workflows":{{(disabled == "workflows" ? "false" : "true")}},"commerce":true,"service":true,"marketing":{{(disabled == "marketing" ? "false" : "true")}}}""");
        var scenario = await PendingEventsAsync("Olay Modul " + disabled, full);
        await scenario.Platform.PutSubscriptionAsync(scenario.Org.TenantId, partial);

        await DrainAllAsync();

        var executions = await ExecutionCountAsync(scenario.Org.TenantId);
        var membership = await MembershipStatusAsync(scenario.CampaignId);
        if (disabled == "workflows")
        {
            executions.ShouldBe(0);
            membership.ShouldBe("Converted");
        }
        else
        {
            executions.ShouldBe(1);
            membership.ShouldBe("Added");
        }

        await ShouldHaveProcessedTheSalesOutboxCleanlyAsync(scenario.Org.TenantId);
    }

    // ---- [EntitlementExempt] sistem işleyicileri -----------------------------------------------------------------------------

    private async Task ShouldHaveSeededDefaultsAsync(Guid tenantId)
    {
        (await factory.ScalarAsync<long>("SELECT count(*) FROM sales.pipelines WHERE tenant_id = @t", ("t", tenantId))).ShouldBeGreaterThan(0, "varsayılan satış hunisi");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.sla_policies WHERE tenant_id = @t", ("t", tenantId))).ShouldBeGreaterThan(0, "varsayılan SLA politikaları");
    }

    [Fact]
    public async Task ExemptSeeders_RunEvenWhenEveryGatedModuleIsOffInTheSignupPlan()
    {
        var offPlan = await factory.PlanAsync("evsgnoff", modulesJson: AllModulesOff);
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Platform:Signup:PlanCode", offPlan));
        var org = await host.NewOrgAsync("Tohum Modul Kapali");
        await (await org.Admin.GetAsync($"{Base}/campaigns", Ct)).ShouldBeModuleDisabledAsync("marketing"); // plan gerçekten uygulanmış

        await host.DrainOutboxesAsync();

        await ShouldHaveSeededDefaultsAsync(org.TenantId);
        (await factory.ScalarAsync<string>("SELECT plan_code FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(offPlan);
        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("signup");
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("pending_deletion")]
    public async Task ExemptSeeders_RunEvenWhenTheTenantIsAlreadyBlockedBeforeTheCreationEventIsProcessed(string state)
    {
        var org = await factory.NewOrgAsync("Tohum Engelli " + state);
        var platform = await factory.PlatformAdminAsync();
        if (state == "blocked")
        {
            await platform.ASuspendAsync(org.TenantId, "blocked");
        }
        else
        {
            await platform.ARequestDeletionAsync(org.TenantId);
        }

        await factory.DrainOutboxesAsync();

        await ShouldHaveSeededDefaultsAsync(org.TenantId);
    }

    [Fact]
    public void OnlyTheApprovedSystemSeedersAndAccountHandlersAreEntitlementExempt_EveryOtherIntegrationEventHandlerIsGated()
    {
        Assembly[] applications =
        [
            typeof(CreateAccountCommand).Assembly, typeof(CreateActivityCommand).Assembly, typeof(CreateProductCommand).Assembly, typeof(CreateRuleCommand).Assembly,
            typeof(CreateCampaignCommand).Assembly, typeof(CreateCaseCommand).Assembly, typeof(AddMemberCommand).Assembly, typeof(AccountProvisioner).Assembly,
        ];
        var handlers = applications
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
            .ToList();

        var exempt = handlers.Where(t => t.IsDefined(typeof(EntitlementExemptAttribute), inherit: true)).Select(t => t.Name).Order().ToList();

        handlers.Count.ShouldBeGreaterThanOrEqualTo(7, "taramanın gerçekten işleyici bulduğunu doğrular");
        exempt.ShouldBe(["OrganizationCreatedAccountHandler", "OrganizationCreatedHandler", "OrganizationCreatedSlaHandler", "OrganizationUpdatedAccountHandler"]);
        handlers.Select(t => t.Name).ShouldContain("LeadCreatedWorkflowHandler");
        handlers.Select(t => t.Name).ShouldContain("LeadConvertedMarketingHandler");
    }

    // ---- Geç gelen OrganizationCreated olayı, platform yöneticisinin plan değişikliğini ezmez (tembel satır) -----------------------

    [Fact]
    public async Task AdminPlanChange_OnALazyAccount_IsNotRevertedByTheLateOrganizationCreatedEvent()
    {
        var chosen = await factory.PlanAsync("evkeep", maxUsers: 7);
        var org = await factory.NewOrgAsync("Tembel Plan Korunur");
        await org.Admin.GetJsonAsync($"{Base}/subscription"); // tembel satır (source = lazy); OrganizationCreated henüz işlenmedi
        var platform = await factory.PlatformAdminAsync();
        await platform.PutSubscriptionAsync(org.TenantId, chosen);

        await factory.DrainOutboxesAsync(); // olay şimdi işlenir

        (await factory.ScalarAsync<string>("SELECT plan_code FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(chosen);
        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("signup");
        (await org.Admin.GetJsonAsync($"{Base}/subscription")).Str("planCode").ShouldBe(chosen);
    }

    // ---- Conductor görev yoklayıcı kapısı ---------------------------------------------------------------------------------------

    private async Task<EntitlementSnapshot> SnapshotAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope(); // Worker gibi: kiracı bağlamı olmayan kapsam
        return await scope.ServiceProvider.GetRequiredService<ITenantEntitlements>().GetAsync(tenantId, Ct);
    }

    [Theory]
    [InlineData("full", null)]
    [InlineData("workflows_off", AConductorGate.ModuleDisabled)]
    [InlineData("readOnly", AConductorGate.Suspended)]
    [InlineData("blocked", AConductorGate.Suspended)]
    [InlineData("trial_expired", AConductorGate.Suspended)]
    [InlineData("pending_deletion", AConductorGate.Suspended)]
    [InlineData("suspended_and_workflows_off", AConductorGate.Suspended)]
    public async Task ConductorPollerGate_FailsTasksTerminally_ExactlyWhenAccessIsNotFullOrWorkflowsIsOff(string state, string? expected)
    {
        var full = await factory.PlanAsync("cgfull");
        var off = await factory.PlanAsync("cgoff", modulesJson: """{"workflows":false,"commerce":true,"service":true,"marketing":true}""");
        var (org, platform) = await factory.OrgOnPlanAsync("Conductor Kapi " + state, state is "workflows_off" or "suspended_and_workflows_off" ? off : full);
        var scenario = new Scenario(org, platform, Guid.Empty, Guid.Empty);
        switch (state)
        {
            case "readOnly" or "blocked" or "trial_expired" or "pending_deletion":
                await ApplyStateAsync(state, scenario);
                break;
            case "suspended_and_workflows_off":
                await platform.ASuspendAsync(org.TenantId);
                break;
        }

        var verdict = AConductorGate.Verdict(await SnapshotAsync(org.TenantId), DateTimeOffset.UtcNow);

        verdict.ShouldBe(expected);
    }

    [Fact]
    public async Task ConductorPollerGate_TreatsAnUnknownTenantAsSuspended()
    {
        var verdict = AConductorGate.Verdict(await SnapshotAsync(Guid.NewGuid()), DateTimeOffset.UtcNow);

        verdict.ShouldBe(AConductorGate.Suspended);
    }

    [Fact]
    public async Task ConductorPollerGate_ReopensAfterReactivation()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync("Conductor Yeniden Ac");
        await platform.ASuspendAsync(org.TenantId, "blocked");
        AConductorGate.Verdict(await SnapshotAsync(org.TenantId), DateTimeOffset.UtcNow).ShouldBe(AConductorGate.Suspended);

        await platform.AReactivateAsync(org.TenantId);

        AConductorGate.Verdict(await SnapshotAsync(org.TenantId), DateTimeOffset.UtcNow).ShouldBeNull();
    }
}
