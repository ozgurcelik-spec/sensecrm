namespace Crm.Shared.Contracts.Security;

/// <summary>
/// Modüllerin kataloğa katkı verdiği izin tanımı. Key biçimi: {grup}.{kaynak}.{eylem} (ör. <c>crm.leads.read</c>).
/// <paramref name="Group"/> istemcinin izin ekranında grupladığı üst başlıktır (<c>org</c> | <c>crm</c>); görünen adlar istemcide çevrilir (K8).
/// </summary>
public sealed record Permission(string Key, string Module, string Group)
{
    public override string ToString() => Key;
}

/// <summary>Her modül kendi izinlerini bu arayüzle sunar; Identity modülü birleşik kataloğu döner.</summary>
public interface IPermissionProvider
{
    IEnumerable<Permission> Permissions { get; }
}

/// <summary>Kullanıcının aktif organizasyondaki etkin izinlerini çözer (HybridCache'li). Basit RBAC (K7): üyelik → rol → izinler.</summary>
public interface IPermissionService
{
    Task<bool> HasAsync(Guid userId, string permission, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Handler üzerinde gerekli izni bildirir; AuthorizationBehaviour denetler.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}
