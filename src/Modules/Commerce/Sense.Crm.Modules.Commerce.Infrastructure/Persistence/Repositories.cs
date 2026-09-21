using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Modules.Commerce.Domain.Vendors;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Commerce.Infrastructure.Persistence;

// Tüm sorgular ModuleDbContext'in "Tenant" ve "SoftDelete" global filtreleri altında çalışır.

public sealed class ProductRepository(CommerceDbContext db) : IProductRepository
{
    public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct) => db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<bool> CodeExistsAsync(string codeNormalized, Guid? exceptProductId, CancellationToken ct) =>
        db.Products.AnyAsync(p => p.CodeNormalized == codeNormalized && (exceptProductId == null || p.Id != exceptProductId), ct);

    public async Task<IReadOnlyList<Product>> ListByVendorAsync(Guid vendorId, CancellationToken ct) =>
        await db.Products.Where(p => p.VendorId == vendorId).ToListAsync(ct).ConfigureAwait(false);

    public void Add(Product product) => db.Products.Add(product);

    public void Remove(Product product) => db.Products.Remove(product);
}

/// <summary>
/// Teklif deposu. Silme yumuşaktır ve <b>doğrudan işaretlenir</b> (<c>EntityState.Deleted</c> kullanılmaz): kalemler başlığa cascade
/// bağlı olduğundan <c>Remove</c> onları da fiziksel silerdi; yumuşak silinen teklif kalemlerini korur.
/// </summary>
public sealed class QuoteRepository(CommerceDbContext db, TimeProvider clock, ICurrentUser user) : IQuoteRepository
{
    public Task<Quote?> GetByIdAsync(Guid id, CancellationToken ct) => db.Quotes.Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == id, ct);

    public void Add(Quote quote) => db.Quotes.Add(quote);

    public void Remove(Quote quote) => SoftDelete.Mark(quote, clock, user);

    public void Touch(Quote quote) => SoftDelete.Touch(db, quote);
}

public sealed class SalesOrderRepository(CommerceDbContext db, TimeProvider clock, ICurrentUser user) : ISalesOrderRepository
{
    public Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken ct) => db.SalesOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<bool> ExistsForQuoteAsync(Guid quoteId, CancellationToken ct) => db.SalesOrders.AnyAsync(o => o.QuoteId == quoteId, ct);

    public void Add(SalesOrder order) => db.SalesOrders.Add(order);

    public void Remove(SalesOrder order) => SoftDelete.Mark(order, clock, user);

    public void Touch(SalesOrder order) => SoftDelete.Touch(db, order);
}

public sealed class InvoiceRepository(CommerceDbContext db, TimeProvider clock, ICurrentUser user) : IInvoiceRepository
{
    public Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Invoices.Include(i => i.Lines).Include(i => i.Payments).AsSplitQuery().FirstOrDefaultAsync(i => i.Id == id, ct);

    public Task<bool> ExistsActiveForOrderAsync(Guid orderId, CancellationToken ct) =>
        db.Invoices.AnyAsync(i => i.OrderId == orderId && i.Status != InvoiceStatus.Cancelled, ct);

    public void Add(Invoice invoice) => db.Invoices.Add(invoice);

    public void Remove(Invoice invoice) => SoftDelete.Mark(invoice, clock, user);

    public void Touch(Invoice invoice) => SoftDelete.Touch(db, invoice);
}

public sealed class PurchaseOrderRepository(CommerceDbContext db, TimeProvider clock, ICurrentUser user) : IPurchaseOrderRepository
{
    public Task<PurchaseOrder?> GetByIdAsync(Guid id, CancellationToken ct) => db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<bool> ExistsForVendorAsync(Guid vendorId, CancellationToken ct) => db.PurchaseOrders.AnyAsync(o => o.VendorId == vendorId, ct);

    public void Add(PurchaseOrder order) => db.PurchaseOrders.Add(order);

    public void Remove(PurchaseOrder order) => SoftDelete.Mark(order, clock, user);

    public void Touch(PurchaseOrder order) => SoftDelete.Touch(db, order);
}

public sealed class VendorRepository(CommerceDbContext db, TimeProvider clock, ICurrentUser user) : IVendorRepository
{
    public Task<Vendor?> GetByIdAsync(Guid id, CancellationToken ct) => db.Vendors.FirstOrDefaultAsync(v => v.Id == id, ct);

    public void Add(Vendor vendor) => db.Vendors.Add(vendor);

    public void Remove(Vendor vendor) => SoftDelete.Mark(vendor, clock, user);
}

