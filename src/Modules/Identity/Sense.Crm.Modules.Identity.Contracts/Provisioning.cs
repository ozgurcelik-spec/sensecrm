using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Identity.Contracts;

/// <summary>
/// Yeni organizasyon açıldığında (kayıt) yayınlanan integration event; iş modülleri kendi varsayılan verilerini
/// (ör. Sales: varsayılan satış hunisi) bununla tohumlar. Kayıt transaction'ıyla aynı anda outbox'a yazılır.
/// </summary>
public sealed record OrganizationCreated(Guid TenantId, string Name, string DefaultLocale) : IntegrationEvent(TenantId);

/// <summary>Bir organizasyonun modüller arası paylaşılan özeti. <see cref="TimeZone"/> IANA kimliğidir (ör. Europe/Istanbul).</summary>
public sealed record TenantInfo(Guid Id, string Name, string DefaultLocale, string TimeZone);

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
