using Crm.Modules.Marketing.Domain.Members;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Persistence;

namespace Crm.Modules.Marketing.Application;

/// <summary>Marketing modülünün UnitOfWork'ü (modül DbContext'i uygular); event/worker yollarında SaveChanges için.</summary>
public interface IMarketingUnitOfWork : IUnitOfWork
{
}

/// <summary>
/// Okuma tarafı (sorgu handler'ları): projeksiyonlar Infrastructure'da EF ile, kiracı ve yumuşak silme filtresi altında üretilir;
/// sıralama yalnız beyaz listedeki alanlarda, arama parametreli (SQL birleştirme yok). Üye/sahip adları toplu (sayfa başına tek
/// sorgu) çözülür. Yumuşak silinen kampanyanın üyeleri hiçbir yoldan görünmez.
/// </summary>
public interface IMarketingReadStore
{
    /// <summary>
    /// Sıralama: <c>name, type, status, startDate, endDate, budget, actualCost, createdAt</c> (varsayılan <c>-createdAt</c>; boş
    /// değerler her iki yönde de sonda; her zaman <c>createdAt</c> + <c>Id</c> ile kararlı).
    /// </summary>
    Task<PagedResult<CampaignDto>> ListCampaignsAsync(PagedQuery paging, CampaignFilter filter, CancellationToken ct);

    Task<CampaignDto?> GetCampaignAsync(Guid id, CancellationToken ct);

    /// <summary>Kampanya (silinmemiş, aktif kiracıda) var mı.</summary>
    Task<bool> CampaignExistsAsync(Guid id, CancellationToken ct);

    /// <summary>Sıralama: <c>addedAt</c> (varsayılan azalan), <c>statusChangedAt</c>, <c>status</c>, <c>memberType</c>; ardından <c>Id</c>.</summary>
    Task<PagedResult<CampaignMemberDto>> ListMembersAsync(Guid campaignId, PagedQuery paging, MemberFilter filter, CancellationToken ct);

    /// <summary>Kampanyanın tür + durum sayımları (tek gruplama sorgusu).</summary>
    Task<Domain.Metrics.MemberCounts> GetMemberCountsAsync(Guid campaignId, CancellationToken ct);

    /// <summary>Kaydın (lead/kişi) yumuşak silinmemiş kampanyalardaki üyelikleri, en çok 200, <c>addedAt</c> azalan.</summary>
    Task<IReadOnlyList<RecordCampaignDto>> ListRecordCampaignsAsync(CampaignMemberType type, Guid memberId, CancellationToken ct);
}

/// <summary>
/// Pazarlama raporu deposu (veritabanında toplama; kampanya başına üye sayımı <c>campaign_members</c> gruplaması). Kampanya aralığa
/// <c>startDate ∈ [from, to]</c> (yoksa <c>createdAt ∈ [fromUtc, toExclusiveUtc)</c>) ile girer; yumuşak silinenler yok, tüm durumlar sayılır.
/// </summary>
public interface IMarketingReportStore
{
    Task<MarketingSummaryDto> GetSummaryAsync(DateOnly from, DateOnly to, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct);
}
