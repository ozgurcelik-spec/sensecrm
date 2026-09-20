using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Modules.Commerce.Domain.Vendors;

namespace Sense.Crm.Modules.Commerce.Domain;

// Depo arayüzleri komut tarafıdır (agregat yükle/ekle/sil). Sorgular kiracı ve yumuşak silme filtresi altında çalışır:
// başka organizasyonun kaydı burada hiç bulunmaz (null → not_found).

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Kod (normalize) kiracıda silinmemiş başka bir üründe var mı.</summary>
    Task<bool> CodeExistsAsync(string codeNormalized, Guid? exceptProductId, CancellationToken ct);

    /// <summary>Birincil tedarikçisi <paramref name="vendorId"/> olan (silinmemiş) ürünler (tedarikçi silinince bağ temizlenir).</summary>
    Task<IReadOnlyList<Product>> ListByVendorAsync(Guid vendorId, CancellationToken ct);

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

public interface IInvoiceRepository
{
    /// <summary>Kalemleri ve tahsilatlarıyla birlikte yükler.</summary>
    Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Siparişe bağlı, iptal edilmemiş ve silinmemiş fatura var mı (<c>ux_invoices_tenant_order</c> ile aynı tanım).</summary>
    Task<bool> ExistsActiveForOrderAsync(Guid orderId, CancellationToken ct);

    void Add(Invoice invoice);

    void Remove(Invoice invoice);

    void Touch(Invoice invoice);
}

public interface IPurchaseOrderRepository
{
    /// <summary>Kalemleriyle birlikte yükler.</summary>
    Task<PurchaseOrder?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Tedarikçiye bağlı silinmemiş satın alma emri var mı.</summary>
    Task<bool> ExistsForVendorAsync(Guid vendorId, CancellationToken ct);

    void Add(PurchaseOrder order);

    void Remove(PurchaseOrder order);

    void Touch(PurchaseOrder order);
}

public interface IVendorRepository
{
    Task<Vendor?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Vendor vendor);

    void Remove(Vendor vendor);
}

public interface IPriceBookRepository
{
    Task<PriceBook?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Ad (normalize) kiracıda silinmemiş başka bir listede var mı.</summary>
    Task<bool> NameExistsAsync(string nameNormalized, Guid? exceptPriceBookId, CancellationToken ct);

    void Add(PriceBook book);

    /// <summary>Yumuşak siler; girdileri ve firma varsayılanlarını aynı transaction'da fiziksel siler.</summary>
    Task RemoveAsync(PriceBook book, CancellationToken ct);

    /// <summary>Başlığın <c>updatedAt</c>'ini ilerletir (girdi yazımı).</summary>
    void Touch(PriceBook book);

    Task<PriceBookEntry?> GetEntryAsync(Guid priceBookId, Guid productId, CancellationToken ct);

    Task<int> CountEntriesAsync(Guid priceBookId, CancellationToken ct);

    /// <summary>Liste girdilerinden ürün → birim fiyat (yalnız verilen ürünler için).</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetEntryPricesAsync(Guid priceBookId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    void AddEntry(PriceBookEntry entry);

    void RemoveEntry(PriceBookEntry entry);

    /// <summary>Ürün silinince tüm listelerdeki girdileri fiziksel siler (aynı transaction).</summary>
    Task RemoveEntriesForProductAsync(Guid productId, CancellationToken ct);

    Task<AccountPriceBook?> GetAccountDefaultAsync(Guid accountId, CancellationToken ct);

    void AddAccountDefault(AccountPriceBook defaultBook);

    void RemoveAccountDefault(AccountPriceBook defaultBook);
}
