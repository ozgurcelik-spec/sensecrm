using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Identity.Contracts;

/// <summary>İzin gruplarının (istemcinin izin ekranındaki başlıklar) sabit adları.</summary>
public static class PermissionGroups
{
    public const string Org = "org";
    public const string Crm = "crm";
}

/// <summary>Organizasyon yönetimi izinleri (Identity modülünün kendi izinleri).</summary>
public static class OrgPermissions
{
    public const string Module = "identity";

    public const string SettingsManage = "org.settings.manage";
    public const string UsersRead = "org.users.read";
    public const string UsersManage = "org.users.manage";
    public const string RolesManage = "org.roles.manage";
    public const string AuditRead = "org.audit.read";

    public static IReadOnlyList<Permission> All { get; } =
    [
        new(SettingsManage, Module, PermissionGroups.Org),
        new(UsersRead, Module, PermissionGroups.Org),
        new(UsersManage, Module, PermissionGroups.Org),
        new(RolesManage, Module, PermissionGroups.Org),
        new(AuditRead, Module, PermissionGroups.Org),
    ];
}

/// <summary>
/// Kendi modülü olmayan (birden çok modülü kapsayan) CRM izni: <c>crm.reports.read</c> satış (Sales) ve aktivite (Activities)
/// raporlarının ortak iznidir. <c>crm.accounts/contacts/leads/deals.*</c> <c>Sense.Crm.Modules.Sales.Contracts.SalesPermissions</c>'ta,
/// <c>crm.activities.*</c> <c>Sense.Crm.Modules.Activities.Contracts.ActivitiesPermissions</c>'ta tanımlıdır (anahtar dizgeleri sözleşmedir).
/// </summary>
public static class CrmPermissions
{
    public const string Module = "crm";

    public const string ReportsRead = "crm.reports.read";

    public static IReadOnlyList<Permission> All { get; } =
    [
        new(ReportsRead, Module, PermissionGroups.Crm),
    ];
}

/// <summary>
/// Modüller arası okuma sözleşmesi: kullanıcının aktif organizasyonun aktif üyesi olup olmadığı (kayıt sahibi/owner
/// doğrulaması) ve üyelerin görünen adları. Aktif kiracı bağlamında çalışır.
/// </summary>
public interface IMemberLookup
{
    Task<bool> IsActiveMemberAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Aktif organizasyonun (pasif olanlar dahil) üyelerinin görünen adları; üye olmayan kimlikler sonuçta yer almaz.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}

/// <summary>Bir rolün aktif üyesi (workflow atama/onay için).</summary>
public sealed record RoleMember(Guid UserId, string DisplayName);

/// <summary>
/// Modüller arası rol üyeliği sözleşmesi (Identity uygular): rol kiracıda var mı ve rolün <b>aktif</b> üyeleri kimler.
/// Aktif kiracı bağlamında çalışır; başka organizasyonun rolü asla dönmez.
/// </summary>
public interface IRoleMemberLookup
{
    Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Rolün aktif üyeleri (kullanıcı kimliğine göre sıralı, kararlı). Rol yoksa boş liste.</summary>
    Task<IReadOnlyList<RoleMember>> GetActiveMembersAsync(Guid roleId, CancellationToken cancellationToken = default);
}
