using Sense.Crm.Modules.Activities.Contracts;
using Sense.Crm.Modules.Identity.Application.Roles;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Modules.Identity.Domain.Roles;
using Sense.Crm.Modules.Identity.Domain.Tenants;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Modules.Sales.Contracts;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Identity.Tests.Domain;

public sealed class TenantSlugTests
{
    [Theory]
    [InlineData("Acme Ltd.", "acme-ltd")]
    [InlineData("Çağrı İletişim Şirketi", "cagri-iletisim-sirketi")]
    [InlineData("  Öz  Ürün  ", "oz-urun")]
    [InlineData("AB", "org-ab")]
    public void SlugFrom_TransliteratesAndNormalizes(string name, string expected) => Tenant.SlugFrom(name).ShouldBe(expected);

    [Fact]
    public void SlugFrom_RespectsMaxLength()
    {
        var slug = Tenant.SlugFrom(new string('a', 120));
        slug.Length.ShouldBeLessThanOrEqualTo(IdentityLimits.SlugMaxLength - 5);
    }
}

public sealed class RoleTests
{
    [Fact]
    public void SystemRole_IsReadOnly_AndNotDeletable()
    {
        var role = Role.CreateSystem(Guid.NewGuid(), SystemRoleCodes.Standard, ["crm.leads.read"]);

        role.Update("Renamed", []).Error.Code.ShouldBe(IdentityErrors.RoleSystemReadOnly);
        role.EnsureDeletable(0).Error.Code.ShouldBe(IdentityErrors.RoleSystemReadOnly);
    }

    [Fact]
    public void CustomRole_WithMembers_IsInUse()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), "Sales", ["crm.leads.read"]);

        role.EnsureDeletable(2).Error.Code.ShouldBe(IdentityErrors.RoleInUse);
        role.EnsureDeletable(0).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Permissions_AreDeduplicatedAndSorted()
    {
        var role = Role.CreateCustom(Guid.NewGuid(), "Sales", ["crm.leads.write", "crm.leads.read", "crm.leads.read"]);

        role.Permissions.ShouldBe(["crm.leads.read", "crm.leads.write"]);
        role.SyncPermissionsUnchecked(["crm.leads.read", "crm.leads.write"]).ShouldBeFalse();
    }
}

public sealed class SystemRoleDefinitionsTests
{
    private static readonly IReadOnlyList<Shared.Contracts.Security.Permission> Catalog = [.. OrgPermissions.All, .. CrmPermissions.All, .. SalesPermissions.All, .. ActivitiesPermissions.All];

    [Fact]
    public void Administrator_GetsEveryPermission() =>
        SystemRoleDefinitions.PermissionsFor(SystemRoleCodes.Administrator, Catalog).Count.ShouldBe(Catalog.Count);

    [Fact]
    public void Standard_GetsAllCrmPermissions_PlusUsersRead()
    {
        var permissions = SystemRoleDefinitions.PermissionsFor(SystemRoleCodes.Standard, Catalog);

        permissions.ShouldContain(OrgPermissions.UsersRead);
        foreach (var crm in CrmPermissions.All.Concat(SalesPermissions.All).Concat(ActivitiesPermissions.All))
        {
            permissions.ShouldContain(crm.Key);
        }

        permissions.ShouldNotContain(OrgPermissions.RolesManage);
        permissions.ShouldNotContain(OrgPermissions.UsersManage);
        permissions.ShouldNotContain(OrgPermissions.SettingsManage);
        permissions.ShouldNotContain(OrgPermissions.AuditRead);
    }

    [Fact]
    public void PermissionKeys_MatchTheContract() =>
        Catalog.Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(
        [
            "crm.accounts.read", "crm.accounts.write", "crm.activities.read", "crm.activities.write", "crm.contacts.read",
            "crm.contacts.write", "crm.deals.read", "crm.deals.write", "crm.leads.read", "crm.leads.write", "crm.reports.read",
            "org.audit.read", "org.roles.manage", "org.settings.manage", "org.users.manage", "org.users.read",
        ]);
}

public sealed class UserLockoutTests
{
    [Fact]
    public void RepeatedFailures_LockTheAccount()
    {
        var user = User.Create("a@example.com", "A", "tr", "hash");
        var now = DateTime.UtcNow;
        var policy = new LockoutPolicy(3, TimeSpan.FromMinutes(15));

        user.RecordFailedAccess(now, policy);
        user.RecordFailedAccess(now, policy);
        user.CanSignIn(now).IsSuccess.ShouldBeTrue();

        user.RecordFailedAccess(now, policy);
        user.CanSignIn(now).Error.Code.ShouldBe(IdentityErrors.LockedOut);
        user.CanSignIn(now.AddMinutes(16)).IsSuccess.ShouldBeTrue();
    }
}
