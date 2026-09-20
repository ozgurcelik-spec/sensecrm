using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Workflows.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Workflows.Infrastructure;

/// <summary>
/// Workflows kullanım sayaçları (M7): yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b> (global kiracı + yumuşak silme filtresi altında; başka kiracı ve
/// yumuşak silinenler sayılmaz). Anahtarlar <c>{modül}.{varlık}</c> + modül toplamı <c>{modül}.records</c> (= alt toplamların toplamı).
/// </summary>
public sealed class WorkflowsUsageReporter(WorkflowsDbContext db) : IUsageReporter
{
    public string Module => WorkflowsDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var rules = await db.Rules.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("workflows.workflow_rules", rules),
            new UsageMetric(UsageKeys.Records(Module), rules),
        ];
    }
}
