using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Shared.Contracts.Context;

namespace Sense.Crm.Modules.Commerce.Infrastructure.Persistence;

// Tüm sorgular ModuleDbContext'in "Tenant" ve "SoftDelete" global filtreleri altında çalışır.

public sealed class ProductRepository(CommerceDbContext db) : IProductRepository
{
    public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct) => db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<bool> CodeExistsAsync(string codeNormalized, Guid? exceptProductId, CancellationToken ct) =>
        db.Products.AnyAsync(p => p.CodeNormalized == codeNormalized && (exceptProductId == null || p.Id != exceptProductId), ct);

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

internal static class SoftDelete
{
    public static void Mark(Domain.Documents.SalesDocument document, TimeProvider clock, ICurrentUser user)
    {
        document.IsDeleted = true;
        document.DeletedAt = clock.GetUtcNow().UtcDateTime;
        document.DeletedUserId = user.UserId;
    }

    /// <summary>Yalnız kalemleri değişen belgede başlığın <c>xmin</c>'ini ilerletir: <c>ModifiedDate</c> işaretlenir (interceptor değeri günceller).</summary>
    public static void Touch(CommerceDbContext db, Domain.Documents.SalesDocument document) =>
        db.Entry(document).Property(x => x.ModifiedDate).IsModified = true;
}

/// <summary>Kalem ürün bağı doğrulaması için ürün özeti (kiracı + yumuşak silme filtresi altında).</summary>
public sealed class ProductLookup(CommerceDbContext db) : IProductLookup
{
    public async Task<IReadOnlyDictionary<Guid, ProductInfo>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var keys = ids.ToArray();
        var rows = await db.Products.AsNoTracking()
            .Where(p => keys.Contains(p.Id))
            .Select(p => new { p.Id, p.IsActive, p.Currency })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => new ProductInfo(r.IsActive, r.Currency));
    }
}