/// <summary>
/// Fiyat listesi deposu. Liste yumuşak silinir; girdileri ve firma varsayılanları aynı transaction'da <b>fiziksel</b> silinir (yalnız liste kimliğine bağlıdırlar).
/// Girdiler <c>IAuditLogged</c> olduğundan silme denetim satırı bırakır.
/// </summary>
public sealed class PriceBookRepository(CommerceDbContext db, TimeProvider clock, ICurrentUser user) : IPriceBookRepository
{
    public Task<PriceBook?> GetByIdAsync(Guid id, CancellationToken ct) => db.PriceBooks.FirstOrDefaultAsync(b => b.Id == id, ct);

    public Task<bool> NameExistsAsync(string nameNormalized, Guid? exceptPriceBookId, CancellationToken ct) =>
        db.PriceBooks.AnyAsync(b => b.NameNormalized == nameNormalized && (exceptPriceBookId == null || b.Id != exceptPriceBookId), ct);

    public void Add(PriceBook book) => db.PriceBooks.Add(book);

    public async Task RemoveAsync(PriceBook book, CancellationToken ct)
    {
        SoftDelete.Mark(book, clock, user);
        db.PriceBookEntries.RemoveRange(await db.PriceBookEntries.Where(e => e.PriceBookId == book.Id).ToListAsync(ct).ConfigureAwait(false));
        db.AccountPriceBooks.RemoveRange(await db.AccountPriceBooks.Where(a => a.PriceBookId == book.Id).ToListAsync(ct).ConfigureAwait(false));
    }

    public void Touch(PriceBook book) => db.Entry(book).Property(x => x.ModifiedDate).IsModified = true;

    public Task<PriceBookEntry?> GetEntryAsync(Guid priceBookId, Guid productId, CancellationToken ct) =>
        db.PriceBookEntries.FirstOrDefaultAsync(e => e.PriceBookId == priceBookId && e.ProductId == productId, ct);

    public Task<int> CountEntriesAsync(Guid priceBookId, CancellationToken ct) => db.PriceBookEntries.CountAsync(e => e.PriceBookId == priceBookId, ct);

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetEntryPricesAsync(Guid priceBookId, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        var keys = productIds.ToArray();
        return await db.PriceBookEntries.AsNoTracking()
            .Where(e => e.PriceBookId == priceBookId && keys.Contains(e.ProductId))
            .ToDictionaryAsync(e => e.ProductId, e => e.UnitPrice, ct).ConfigureAwait(false);
    }

    public void AddEntry(PriceBookEntry entry) => db.PriceBookEntries.Add(entry);

    public void RemoveEntry(PriceBookEntry entry) => db.PriceBookEntries.Remove(entry);

    public async Task RemoveEntriesForProductAsync(Guid productId, CancellationToken ct) =>
        db.PriceBookEntries.RemoveRange(await db.PriceBookEntries.Where(e => e.ProductId == productId).ToListAsync(ct).ConfigureAwait(false));

    public Task<AccountPriceBook?> GetAccountDefaultAsync(Guid accountId, CancellationToken ct) =>
        db.AccountPriceBooks.FirstOrDefaultAsync(a => a.AccountId == accountId, ct);

    public void AddAccountDefault(AccountPriceBook defaultBook) => db.AccountPriceBooks.Add(defaultBook);

    public void RemoveAccountDefault(AccountPriceBook defaultBook) => db.AccountPriceBooks.Remove(defaultBook);
}

internal static class SoftDelete
{
    public static void Mark(ISoftDelete entity, TimeProvider clock, ICurrentUser user)
    {
        entity.IsDeleted = true;
        entity.DeletedAt = clock.GetUtcNow().UtcDateTime;
        entity.DeletedUserId = user.UserId;
    }

    /// <summary>Yalnız kalemleri değişen belgede başlığın <c>xmin</c>'ini ilerletir: <c>ModifiedDate</c> işaretlenir (interceptor değeri günceller).</summary>
    public static void Touch(CommerceDbContext db, CommerceDocument document) =>
        db.Entry(document).Property(x => x.ModifiedDate).IsModified = true;
}

/// <summary>Kalem ürün bağı doğrulaması ve fiyat çözümü için ürün özeti (kiracı + yumuşak silme filtresi altında).</summary>
public sealed class ProductLookup(CommerceDbContext db) : IProductLookup
{
    public async Task<IReadOnlyDictionary<Guid, ProductInfo>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var keys = ids.ToArray();
        var rows = await db.Products.AsNoTracking()
            .Where(p => keys.Contains(p.Id))
            .Select(p => new { p.Id, p.IsActive, p.Currency, p.UnitPrice, p.PurchasePrice, p.Name, p.Code })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => new ProductInfo(r.IsActive, r.Currency, r.UnitPrice, r.PurchasePrice, r.Name, r.Code));
    }
}
