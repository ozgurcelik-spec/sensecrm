using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Activities.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Activities.Infrastructure;

/// <summary>
/// Activities kullanım sayaçları (M7): yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b> (global kiracı + yumuşak silme filtresi altında; başka kiracı ve
/// yumuşak silinenler sayılmaz). Anahtarlar <c>{modül}.{varlık}</c> + modül toplamı <c>{modül}.records</c> (= alt toplamların toplamı).
/// </summary>
public sealed class ActivitiesUsageReporter(ActivitiesDbContext db) : IUsageReporter
{
    public string Module => ActivitiesDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var activities = await db.Activities.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("activities.activities", activities),
            new UsageMetric(UsageKeys.Records(Module), activities),
        ];
    }
}
