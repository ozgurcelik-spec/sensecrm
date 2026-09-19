using Crm.Modules.Identity.Contracts;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Shared.Contracts.Security;

namespace Crm.Modules.Identity.Application.Roles;

/// <summary>
/// Sistem rollerinin izin eşlemesi (tek kaynak). Yeni organizasyon açılırken tohumlanır; API başlangıcında
/// SystemRolePermissionSynchronizer mevcut organizasyonlara da uygular (yeni modül izinleri böyle yayılır).
/// </summary>
public static class SystemRoleDefinitions
{
    private const string CrmPrefix = PermissionGroups.Crm + ".";

    /// <summary>
    /// "Standard" rolünün otomatik almadığı <c>crm.*</c> izinleri: onay verme yetkisi bilinçli olarak yalnız Administrator'a
    /// (ve yöneticinin özel rollerine) verilir (M4: onaylayıcı rol seçimi bir yönetim kararıdır).
    /// </summary>
    private static readonly HashSet<string> StandardExcluded = new(StringComparer.Ordinal) { "crm.approvals.decide" };

    public static IReadOnlyList<string> PermissionsFor(string code, IReadOnlyList<Permission> catalog) => code switch
    {
        SystemRoleCodes.Administrator => catalog.Select(p => p.Key).ToList(),
        SystemRoleCodes.Standard => catalog
            .Where(p => (p.Key.StartsWith(CrmPrefix, StringComparison.Ordinal) && !StandardExcluded.Contains(p.Key)) || p.Key == OrgPermissions.UsersRead)
            .Select(p => p.Key)
            .ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown system role code."),
    };
}
