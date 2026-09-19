using Crm.Shared.Contracts.Security;

namespace Crm.Modules.Identity.Contracts;

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
/// Henüz kendi modülü olmayan CRM izinleri (aktiviteler, raporlar). <c>crm.accounts/contacts/leads/deals.*</c> anahtarları
/// Milestone 2'de <c>Crm.Modules.Sales.Contracts.SalesPermissions</c>'a taşındı (anahtar dizgeleri aynı, sözleşmedir).
/// Aktiviteler/raporlar modülleri (Milestone 3) geldiğinde bu geçici kayıt da kaldırılır.
/// </summary>
public static class CrmPermissions
{
    public const string Module = "crm";

    public const string ActivitiesRead = "crm.activities.read";
    public const string ActivitiesWrite = "crm.activities.write";
    public const string ReportsRead = "crm.reports.read";

    public static IReadOnlyList<Permission> All { get; } =
    [
        new(ActivitiesRead, Module, PermissionGroups.Crm),
        new(ActivitiesWrite, Module, PermissionGroups.Crm),
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
