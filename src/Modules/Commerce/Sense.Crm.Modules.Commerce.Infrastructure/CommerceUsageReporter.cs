using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Commerce.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Commerce.Infrastructure;

/// <summary>
/// Commerce kullanım sayaçları (M7): yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b> (global kiracı + yumuşak silme filtresi altında; başka kiracı ve
/// yumuşak silinenler sayılmaz). Anahtarlar <c>{modül}.{varlık}</c> + modül toplamı <c>{modül}.records</c> (= alt toplamların toplamı).
/// </summary>
public sealed class CommerceUsageReporter(CommerceDbContext db) : IUsageReporter
{
    public string Module => CommerceDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var products = await db.Products.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var quotes = await db.Quotes.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var orders = await db.SalesOrders.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("commerce.products", products),
            new UsageMetric("commerce.quotes", quotes),
            new UsageMetric("commerce.orders", orders),
            new UsageMetric(UsageKeys.Records(Module), products + quotes + orders),
        ];
    }
}
