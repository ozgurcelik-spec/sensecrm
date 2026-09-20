using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Commerce.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Commerce.Infrastructure;

/// <summary>
/// Commerce kullanım sayaçları (M7, M9C ile genişledi): yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b> (global kiracı + yumuşak silme filtresi altında; başka kiracı ve
/// yumuşak silinenler sayılmaz). Anahtarlar <c>{modül}.{varlık}</c> + modül toplamı <c>{modül}.records</c> (= alt toplamların toplamı). Kalemler, fiyat listesi girdileri,
/// tahsilatlar ve firma varsayılanları sayılmaz. Mevcut kiracılarda <c>commerce.records</c> yeni türler kadar artar; limit aşımı yalnız yeni oluşturmayı engeller (veri silinmez).
/// </summary>
public sealed class CommerceUsageReporter(CommerceDbContext db) : IUsageReporter
{
    public string Module => CommerceDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var products = await db.Products.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var quotes = await db.Quotes.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var orders = await db.SalesOrders.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var invoices = await db.Invoices.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var purchaseOrders = await db.PurchaseOrders.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var vendors = await db.Vendors.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var priceBooks = await db.PriceBooks.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("commerce.products", products),
            new UsageMetric("commerce.quotes", quotes),
            new UsageMetric("commerce.orders", orders),
            new UsageMetric("commerce.invoices", invoices),
            new UsageMetric("commerce.purchase_orders", purchaseOrders),
            new UsageMetric("commerce.vendors", vendors),
            new UsageMetric("commerce.price_books", priceBooks),
            new UsageMetric(UsageKeys.Records(Module), products + quotes + orders + invoices + purchaseOrders + vendors + priceBooks),
        ];
    }
}
