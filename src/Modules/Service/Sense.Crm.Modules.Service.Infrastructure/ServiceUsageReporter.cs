using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Service.Infrastructure;

/// <summary>
/// Service kullanım sayaçları (M7): yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b> (global kiracı + yumuşak silme filtresi altında; başka kiracı ve
/// yumuşak silinenler sayılmaz). Anahtarlar <c>{modül}.{varlık}</c> + modül toplamı <c>{modül}.records</c> (= alt toplamların toplamı).
/// </summary>
public sealed class ServiceUsageReporter(ServiceDbContext db) : IUsageReporter
{
    public string Module => ServiceDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var cases = await db.Cases.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("service.cases", cases),
            new UsageMetric(UsageKeys.Records(Module), cases),
        ];
    }
}
