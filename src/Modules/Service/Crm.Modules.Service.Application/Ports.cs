using Crm.Shared.Contracts.Paging;

namespace Crm.Modules.Service.Application;

/// <summary>
/// Okuma tarafı (sorgu handler'ları): projeksiyonlar Infrastructure'da EF ile, kiracı ve yumuşak silme filtresi altında üretilir;
/// sıralama yalnız beyaz listedeki alanlarda, arama parametreli (SQL birleştirme yok). Adlar (üye, firma, kişi) sayfa başına toplu çözülür.
/// SLA durumu/süzgeci <c>nowUtc</c> ile okuma anında hesaplanır (yanıt ve süzgeç aynı tanım).
/// </summary>
public interface ICaseReadStore
{
    /// <summary>Sıralama: <c>number, subject, status, priority, dueAt, createdAt, updatedAt</c>; varsayılan <c>-createdAt</c>; her zaman <c>Id</c> ile kararlı.</summary>
    Task<PagedResult<CaseListItemDto>> ListAsync(PagedQuery paging, CaseFilter filter, DateTime nowUtc, CancellationToken ct);

    Task<CaseDetailDto?> GetAsync(Guid id, DateTime nowUtc, CancellationToken ct);

    Task<CaseSummaryDto> GetSummaryAsync(Guid userId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Yorumlar + olaylar (<c>UNION ALL</c>), en yeni önce; talep yok/silinmiş/başka kiracıysa null.</summary>
    Task<PagedResult<TimelineItemDto>?> GetTimelineAsync(Guid caseId, PagedQuery paging, CancellationToken ct);

    /// <summary>Kohort: <c>createdAt ∈ [fromUtc, toExclusiveUtc)</c>, silinmemiş talepler; "ihlal" <paramref name="nowUtc"/> ile hesaplanır.</summary>
    Task<ServiceSummaryTotals> GetSummaryTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateTime nowUtc, CancellationToken ct);

    Task<IReadOnlyList<AssigneeTotals>> GetAssigneeTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateTime nowUtc, CancellationToken ct);
}

/// <summary>
/// Talep numarası üreticisi: <c>C-{yıl}-{sıra:D4}</c>. Sayaç talep INSERT'iyle <b>aynı transaction'da</b> tek ham SQL'le artırılır
/// (<c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>): satır kilidi eşzamanlı oluşturmaları sıraya dizer → numara benzersiz ve boşluksuz;
/// transaction geri alınırsa sayaç da geri alınır. Doğrulamalardan sonra en son adım olarak çağrılır.
/// </summary>
public interface ICaseNumberGenerator
{
    Task<string> NextAsync(int year, CancellationToken ct);
}

/// <summary>
/// SLA politikası tohumlama: kiracıda eksik öncelikler varsayılan değerlerle eklenir (dört satır tamamsa hiçbir şey yapmaz →
/// idempotent ve eşzamanlı çağrıya dayanıklı). Worker olayı, API açılışı ve tembel güvence aynı uygulamayı kullanır.
/// </summary>
public interface IDefaultSlaPolicySeeder
{
    /// <returns>Bu çağrıda en az bir satır eklendiyse true.</returns>
    Task<bool> EnsureAsync(Guid tenantId, CancellationToken ct);
}
