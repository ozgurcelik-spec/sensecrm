using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>Sahte saatli API host'u: aynı veritabanı, <see cref="TimeProvider"/> testin elinde (JWT doğrulaması gerçek saatle yapılır: önce oturum aç, sonra saati oynat).</summary>
internal sealed class AClockedHost : IDisposable
{
    public AClockedHost(CrmApiFactory factory, int? entitlementCacheSeconds = null)
    {
        var now = DateTimeOffset.UtcNow;
        Clock = new TestClock(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero));
        Web = factory.WithWebHostBuilder(builder =>
        {
            if (entitlementCacheSeconds is { } seconds)
            {
                builder.UseSetting("Platform:Entitlements:CacheSeconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        });
    }

    public TestClock Clock { get; }

    public WebApplicationFactory<Program> Web { get; }

    public void Dispose() => Web.Dispose();
}

/// <summary>Askı/deneme testlerinin ortak yardımcıları (A öneki: diğer test dosyalarıyla çakışmaz).</summary>
internal static class ASuspensionKit
{
    public static WebApplicationFactory<Program> WithEntitlementCacheSeconds(this CrmApiFactory factory, int seconds) =>
        factory.WithWebHostBuilder(builder => builder.UseSetting("Platform:Entitlements:CacheSeconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Yeni organizasyon + kayıt olayı boşaltılmış (tembel satır düzelmiş) + platform yöneticisi.</summary>
    public static async Task<(TestOrg Org, HttpClient Platform)> OrgWithPlatformAsync(this WebApplicationFactory<Program> host, string name)
    {
        var org = await host.NewOrgAsync(name);
        await host.DrainOutboxesAsync();
        return (org, await host.PlatformAdminAsync());
    }

    public static Task ASuspendAsync(this HttpClient platform, Guid tenantId, string mode = "readOnly") =>
        platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{tenantId}/suspend", new { reason = "test askisi", mode }, HttpStatusCode.NoContent);

    public static Task AReactivateAsync(this HttpClient platform, Guid tenantId) =>
        platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{tenantId}/reactivate", null, HttpStatusCode.NoContent);

    public static Task ARequestDeletionAsync(this HttpClient platform, Guid tenantId) =>
        platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{tenantId}/deletion-request", new { reason = "test silme talebi", retentionDays = 7 }, HttpStatusCode.OK);

    public static Task ACancelDeletionAsync(this HttpClient platform, Guid tenantId) =>
        platform.SendJsonAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{tenantId}/deletion-request/cancel", null, HttpStatusCode.NoContent);

    public static async Task<DateTimeOffset> TrialEndsAtAsync(this CrmApiFactory factory, Guid tenantId) =>
        new(DateTime.SpecifyKind(await factory.ScalarAsync<DateTime>("SELECT trial_ends_at FROM platform.tenant_accounts WHERE tenant_id = @t", ("t", tenantId)), DateTimeKind.Utc));

    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static DateOnly UtcToday() => DateOnly.FromDateTime(DateTime.UtcNow);

    public static Task<HttpResponseMessage> WriteAsync(this HttpClient client) => client.PostAccountAsync();

    public static async Task ShouldBeSuspendedAsync(this HttpResponseMessage response, string reason)
    {
        var problem = await response.ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        problem.GetProperty("args").Str("reason").ShouldBe(reason);
    }

    public static async Task ShouldWriteAsync(this HttpClient client) =>
        (await client.WriteAsync()).StatusCode.ShouldBe(HttpStatusCode.Created);

    /// <summary>Kiracı durumundan bağımsız uçlar (durumu göstermek için): <c>/me</c>, <c>/subscription</c>, <c>/me/invitations</c>.</summary>
    public static async Task ShouldSeeStatusAsync(this HttpClient client, string status, string access)
    {
        var me = await client.GetJsonAsync($"{Base}/me");
        me.GetProperty("subscription").Str("status").ShouldBe(status);
        me.GetProperty("subscription").Str("accessLevel").ShouldBe(access);
        var subscription = await client.GetJsonAsync($"{Base}/subscription");
        subscription.Str("status").ShouldBe(status);
        subscription.Str("accessLevel").ShouldBe(access);
        (await client.GetAsync($"{Base}/me/invitations", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Muaf olmayan her uç (okumalar dahil) <c>tenant.suspended</c> döner.</summary>
    public static async Task ShouldBeBlockedEverywhereAsync(this HttpClient client, string reason)
    {
        foreach (var url in new[]
        {
            "/accounts", "/leads", "/contacts", "/deals", "/pipelines", "/organization", "/organization/members", "/organization/roles",
            "/onboarding", "/campaigns", "/products", "/cases", "/workflows/rules",
        })
        {
            await (await client.GetAsync($"{Base}{url}", Ct)).ShouldBeSuspendedAsync(reason);
        }

        await (await client.WriteAsync()).ShouldBeSuspendedAsync(reason);
        await (await client.PostLeadAsync()).ShouldBeSuspendedAsync(reason);
    }

    /// <summary>Yeni oturum açar (gerçek saat) ve refresh token'lı yanıtı döner.</summary>
    public static Task<AuthResponse> LoginFreshAsync(this WebApplicationFactory<Program> host, string email) => host.CreateClient().LoginAsync(email);

    /// <summary><paramref name="member"/>'in yöneticisini <paramref name="host"/> organizasyonuna davet eder ve davet kabul edilir (kullanıcı iki organizasyonun etkin üyesi olur).</summary>
    public static async Task JoinAsync(this TestOrg target, TestOrg user)
    {
        var role = await target.Admin.StandardRoleIdAsync();
        (await target.Admin.PostMemberAsync(role, user.AdminEmail)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var invitationId = (await user.Admin.GetJsonAsync($"{Base}/me/invitations")).EnumerateArray().Single(i => i.GuidProp("organizationId") == target.TenantId).GuidProp("id");
        (await user.Admin.PostAsync($"{Base}/me/invitations/{invitationId}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}

/// <summary>Askı, deneme bitişi ve bekleyen silme: sahte saat, salt okunur/tam engel, önbellek, oturum kuralları.</summary>
[Collection(ApiCollection.Name)]
public sealed class SuspensionAndTrialApiTests(CrmApiFactory factory)
{
    // ---- Deneme bitişi (sahte saat) -----------------------------------------------------------------------------------

    private static async Task<(AClockedHost Host, TestOrg Org, HttpClient Platform, DateTimeOffset TrialEnd)> TrialOrgAsync(CrmApiFactory factory, string name, int? cacheSeconds = null)
    {
        var host = new AClockedHost(factory, cacheSeconds);
        var (org, platform) = await host.Web.OrgWithPlatformAsync(name);
        await platform.PutSubscriptionAsync(org.TenantId, "internal", trialEndsOn: ASuspensionKit.Iso(ASuspensionKit.UtcToday().AddDays(1)));
        return (host, org, platform, await factory.TrialEndsAtAsync(org.TenantId));
    }

    [Fact]
    public async Task TrialEnd_MakesWritesFail403WithTrialExpired_WhileReadsMeAndSubscriptionKeepWorking()
    {
        var (host, org, _, trialEnd) = await TrialOrgAsync(factory, "Deneme Bitisi");
        using var clockedHost = host;
        await org.Admin.ShouldSeeStatusAsync("trial", "full");

        host.Clock.SetUtcNow(trialEnd.AddSeconds(-1));
        await org.Admin.ShouldWriteAsync();

        host.Clock.SetUtcNow(trialEnd); // now >= trial_ends_at: bitti
        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("trial_expired");
        await (await org.Admin.PostLeadAsync()).ShouldBeSuspendedAsync("trial_expired");
        await (await org.Admin.PostMemberAsync(await org.Admin.StandardRoleIdAsync())).ShouldBeSuspendedAsync("trial_expired");
        (await org.Admin.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.GetAsync($"{Base}/organization/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await org.Admin.ShouldSeeStatusAsync("trial_expired", "readOnly");
        (await org.Admin.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"{Base}/me") { Content = JsonContent.Create(new { displayName = "Yeni Ad" }) }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent); // kullanıcı-düzeyi muaf komut
    }

    [Fact]
    public async Task TrialExpiry_IsReopened_WhenTheClockMovesBack()
    {
        var (host, org, _, trialEnd) = await TrialOrgAsync(factory, "Deneme Saat Geri");
        using var clockedHost = host;
        host.Clock.SetUtcNow(trialEnd.AddHours(1));
        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("trial_expired");

        host.Clock.SetUtcNow(trialEnd.AddHours(-1));

        await org.Admin.ShouldWriteAsync();
    }

    [Fact]
    public async Task TrialExpiry_IsReopened_WhenThePlanIsExtendedOrTheTrialIsRemoved()
    {
        var (host, org, platform, trialEnd) = await TrialOrgAsync(factory, "Deneme Uzatma");
        using var clockedHost = host;
        host.Clock.SetUtcNow(trialEnd.AddHours(1));
        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("trial_expired");

        var extendedTo = DateOnly.FromDateTime(host.Clock.GetUtcNow().UtcDateTime).AddDays(3);
        await platform.PutSubscriptionAsync(org.TenantId, "internal", trialEndsOn: ASuspensionKit.Iso(extendedTo));

        await org.Admin.ShouldWriteAsync();
        await org.Admin.ShouldSeeStatusAsync("trial", "full");

        host.Clock.SetUtcNow((await factory.TrialEndsAtAsync(org.TenantId)).AddSeconds(1));
        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("trial_expired");

        await platform.PutSubscriptionAsync(org.TenantId, "internal"); // denemesiz kalıcı plan

        await org.Admin.ShouldWriteAsync();
        await org.Admin.ShouldSeeStatusAsync("active", "full");
    }

    [Fact]
    public async Task TrialExpiry_DoesNotWaitForTheEntitlementCacheTtl()
    {
        var (host, org, _, trialEnd) = await TrialOrgAsync(factory, "Deneme Onbellek", cacheSeconds: 3600);
        using var clockedHost = host;
        host.Clock.SetUtcNow(trialEnd.AddSeconds(-30));
        await org.Admin.ShouldWriteAsync(); // önbellek ısındı (ham durum, deneme bitiş anıyla)

        host.Clock.SetUtcNow(trialEnd.AddSeconds(1));

        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("trial_expired");
    }

    // ---- Askı (readOnly / blocked) ve bekleyen silme --------------------------------------------------------------------

    [Fact]
    public async Task Suspend_ReadOnly_BlocksWrites_KeepsReadsMeAndSubscription_AndReactivateRestoresWrites()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync("Aski ReadOnly");
        await org.Admin.ShouldWriteAsync();

        await platform.ASuspendAsync(org.TenantId);

        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("suspended");
        await (await org.Admin.PostMemberAsync(await org.Admin.StandardRoleIdAsync())).ShouldBeSuspendedAsync("suspended");
        (await org.Admin.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await org.Admin.GetAsync($"{Base}/organization/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await org.Admin.ShouldSeeStatusAsync("suspended", "readOnly");

        await platform.AReactivateAsync(org.TenantId);

        await org.Admin.ShouldWriteAsync();
        await org.Admin.ShouldSeeStatusAsync("active", "full");
    }

    [Fact]
    public async Task Suspend_Blocked_MakesEveryEndpointFail403_ExceptMeSubscriptionAndOwnInvitations_AndReactivateRestores()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync("Aski Blocked");
        await org.Admin.ShouldWriteAsync();

        await platform.ASuspendAsync(org.TenantId, mode: "blocked");

        await org.Admin.ShouldBeBlockedEverywhereAsync("suspended");
        await org.Admin.ShouldSeeStatusAsync("suspended", "none");

        await platform.AReactivateAsync(org.TenantId);

        await org.Admin.ShouldWriteAsync();
        (await org.Admin.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await org.Admin.ShouldSeeStatusAsync("active", "full");
    }

    [Fact]
    public async Task PendingDeletion_BlocksEveryEndpointImmediately_ExceptMeAndSubscription_AndCancelRestoresThePreviousStatus()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync("Silme Bekleyen");
        await org.Admin.ShouldWriteAsync();

        await platform.ARequestDeletionAsync(org.TenantId);

        await org.Admin.ShouldBeBlockedEverywhereAsync("pending_deletion");
        await org.Admin.ShouldSeeStatusAsync("pending_deletion", "none");

        await platform.ACancelDeletionAsync(org.TenantId);

        await org.Admin.ShouldWriteAsync();
        await org.Admin.ShouldSeeStatusAsync("active", "full");
    }

    [Fact]
    public async Task SuspensionTakesEffectImmediatelyInProcess_EvenWithALongEntitlementCacheTtl()
    {
        using var host = factory.WithEntitlementCacheSeconds(3600);
        var (org, platform) = await host.OrgWithPlatformAsync("Aski Onbellek");
        await org.Admin.ShouldWriteAsync(); // önbellek ısındı

        await platform.ASuspendAsync(org.TenantId);
        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("suspended");

        await platform.AReactivateAsync(org.TenantId);
        await org.Admin.ShouldWriteAsync();

        // Önbellek gerçekten var: geçersiz kılmadan yapılan doğrudan SQL değişikliği süre dolana kadar görünmez; geçersiz kılınca hemen görünür.
        (await factory.SqlAsync("UPDATE platform.tenant_accounts SET status = 'suspended', suspension_mode = 'readOnly' WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(1);
        await org.Admin.ShouldWriteAsync();
        await host.SetAccountAsync(factory, org.TenantId, "status = status");
        await (await org.Admin.WriteAsync()).ShouldBeSuspendedAsync("suspended");
    }

    [Fact]
    public async Task MemberCreationHelpers_KeepWorking_OnARunningTrial_AndTheMemberSeesTheTrial()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync("Deneme Uye Yardimcisi");
        await platform.PutSubscriptionAsync(org.TenantId, "internal", trialEndsOn: ASuspensionKit.Iso(ASuspensionKit.UtcToday().AddDays(5)));
        var role = await org.Admin.StandardRoleIdAsync();

        var member = await factory.AddMemberAsync(org.Admin, "Deneme Uyesi", role);

        (await member.Client.GetAsync($"{Base}/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await member.Client.GetJsonAsync($"{Base}/me")).GetProperty("subscription").Str("status").ShouldBe("trial");
        (await member.Client.GetAsync($"{Base}/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Giriş / yenileme / organizasyon değiştirme -----------------------------------------------------------------------

    [Theory]
    [InlineData("blocked", "suspended")]
    [InlineData("deletion", "pending_deletion")]
    public async Task Login_WhenTheOnlyOrganizationIsBlocked_Fails403TenantSuspended_WithTheReason(string how, string reason)
    {
        var (org, platform) = await factory.OrgWithPlatformAsync("Giris Engelli " + how);
        if (how == "blocked")
        {
            await platform.ASuspendAsync(org.TenantId, mode: "blocked");
        }
        else
        {
            await platform.ARequestDeletionAsync(org.TenantId);
        }

        var response = await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/login", new { email = org.AdminEmail, password = DefaultPassword }, Ct);

        await response.ShouldBeSuspendedAsync(reason);
    }

    [Fact]
    public async Task Login_WhenTheOrganizationIsOnlyReadOnlySuspended_StillSucceeds()
    {
        var (org, platform) = await factory.OrgWithPlatformAsync("Giris ReadOnly");
        await platform.ASuspendAsync(org.TenantId);

        var auth = await factory.LoginFreshAsync(org.AdminEmail);

        var client = factory.CreateClient().WithToken(auth.AccessToken);
        await client.ShouldSeeStatusAsync("suspended", "readOnly");
    }

    [Fact]
    public async Task Refresh_ForABlockedOrganization_FailsWithTheExistingRefreshFailureCode_WhileReadOnlyStillRefreshes()
    {
        var (blockedOrg, platform) = await factory.OrgWithPlatformAsync("Yenileme Engelli");
        var readOnlyOrg = await factory.NewOrgAsync("Yenileme ReadOnly");
        await factory.DrainOutboxesAsync();
        var blockedSession = await factory.LoginFreshAsync(blockedOrg.AdminEmail);
        var readOnlySession = await factory.LoginFreshAsync(readOnlyOrg.AdminEmail);

        await platform.ASuspendAsync(blockedOrg.TenantId, mode: "blocked");
        await platform.ASuspendAsync(readOnlyOrg.TenantId);

        await (await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/refresh", new { refreshToken = blockedSession.RefreshToken }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        (await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/refresh", new { refreshToken = readOnlySession.RefreshToken }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SwitchOrganization_ToABlockedOrganization_Fails403TenantSuspended_ButReadOnlyAndNonMembersFollowTheirOwnRules()
    {
        var (home, platform) = await factory.OrgWithPlatformAsync("Gecis Ev");
        var blocked = await factory.NewOrgAsync("Gecis Engelli");
        var readOnly = await factory.NewOrgAsync("Gecis ReadOnly");
        var stranger = await factory.NewOrgAsync("Gecis Yabanci");
        await factory.DrainOutboxesAsync();
        await blocked.JoinAsync(home);
        await readOnly.JoinAsync(home);
        await platform.ASuspendAsync(blocked.TenantId, mode: "blocked");
        await platform.ASuspendAsync(readOnly.TenantId);
        await platform.ASuspendAsync(stranger.TenantId, mode: "blocked");

        await (await home.Admin.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = blocked.TenantId }, Ct)).ShouldBeSuspendedAsync("suspended");
        (await home.Admin.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = readOnly.TenantId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await home.Admin.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = stranger.TenantId }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden"); // üye olmayana kiracı durumu sızdırılmaz
    }

    [Fact]
    public async Task Login_ForAUserWithTwoOrganizations_SkipsTheBlockedDefaultOne_AndLogsIntoTheOther()
    {
        var (blocked, platform) = await factory.OrgWithPlatformAsync("Cift Org Engelli");
        var home = await factory.NewOrgAsync("Cift Org Ev");
        await factory.DrainOutboxesAsync();
        await blocked.JoinAsync(home); // ev yöneticisi, engellenecek organizasyona da üye
        var toBlocked = await home.Admin.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = blocked.TenantId }, Ct); // varsayılan = engellenecek olan
        toBlocked.StatusCode.ShouldBe(HttpStatusCode.OK);

        await platform.ASuspendAsync(blocked.TenantId, mode: "blocked");
        var auth = await factory.LoginFreshAsync(home.AdminEmail);

        var me = await factory.CreateClient().WithToken(auth.AccessToken).GetJsonAsync($"{Base}/me");
        me.GetProperty("organization").GuidProp("id").ShouldBe(home.TenantId);
    }
}
