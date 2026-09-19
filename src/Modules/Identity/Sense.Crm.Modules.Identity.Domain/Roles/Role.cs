using System.Globalization;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Identity.Domain.Roles;

/// <summary>
/// Organizasyona ait rol (K7). Sistem rolleri (<see cref="SystemRoleCodes"/>) her yeni organizasyona tohumlanır, salt-okurdur
/// ve izinleri başlangıçta katalogla senkronlanır. İzinler tek bir dizi kolonunda tutulur (PostgreSQL text[]): bir rolün
/// izin değişikliği denetim kaydına tek bir "permissions" önce/sonra farkı olarak düşer.
/// </summary>
public sealed class Role : TenantAggregateRoot<Guid>, IAuditLogged
{
    private Role()
    {
    }

    private Role(Guid id, Guid tenantId, string name, string? code, bool isSystem, List<string> permissions) : base(id, tenantId)
    {
        Name = name;
        Code = code;
        IsSystem = isSystem;
        Permissions = permissions;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Sistem rollerinde sabit kod (<see cref="SystemRoleCodes"/>); özel rollerde null.</summary>
    public string? Code { get; private set; }

    public bool IsSystem { get; private set; }

    public List<string> Permissions { get; private set; } = [];

    public static Role CreateSystem(Guid tenantId, string code, IEnumerable<string> permissions) =>
        new(Guid.CreateVersion7(), tenantId, Guard.NotEmpty(code), code, isSystem: true, Normalize(permissions));

    public static Role CreateCustom(Guid tenantId, string name, IEnumerable<string> permissions) =>
        new(Guid.CreateVersion7(), tenantId, Guard.MaxLength(Guard.NotEmpty(name), IdentityLimits.RoleNameMaxLength), null, isSystem: false, Normalize(permissions));

    public Result Update(string name, IEnumerable<string> permissions)
    {
        if (IsSystem)
        {
            return Error.Rule(IdentityErrors.RoleSystemReadOnly);
        }

        Name = Guard.MaxLength(Guard.NotEmpty(name), IdentityLimits.RoleNameMaxLength);
        Permissions = Normalize(permissions);
        return Result.Success();
    }

    /// <summary>Sistem rolü senkronizasyonu (salt-okur kural dışı): izin seti farklıysa yeniden yazar; değişiklik olduysa true.</summary>
    public bool SyncPermissionsUnchecked(IEnumerable<string> desired)
    {
        var target = Normalize(desired);
        if (target.SequenceEqual(Permissions, StringComparer.Ordinal))
        {
            return false;
        }

        Permissions = target;
        return true;
    }

    public Result EnsureDeletable(int memberCount)
    {
        if (IsSystem)
        {
            return Error.Rule(IdentityErrors.RoleSystemReadOnly);
        }

        if (memberCount > 0)
        {
            return Error.Conflict(IdentityErrors.RoleInUse, (IdentityErrors.Args.Count, memberCount.ToString(CultureInfo.InvariantCulture)));
        }

        return Result.Success();
    }

    public bool Has(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);

    private static List<string> Normalize(IEnumerable<string> permissions) =>
        permissions.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

/// <summary>Her organizasyona tohumlanan sistem rolleri. Ad = kod (istemci görünen adı kendisi çevirir, K8).</summary>
public static class SystemRoleCodes
{
    /// <summary>Tüm izinler; organizasyonda en az bir aktif Administrator kalmalıdır.</summary>
    public const string Administrator = "Administrator";

    /// <summary>Tüm <c>crm.*</c> izinleri + <c>org.users.read</c>.</summary>
    public const string Standard = "Standard";

    public static readonly IReadOnlyList<string> All = [Administrator, Standard];
}
