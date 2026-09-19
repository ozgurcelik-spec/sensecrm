using Crm.Modules.Sales.Domain.Leads;
using Crm.Shared.Contracts.Paging;

namespace Crm.Modules.Sales.Application;

/// <summary>
/// Okuma tarafı (sorgu handler'ları): projeksiyonlar Infrastructure'da EF ile, kiracı ve yumuşak silme filtresi altında
/// üretilir; sıralama yalnız beyaz listedeki alanlarda, arama parametreli (SQL birleştirme yok). Sahip adları dolu döner.
/// </summary>
public interface ISalesReadStore
{
    Task<PagedResult<AccountDto>> ListAccountsAsync(PagedQuery paging, Guid? ownerUserId, string? industry, CancellationToken ct);

    Task<AccountDto?> GetAccountAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<ContactDto>> ListAccountContactsAsync(Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<DealDto>> ListAccountDealsAsync(Guid accountId, CancellationToken ct);

    Task<PagedResult<ContactDto>> ListContactsAsync(PagedQuery paging, Guid? accountId, Guid? ownerUserId, CancellationToken ct);

    Task<ContactDto?> GetContactAsync(Guid id, CancellationToken ct);

    Task<PagedResult<LeadDto>> ListLeadsAsync(PagedQuery paging, LeadStatus? status, LeadSource? source, Guid? ownerUserId, CancellationToken ct);

    Task<LeadDto?> GetLeadAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<PipelineDto>> ListPipelinesAsync(CancellationToken ct);

    Task<PipelineDto?> GetPipelineAsync(Guid id, CancellationToken ct);

    Task<PagedResult<DealDto>> ListDealsAsync(PagedQuery paging, DealFilter filter, CancellationToken ct);

    Task<DealDto?> GetDealAsync(Guid id, CancellationToken ct);

    /// <summary>Huni yoksa null.</summary>
    Task<DealBoardDto?> GetBoardAsync(Guid pipelineId, Guid? ownerUserId, CancellationToken ct);
}

/// <summary>
/// Varsayılan satış hunisini organizasyon dilinde tohumlar (idempotent, organizasyon başına advisory lock ile yarışa dayanıklı).
/// Yeni organizasyon olayı, API başlangıcı (mevcut organizasyonlar) ve tembel güvence yolu aynı uygulamayı kullanır.
/// </summary>
public interface IDefaultPipelineSeeder
{
    /// <returns>Huni bu çağrıda oluşturulduysa true; organizasyonda zaten huni varsa false.</returns>
    Task<bool> EnsureAsync(Guid tenantId, string locale, CancellationToken ct);
}

/// <summary>
/// Satış raporları okuma tarafı (Milestone 3): kiracı + yumuşak silme filtresi altında toplama sorguları. Aralıklar UTC
/// yarı açık <c>[from, toExclusive)</c>'dir; kiracı saat diliminden UTC'ye çeviri handler'dadır.
/// </summary>
public interface ISalesReportStore
{
    /// <summary>Huni yoksa null.</summary>
    Task<Reports.FunnelDto?> GetFunnelAsync(Guid pipelineId, CancellationToken ct);

    /// <summary>Kapanış anı aralıkta olan kazanılan/kaybedilen fırsatlar, kiracı saat diliminde (IANA) yerel gün + tür bazında.</summary>
    Task<IReadOnlyList<Reports.ClosedDealDayRow>> GetClosedDealsByDayAsync(DateTime fromUtc, DateTime toExclusiveUtc, string timeZoneId, CancellationToken ct);

    /// <summary>Oluşturulma anı aralıkta olan potansiyel müşteriler kaynak bazında (yalnız potansiyeli olan kaynaklar).</summary>
    Task<IReadOnlyList<Reports.LeadSourceReportRow>> GetLeadsBySourceAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct);

    /// <summary>
    /// Sahip bazında: açık fırsatlar (güncel durum, aralıktan bağımsız), aralıkta kazanılanlar (<c>closedAt</c>) ve aralıkta
    /// oluşturulan potansiyeller.
    /// </summary>
    Task<IReadOnlyList<Reports.OwnerTotals>> GetOwnerTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct);
}
