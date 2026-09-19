using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Identity.Tests.Api;

/// <summary>
/// M7 — yetki devri: kullanıcı kendinde olmayan rol/izin veremez, kendi rolünü değiştiremez/kendini pasifleştiremez.
/// M9 — rol ve üyelik değişiklikleri izin önbelleğini hemen geçersiz kılar (bayat izin yok).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DelegationAndCacheApiTests(CrmApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(HttpClient Admin, Guid AdminUserId, Guid DelegateRoleId, NewMember Delegate);

    /// <summary>Yönetici + temsilci yönetici (org.users.manage + org.roles.manage + org.users.read + crm.leads.read/write; Administrator DEĞİL).</summary>
    private async Task<Setup> ArrangeAsync(string orgName)
    {
        var admin = factory.CreateClient();
        admin.WithToken((await admin.SignUpAsync(orgName, UniqueEmail("admin"))).AccessToken);
        var adminUserId = (await admin.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct)).GetProperty("user").GetProperty("id").GetGuid();

        var role = await admin.PostAsJsonAsync($"{Base}/organization/roles",
            new { name = "Delegate", permissions = new[] { "org.users.manage", "org.roles.manage", "org.users.read", "crm.leads.read", "crm.leads.write" } }, Ct);
        var roleId = (await role.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var delegated = await factory.AddMemberAsync(admin, "Delegate", roleId);
        return new Setup(admin, adminUserId, roleId, delegated);
    }

    [Fact]
    public async Task DelegatedAdmin_CannotCreateOrEditRolesWithPermissionsTheyDoNotHold()
    {
        var s = await ArrangeAsync("Escalation Roles");
        var d = s.Delegate.Client;

        // Sahip olmadığı izinler (crm.deals.write, org.settings.manage, org.audit.read …) verilemez.
        foreach (var forbidden in new[] { "crm.deals.write", "org.settings.manage", "org.audit.read", "crm.approvals.decide" })
        {
            await (await d.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Esc " + forbidden, permissions = new[] { "crm.leads.read", forbidden } }, Ct))
                .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "role.permission_escalation");
        }

        // Kendi izinlerinin alt kümesi serbest.
        var ok = await d.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Subset", permissions = new[] { "crm.leads.read" } }, Ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        var subsetId = (await ok.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await d.PutAsJsonAsync($"{Base}/organization/roles/{subsetId}", new { name = "Subset 2", permissions = new[] { "crm.leads.read", "crm.leads.write" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Güncellemede de: kendinde olmayan izin eklenemez.
        await (await d.PutAsJsonAsync($"{Base}/organization/roles/{subsetId}", new { name = "Subset 2", permissions = new[] { "crm.leads.read", "org.audit.read" } }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "role.permission_escalation");

        // Administrator sistem rolü her şeyi verebilir.
        (await s.Admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Everything", permissions = new[] { "crm.deals.write", "org.audit.read", "org.settings.manage" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DelegatedAdmin_CannotAssignARoleAboveTheirOwnPermissions_ToNewOrExistingMembers()
    {
        var s = await ArrangeAsync("Escalation Members");
        var d = s.Delegate.Client;
        var standardId = await AuthApiTests.RoleIdAsync(s.Admin, "Standard");      // crm.* (deals, accounts ...) > temsilcinin izinleri
        var administratorId = await AuthApiTests.RoleIdAsync(s.Admin, "Administrator");
        var lowRole = await (await s.Admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Low", permissions = new[] { "crm.leads.read" } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var lowId = lowRole.GetProperty("id").GetGuid();

        // Yeni üye: yüksek rol reddedilir, alt küme rol kabul edilir.
        await (await d.PostAsJsonAsync($"{Base}/organization/members", new { email = UniqueEmail("hi"), displayName = "Hi", roleId = standardId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "role.permission_escalation");
        await (await d.PostAsJsonAsync($"{Base}/organization/members", new { email = UniqueEmail("adm"), displayName = "Adm", roleId = administratorId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "role.permission_escalation");
        var lowMember = await d.PostAsJsonAsync($"{Base}/organization/members", new { email = UniqueEmail("lo"), displayName = "Lo", roleId = lowId }, Ct);
        lowMember.StatusCode.ShouldBe(HttpStatusCode.Created);
        var lowUserId = (await lowMember.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("userId").GetGuid();

        // Mevcut üyenin rolünü yükseltemez.
        await (await d.PatchAsJsonAsync($"{Base}/organization/members/{lowUserId}", new { roleId = standardId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "role.permission_escalation");
        (await d.PatchAsJsonAsync($"{Base}/organization/members/{lowUserId}", new { roleId = s.DelegateRoleId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Yönetici (Administrator) her rolü verebilir.
        (await s.Admin.PatchAsJsonAsync($"{Base}/organization/members/{lowUserId}", new { roleId = standardId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Members_CannotChangeTheirOwnRole_OrDeactivateThemselves()
    {
        var s = await ArrangeAsync("Self Modify");
        var lowRole = await (await s.Admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Low Self", permissions = new[] { "crm.leads.read" } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        await (await s.Delegate.Client.PatchAsJsonAsync($"{Base}/organization/members/{s.Delegate.UserId}", new { roleId = lowRole.GetProperty("id").GetGuid() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "member.cannot_modify_self");
        await (await s.Delegate.Client.PatchAsJsonAsync($"{Base}/organization/members/{s.Delegate.UserId}", new { isActive = false }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "member.cannot_modify_self");

        // Yönetici de kendini pasifleştiremez / rolünü düşüremez.
        await (await s.Admin.PatchAsJsonAsync($"{Base}/organization/members/{s.AdminUserId}", new { isActive = false }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "member.cannot_modify_self");

        // Aynı değeri yeniden göndermek (değişiklik yok) zararsızdır.
        (await s.Delegate.Client.PatchAsJsonAsync($"{Base}/organization/members/{s.Delegate.UserId}", new { roleId = s.DelegateRoleId, isActive = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DelegatedAdmin_CannotDemoteTheLastAdministrator()
    {
        var s = await ArrangeAsync("Last Admin");
        var low = await (await s.Admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Low Admin", permissions = new[] { "crm.leads.read" } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        // Temsilci, tek Administrator'ı düşüremez (member.last_admin) ya da pasifleştiremez.
        await (await s.Delegate.Client.PatchAsJsonAsync($"{Base}/organization/members/{s.AdminUserId}", new { roleId = low.GetProperty("id").GetGuid() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "member.last_admin");
    }

    // ---- M9: izin önbelleği ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RoleUpdate_TakesEffectImmediately_InsteadOfWaitingForTheCacheToExpire()
    {
        var s = await ArrangeAsync("Cache Role");
        var reader = await factory.AddMemberAsync(s.Admin, "Reader", s.DelegateRoleId);   // izinler önbelleğe girsin
        (await reader.Client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await s.Admin.PutAsJsonAsync($"{Base}/organization/roles/{s.DelegateRoleId}", new { name = "Delegate", permissions = new[] { "org.users.read" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await (await reader.Client.GetAsync($"{Base}/leads", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await reader.Client.GetAsync($"{Base}/organization/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MembershipChanges_TakeEffectImmediately()
    {
        var s = await ArrangeAsync("Cache Member");
        var member = await factory.AddMemberAsync(s.Admin, "Member", s.DelegateRoleId);
        (await member.Client.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Rol değişimi.
        var noLeads = await (await s.Admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "No Leads", permissions = new[] { "org.users.read" } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        (await s.Admin.PatchAsJsonAsync($"{Base}/organization/members/{member.UserId}", new { roleId = noLeads.GetProperty("id").GetGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await member.Client.GetAsync($"{Base}/leads", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Pasifleştirme: mevcut token'la bile hiçbir izin kalmaz.
        (await s.Admin.PatchAsJsonAsync($"{Base}/organization/members/{member.UserId}", new { isActive = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await member.Client.GetAsync($"{Base}/organization/members", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task AcceptingAnInvitation_GrantsThePermissionsImmediately()
    {
        var s = await ArrangeAsync("Cache Invite");
        var guestEmail = UniqueEmail("guest");
        var guest = factory.CreateClient();
        var guestAuth = await guest.SignUpAsync("Guest Home", guestEmail);
        guest.WithToken(guestAuth.AccessToken);

        (await s.Admin.PostAsJsonAsync($"{Base}/organization/members", new { email = guestEmail, roleId = s.DelegateRoleId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var invitation = (await guest.GetFromJsonAsync<JsonElement>($"{Base}/me/invitations", Ct)).EnumerateArray().Single();
        (await guest.PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var orgId = invitation.GetProperty("organizationId").GetGuid();
        var switched = (await (await guest.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = orgId }, Ct)).Content.ReadFromJsonAsync<Crm.Modules.Identity.Application.AuthResponse>(Ct))!;
        var inOrg = factory.CreateClient().WithToken(switched.AccessToken);
        (await inOrg.GetAsync($"{Base}/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
