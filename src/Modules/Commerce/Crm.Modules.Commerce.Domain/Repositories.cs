using Crm.Modules.Commerce.Domain.Orders;
using Crm.Modules.Commerce.Domain.Products;
using Crm.Modules.Commerce.Domain.Quotes;

namespace Crm.Modules.Commerce.Domain;

// Depo arayüzleri komut tarafıdır (agregat yükle/ekle/sil). Sorgular kiracı ve yumuşak silme filtresi altında çalışır:
// başka organizasyonun kaydı burada hiç bulunmaz (null → not_found).

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Kod (normalize) kiracıda silinmemiş başka bir üründe var mı.</summary>
    Task<bool> CodeExistsAsync(string codeNormalized, Guid? exceptProductId, CancellationToken ct);

    void Add(Product product);

    void Remove(Product product);
}

public interface IQuoteRepository
{
    /// <summary>Kalemleriyle birlikte yükler.</summary>
    Task<Quote?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Quote quote);

    void Remove(Quote quote);

    /// <summary>
    /// Yalnız kalemleri değişen belgede başlığın da <c>xmin</c> belirtecini ilerletir (kalem yarışında çakışma yakalansın diye
    /// <c>ModifiedDate</c> işaretlenir).
    /// </summary>
    void Touch(Quote quote);
}

public interface ISalesOrderRepository
{
    /// <summary>Kalemleriyle birlikte yükler.</summary>
    Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Teklife bağlı, silinmemiş sipariş var mı (iptal edilmiş dahil).</summary>
    Task<bool> ExistsForQuoteAsync(Guid quoteId, CancellationToken ct);

    void Add(SalesOrder order);

    void Remove(SalesOrder order);

    void Touch(SalesOrder order);
}
