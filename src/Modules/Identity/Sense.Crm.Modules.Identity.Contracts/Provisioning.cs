using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Identity.Contracts;

/// <summary>
/// Yeni organizasyon açıldığında (kayıt) yayınlanan integration event; iş modülleri kendi varsayılan verilerini
/// (ör. Sales: varsayılan satış hunisi) bununla tohumlar. Kayıt transaction'ıyla aynı anda outbox'a yazılır. M7: <c>Slug</c>, <c>Origin</c>,
/// <c>PlanCode</c>, <c>TrialEndsOn</c> Platform hesabını açmak içindir (hepsi varsayılanlıdır: eski outbox satırları çözülebilir).
/// </summary>
public sealed record OrganizationCreated(
    Guid TenantId,
    string Name,
    string DefaultLocale,
    string? Slug = null,
    OrganizationOrigin Origin = OrganizationOrigin.Signup,
    string? PlanCode = null,
    DateOnly? TrialEndsOn = null) : IntegrationEvent(TenantId);

/// <summary>Organizasyonun açılış yolu (Platform hesabının varsayılan planı/kaynağı buradan seçilir).</summary>
public enum OrganizationOrigin
{
    /// <summary>Kendi kendine kayıt (<c>auth/signup</c>): varsayılan kayıt planı + deneme.</summary>
    Signup = 0,

    /// <summary>Platform yöneticisi (<c>POST /platform/organizations</c>): istekteki plan/deneme, yoksa varsayılan plan.</summary>
    Platform = 1,

    /// <summary>Migrator <c>create-platform-admin</c>: sistem (işletim) organizasyonu.</summary>
    Bootstrap = 2,
}

/// <summary>Organizasyon adı değişti (Platform konsolundaki ad/slug okuma kopyası için).</summary>
public sealed record OrganizationUpdated(Guid TenantId, string Name) : IntegrationEvent(TenantId);

/// <summary>Bir organizasyonun modüller arası paylaşılan özeti. <see cref="TimeZone"/> IANA kimliğidir (ör. Europe/Istanbul).</summary>
public sealed record TenantInfo(Guid Id, string Name, string DefaultLocale, string TimeZone, string? Slug = null, DateTimeOffset? CreatedAt = null);

/// <summary>
/// Modüller arası organizasyon dizini (kiracı filtresi dışı; yalnız başlangıçta mevcut organizasyonlara varsayılan veri
/// tohumlamak gibi sistem işleri içindir).
/// </summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantInfo>> ListAllAsync(CancellationToken cancellationToken = default);

    Task<TenantInfo?> FindAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bir modülün denetim kaydında görünen varlık türleri ve bu türlerin kayıt bazlı okuma izinleri. <c>GET /audit?entityType&amp;entityId</c>
/// ucu, ilgili <c>crm.&lt;kaynak&gt;.read</c> iznine sahip kullanıcıya o türün denetim kayıtlarını açar.
/// </summary>
public interface IAuditEntityPermissions
{
    /// <summary>CLR tip adı (ör. "Account") → gereken okuma izni.</summary>
    IReadOnlyDictionary<string, string> ReadPermissionsByEntityType { get; }
}
