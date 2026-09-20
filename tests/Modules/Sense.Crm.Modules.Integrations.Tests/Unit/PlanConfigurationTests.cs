using Microsoft.Extensions.Configuration;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Shared.Contracts.Entitlements;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Integrations.Tests.Unit;

/// <summary>M7 uzantısı: <c>integrations</c> kapı modülü, plan yapılandırması ve <c>maxWebhooks</c>/<c>maxApiKeys</c> hak birleştirme.</summary>
public sealed class PlanConfigurationTests
{
    private static PlatformOptions Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sense.Crm.slnx")))
        {
            dir = dir.Parent;
        }

        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(dir!.FullName, "src", "Sense.Crm.Api", "appsettings.json")).Build();
        return configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>()!;
    }

    [Fact]
    public void IntegrationsIsAGatedModule_TheValidatorKnowsIt_AndTheShippedCatalogIsValid()
    {
        GatedModules.All.ShouldContain("integrations");
        GatedModules.IsGated("integrations").ShouldBeTrue();
        PlanCatalogValidator.KnownModules.ShouldContain("integrations");
        PlanCatalogValidator.Validate(Load()).ShouldBeEmpty();
    }

    [Fact]
    public void ShippedPlans_EnableIntegrationsForInternalBusinessEnterprise_NotForStarter()
    {
        var plans = Load().Plans.ToDictionary(p => p.Code, StringComparer.Ordinal);
        plans["internal"].Modules["integrations"].ShouldBeTrue();
        plans["enterprise"].Modules["integrations"].ShouldBeTrue();
        plans["business"].Modules["integrations"].ShouldBeTrue();
        plans["starter"].Modules["integrations"].ShouldBeFalse();
        plans["business"].Limits.MaxWebhooks.ShouldNotBeNull();
        plans["business"].Limits.MaxApiKeys.ShouldNotBeNull();
        plans["internal"].Limits.MaxWebhooks.ShouldBeNull("unlimited");
    }

    [Fact]
    public void NegativeIntegrationLimits_AreRejected()
    {
        var options = Load();
        options.Plans[0].Limits.MaxWebhooks = -1;
        PlanCatalogValidator.Validate(options).ShouldNotBeEmpty();
        options.Plans[0].Limits.MaxWebhooks = null;
        options.Plans[0].Limits.MaxApiKeys = -5;
        PlanCatalogValidator.Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void EffectiveLimits_OverrideKeyPresenceWins_NullMeansUnlimited_AbsentFallsBackToThePlan()
    {
        var plan = new PlanLimits(null, new Dictionary<string, int?>(), MaxWebhooks: 5, MaxApiKeys: 2);
        var modules = new Dictionary<string, bool> { ["integrations"] = true };

        var none = EffectiveEntitlements.Merge(plan, modules, TenantOverrides.None, GatedModules.All);
        none.MaxWebhooks.ShouldBe(5);
        none.MaxApiKeys.ShouldBe(2);
        none.Modules["integrations"].ShouldBeTrue();

        var explicitUnlimited = TenantOverrides.FromJson("""{"maxWebhooks":null}""");
        var merged = EffectiveEntitlements.Merge(plan, modules, explicitUnlimited, GatedModules.All);
        merged.MaxWebhooks.ShouldBeNull();
        merged.MaxApiKeys.ShouldBe(2);

        var tighter = EffectiveEntitlements.Merge(plan, modules, TenantOverrides.FromJson("""{"maxApiKeys":0,"maxWebhooks":9}"""), GatedModules.All);
        tighter.MaxApiKeys.ShouldBe(0);
        tighter.MaxWebhooks.ShouldBe(9);

        var roundTrip = TenantOverrides.FromJson(TenantOverrides.FromJson("""{"maxWebhooks":3,"maxApiKeys":null,"modules":{"integrations":false}}""").ToJson());
        roundTrip.MaxWebhooksSet.ShouldBeTrue();
        roundTrip.MaxWebhooks.ShouldBe(3);
        roundTrip.MaxApiKeysSet.ShouldBeTrue();
        roundTrip.MaxApiKeys.ShouldBeNull();
        roundTrip.IsEmpty.ShouldBeFalse();
        roundTrip.SameAs(TenantOverrides.FromJson("""{"maxWebhooks":3,"maxApiKeys":null,"modules":{"integrations":false}}""")).ShouldBeTrue();
        roundTrip.SameAs(TenantOverrides.FromJson("""{"maxWebhooks":4,"maxApiKeys":null,"modules":{"integrations":false}}""")).ShouldBeFalse();
    }

    [Fact]
    public void MissingModuleKeyMeansClosed_SoStarterAndUnlistedPlansLoseIntegrations()
    {
        var merged = EffectiveEntitlements.Merge(PlanLimits.Unlimited, new Dictionary<string, bool>(), TenantOverrides.None, GatedModules.All);
        merged.Modules["integrations"].ShouldBeFalse();
        var snapshotless = new Dictionary<string, bool> { ["integrations"] = false };
        new EntitlementSnapshot(Guid.NewGuid(), "x", "x", "active", null, null, null, "UTC", snapshotless, null, new Dictionary<string, int?>()).IsModuleEnabled("integrations").ShouldBeFalse();
    }
}
