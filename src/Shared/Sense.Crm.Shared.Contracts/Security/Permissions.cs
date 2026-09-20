namespace Sense.Crm.Shared.Contracts.Security;

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

/// <summary>
/// İstek (command/query) için bilinçli "izin gerekmez, her kimliği doğrulanmış kullanıcı çağırabilir" işaretidir (M6: her istek ya
/// <see cref="RequiresPermissionAttribute"/> ya da bu işareti taşımalıdır; mimari test yeni işaretsiz istekleri reddeder).
/// Yalnız kullanıcının kendi verisi / kendi organizasyonunun temel bilgisi gibi yerlerde kullanılır; handler kendi içinde
/// çağıranı sınırlar (kendi kaydı, kiracı filtresi). <see cref="Reason"/> gerekçedir.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AnyAuthenticatedUserAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Platform yöneticisi (ürünü işleten taraf) isteği: <c>isPlatformAdmin</c> kiracı izni DEĞİLDİR. <c>AuthorizationBehaviour</c> her
/// istekte <see cref="IPlatformAdminVerifier"/> ile bayrağı ve hesap aktifliğini veritabanından doğrular; kiracı durumundan bağımsızdır
/// (<c>EntitlementBehaviour</c> atlar). Her istek tam olarak birini taşır: <c>[RequiresPermission]</c> | <c>[AnyAuthenticatedUser]</c> | bu.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PlatformAdminOnlyAttribute : Attribute;

/// <summary>Kullanıcı aktif <b>ve</b> platform yöneticisi bayrağı veritabanında var mı (JWT bayrağına güvenilmez). Identity uygular.</summary>
public interface IPlatformAdminVerifier
{
    Task<bool> IsPlatformAdminAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Platform denetimi yazma noktası (Platform uygular): başka bir modülün (ör. Identity <c>POST /platform/organizations</c>) kendi transaction'ında yapamadığı platform eylemlerini
/// <c>platform_audit_entries</c>'a yazar (aktör: çağıran platform yöneticisi). Platform yüklü değilse hiçbir şey yazılmaz. <c>details</c> kişisel veri içermez.
/// </summary>
public interface IPlatformAuditSink
{
    Task RecordAsync(string action, Guid? targetTenantId, string? targetTenantName, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default);
}

/// <summary>Handler üzerinde gerekli izni bildirir; AuthorizationBehaviour denetler.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}
