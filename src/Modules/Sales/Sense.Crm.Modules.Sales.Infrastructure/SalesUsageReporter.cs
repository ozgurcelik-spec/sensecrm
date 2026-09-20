using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Sales.Infrastructure;

/// <summary>
/// Sales kullanım sayaçları (M7): yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b> (global kiracı + yumuşak silme filtresi altında; başka kiracı ve
/// yumuşak silinenler sayılmaz). Anahtarlar <c>{modül}.{varlık}</c> + modül toplamı <c>{modül}.records</c> (= alt toplamların toplamı).
/// </summary>
public sealed class SalesUsageReporter(SalesDbContext db) : IUsageReporter
{
    public string Module => SalesDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var accounts = await db.Accounts.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var contacts = await db.Contacts.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var leads = await db.Leads.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var deals = await db.Deals.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("sales.accounts", accounts),
            new UsageMetric("sales.contacts", contacts),
            new UsageMetric("sales.leads", leads),
            new UsageMetric("sales.deals", deals),
            new UsageMetric(UsageKeys.Records(Module), accounts + contacts + leads + deals),
        ];
    }
}
