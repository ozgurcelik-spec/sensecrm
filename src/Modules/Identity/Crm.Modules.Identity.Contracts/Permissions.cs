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
/// CRM kayıt izinleri. Anahtarlar Milestone 1'de rollere atanabilsin diye şimdiden tanımlıdır; CRM modülleri (Milestone 2:
/// Sales, Milestone 3: Activities/Reporting) geldiğinde her modül kendi anahtarlarını kendi <c>IModule.Permissions</c>'ından
/// döndürür (katalog anahtara göre tekilleştirir) ve buradaki geçici kayıt kaldırılır. Anahtarlar sözleşmedir, değiştirilmez.
/// </summary>
public static class CrmPermissions
{
    public const string Module = "crm";

    public const string AccountsRead = "crm.accounts.read";
    public const string AccountsWrite = "crm.accounts.write";
    public const string ContactsRead = "crm.contacts.read";
    public const string ContactsWrite = "crm.contacts.write";
    public const string LeadsRead = "crm.leads.read";
    public const string LeadsWrite = "crm.leads.write";
    public const string DealsRead = "crm.deals.read";
    public const string DealsWrite = "crm.deals.write";
    public const string ActivitiesRead = "crm.activities.read";
    public const string ActivitiesWrite = "crm.activities.write";
    public const string ReportsRead = "crm.reports.read";

    public static IReadOnlyList<Permission> All { get; } =
    [
        new(AccountsRead, Module, PermissionGroups.Crm),
        new(AccountsWrite, Module, PermissionGroups.Crm),
        new(ContactsRead, Module, PermissionGroups.Crm),
        new(ContactsWrite, Module, PermissionGroups.Crm),
        new(LeadsRead, Module, PermissionGroups.Crm),
        new(LeadsWrite, Module, PermissionGroups.Crm),
        new(DealsRead, Module, PermissionGroups.Crm),
        new(DealsWrite, Module, PermissionGroups.Crm),
        new(ActivitiesRead, Module, PermissionGroups.Crm),
        new(ActivitiesWrite, Module, PermissionGroups.Crm),
        new(ReportsRead, Module, PermissionGroups.Crm),
    ];
}

/// <summary>
/// Modüller arası okuma sözleşmesi: bir kullanıcının aktif organizasyonun aktif üyesi olup olmadığı
/// (ör. Milestone 2'de kayıt sahibi/owner doğrulaması). Aktif kiracı bağlamında çalışır.
/// </summary>
public interface IMemberLookup
{
    Task<bool> IsActiveMemberAsync(Guid userId, CancellationToken cancellationToken = default);
}
