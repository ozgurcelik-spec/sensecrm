using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Marketing.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Marketing.Infrastructure;

/// <summary>
/// Marketing kullanım sayaçları (M7): yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b> (global kiracı + yumuşak silme filtresi altında; başka kiracı ve
/// yumuşak silinenler sayılmaz). Anahtarlar <c>{modül}.{varlık}</c> + modül toplamı <c>{modül}.records</c> (= alt toplamların toplamı).
/// </summary>
public sealed class MarketingUsageReporter(MarketingDbContext db) : IUsageReporter
{
    public string Module => MarketingDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var campaigns = await db.Campaigns.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("marketing.campaigns", campaigns),
            new UsageMetric(UsageKeys.Records(Module), campaigns),
        ];
    }
}
