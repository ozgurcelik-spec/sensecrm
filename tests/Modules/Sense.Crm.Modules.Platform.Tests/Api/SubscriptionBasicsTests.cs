using System.Net;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>Test ortamı varsayılanı (<c>internal</c> plan): kiracı tarafı abonelik uçları ve <c>/me.subscription</c>; tembel hesap satırı.</summary>
[Collection(ApiCollection.Name)]
public sealed class SubscriptionBasicsTests(CrmApiFactory factory)
{
    [Fact]
    public async Task NewOrganization_GetsTheInternalPlan_Unlimited_AllModulesOpen_FullAccess()
    {
        var org = await factory.NewOrgAsync("Internal Org");

        var subscription = await org.Admin.GetJsonAsync($"{Base}/subscription");

        subscription.Str("planCode").ShouldBe("internal");
        subscription.Str("status").ShouldBe("active");
        subscription.Str("accessLevel").ShouldBe("full");
        subscription.TryGetProperty("trialEndsOn", out _).ShouldBeFalse();
        subscription.GetProperty("modules").EnumerateObject().Select(m => m.Value.GetBoolean()).ShouldAllBe(on => on);
        subscription.GetProperty("limits").TryGetProperty("maxUsers", out _).ShouldBeFalse("sınırsız = anahtar yok");
        subscription.GetProperty("limits").GetProperty("maxRecords").EnumerateObject().ShouldBeEmpty();
        subscription.GetProperty("usage").GetProperty("users").GetInt32().ShouldBe(1);
        subscription.GetProperty("overLimit").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Me_IncludesTheSubscriptionSummary()
    {
        var org = await factory.NewOrgAsync("Me Org");

        var me = await org.Admin.GetJsonAsync($"{Base}/me");

        var subscription = me.GetProperty("subscription");
        subscription.Str("planCode").ShouldBe("internal");
        subscription.Str("status").ShouldBe("active");
        subscription.Str("accessLevel").ShouldBe("full");
        subscription.GetProperty("modules").GetProperty("workflows").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task TheAccountRowIsCreatedLazily_ThenCorrectedByTheOrganizationCreatedEvent()
    {
        var org = await factory.NewOrgAsync("Lazy Org");
        await org.Admin.GetJsonAsync($"{Base}/subscription"); // ilk erişimde tembel satır

        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("lazy");

        await factory.DrainOutboxesAsync();

        (await factory.ScalarAsync<string>("SELECT source FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe("signup");
    }

    [Fact]
    public async Task Subscription_RequiresTheSettingsManagePermission_AndAnonymousIsRejected()
    {
        var org = await factory.NewOrgAsync("Perm Org");
        var standardRole = (await org.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Standard").GuidProp("id");
        var member = await factory.AddMemberAsync(org.Admin, "Standart Üye", standardRole);

        await (await member.Client.GetAsync($"{Base}/subscription", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await factory.CreateClient().GetAsync($"{Base}/subscription", Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }
}
