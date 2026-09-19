using Sense.Crm.Shared.Contracts.Paging;

namespace Sense.Crm.Modules.Activities.Application;

/// <summary>
/// Okuma tarafı (sorgu handler'ları): projeksiyonlar Infrastructure'da EF ile, kiracı ve yumuşak silme filtresi altında
/// üretilir; sıralama yalnız beyaz listedeki alanlarda, arama parametreli (SQL birleştirme yok). Atanan kullanıcı adları ve
/// ilişkili kayıt adları toplu (sayfa başına tek sorgu) çözülür.
/// </summary>
public interface IActivityReadStore
{
    /// <summary>Sıralama: <c>dueAt</c> (varsayılan, artan; boş olanlar her yönde sonda), <c>createdAt</c>, <c>subject</c>, <c>priority</c>.</summary>
    Task<PagedResult<ActivityDto>> ListAsync(PagedQuery paging, ActivityFilter filter, DateTime nowUtc, CancellationToken ct);

    Task<ActivityDto?> GetAsync(Guid id, DateTime nowUtc, CancellationToken ct);

    Task<ActivitySummaryDto> GetSummaryAsync(Guid userId, ActivitySummaryWindow window, CancellationToken ct);

    /// <summary>
    /// Kullanıcı bazında (notlar hariç): aralıkta tamamlananlar (<c>completedAt</c>), son tarihi aralıkta olan açıklar ve bunların
    /// geciken (<c>dueAt &lt; now</c>) alt kümesi. Aralık UTC yarı açık <c>[from, toExclusive)</c>.
    /// </summary>
    Task<IReadOnlyList<ActivityUserTotals>> GetUserTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateTime nowUtc, CancellationToken ct);
}
