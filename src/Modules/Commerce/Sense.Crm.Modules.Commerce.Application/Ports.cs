using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application;

/// <summary>
/// Okuma tarafı (sorgu handler'ları): projeksiyonlar Infrastructure'da EF ile, kiracı ve yumuşak silme filtresi altında üretilir;
/// sıralama yalnız beyaz listedeki alanlarda (bilinmeyen alan yok sayılır, her zaman <c>Id</c> ile kararlı), arama <c>ILIKE</c> + kaçışlı parametre.
/// </summary>
public interface IProductReadStore
{
    /// <summary>Sıralama: <c>name</c> (varsayılan, artan), <c>code</c>, <c>unitPrice</c>, <c>createdAt</c>, <c>updatedAt</c>.</summary>
    Task<PagedResult<ProductDto>> ListAsync(PagedQuery paging, ProductFilter filter, CancellationToken ct);

    Task<ProductDto?> GetAsync(Guid id, CancellationToken ct);
}

public interface IQuoteReadStore
{
    /// <summary>Sıralama: <c>number</c>, <c>subject</c>, <c>grandTotal</c>, <c>validUntil</c> (boş her yönde sonda), <c>createdAt</c> (varsayılan azalan).</summary>
    Task<PagedResult<QuoteSummaryDto>> ListAsync(PagedQuery paging, QuoteFilter filter, DateOnly today, CancellationToken ct);

    Task<QuoteDto?> GetAsync(Guid id, DateOnly today, CancellationToken ct);
}

public interface IOrderReadStore
{
    /// <summary>Sıralama: <c>number</c>, <c>subject</c>, <c>grandTotal</c>, <c>orderDate</c>, <c>createdAt</c> (varsayılan azalan).</summary>
    Task<PagedResult<OrderSummaryDto>> ListAsync(PagedQuery paging, OrderFilter filter, CancellationToken ct);

    Task<OrderDto?> GetAsync(Guid id, CancellationToken ct);
}

/// <summary>Rapor toplamları (veritabanında gruplanır). Boş durumlar dönmeyebilir; sabit sıralı doldurma uygulama katmanındadır.</summary>
public interface ICommerceReportStore
{
    /// <summary>Teklifler <c>createdAt</c> UTC yarı açık aralığında; etkin durum (<c>expired</c> bugüne göre) başına adet ve genel toplam.</summary>
    Task<IReadOnlyList<StatusTotal<QuoteStatus>>> GetQuoteTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateOnly today, CancellationToken ct);

    /// <summary>Siparişler <c>orderDate</c> kapalı aralığında; durum başına adet ve genel toplam.</summary>
    Task<IReadOnlyList<StatusTotal<SalesOrderStatus>>> GetOrderTotalsAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Aynı aralıklardaki teklif, sipariş, fatura ve satın alma emirlerinin farklı para birimleri (sıralı, tekil).</summary>
    Task<IReadOnlyList<string>> GetCurrenciesAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Faturalar <c>invoiceDate</c> kapalı aralığında; <b>etkin</b> durum (bugüne göre) başına adet ve genel toplam (<c>grandTotal</c>).</summary>
    Task<IReadOnlyList<StatusTotal<InvoiceStatus>>> GetInvoiceTotalsAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken ct);

    /// <summary>Aynı aralıkta iptal edilmemiş faturaların tahsilat/bakiye/vade toplamları.</summary>
    Task<InvoiceSums> GetInvoiceSumsAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken ct);

    /// <summary>Satın alma emirleri <c>poDate</c> kapalı aralığında; durum başına adet ve genel toplam.</summary>
    Task<IReadOnlyList<StatusTotal<PurchaseOrderStatus>>> GetPurchaseOrderTotalsAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

public sealed record StatusTotal<TStatus>(TStatus Status, int Count, decimal Amount);

/// <summary>Fatura raporu ek toplamları: <c>paidAmount</c> (iptaller hariç Σ paid_amount), <c>outstanding</c> (açık faturaların bakiyesi), vadesi geçenlerin adedi ve bakiyesi.</summary>
public sealed record InvoiceSums(decimal PaidAmount, decimal OutstandingAmount, int OverdueCount, decimal OverdueAmount);

public interface IInvoiceReadStore
{
    /// <summary>
    /// Sıralama: <c>number</c>, <c>subject</c>, <c>grandTotal</c>, <c>balanceAmount</c>, <c>invoiceDate</c>, <c>dueDate</c> (boş her yönde sonda), <c>createdAt</c> (varsayılan azalan).
    /// <c>q</c>: numara + konu + <c>customerPoNumber</c>.
    /// </summary>
    Task<PagedResult<InvoiceSummaryDto>> ListAsync(PagedQuery paging, InvoiceFilter filter, DateOnly today, CancellationToken ct);

    Task<InvoiceDto?> GetAsync(Guid id, DateOnly today, CancellationToken ct);
}

public interface IPurchaseOrderReadStore
{
    /// <summary>Sıralama: <c>number</c>, <c>subject</c>, <c>grandTotal</c>, <c>poDate</c>, <c>dueDate</c> (boş sonda), <c>createdAt</c> (varsayılan azalan). <c>q</c>: numara + konu.</summary>
    Task<PagedResult<PurchaseOrderSummaryDto>> ListAsync(PagedQuery paging, PurchaseOrderFilter filter, CancellationToken ct);

    Task<PurchaseOrderDto?> GetAsync(Guid id, CancellationToken ct);
}

public interface IVendorReadStore
{
    /// <summary>Sıralama: <c>name</c> (varsayılan, artan), <c>category</c>, <c>createdAt</c>. <c>q</c>: ad + e-posta + telefon + kategori.</summary>
    Task<PagedResult<VendorDto>> ListAsync(PagedQuery paging, VendorFilter filter, CancellationToken ct);

    Task<VendorDto?> GetAsync(Guid id, CancellationToken ct);
}

public interface IPriceBookReadStore
{
    /// <summary>Sıralama: <c>name</c> (varsayılan, artan), <c>createdAt</c>, <c>validTo</c> (boş her yönde sonda). <c>q</c>: ad + açıklama.</summary>
    Task<PagedResult<PriceBookDto>> ListAsync(PagedQuery paging, PriceBookFilter filter, DateOnly today, CancellationToken ct);

    Task<PriceBookDto?> GetAsync(Guid id, DateOnly today, CancellationToken ct);

    /// <summary>Sıralama: <c>productName</c> (varsayılan, artan), <c>unitPrice</c>, <c>updatedAt</c>. <c>q</c>: ürün adı/kodu.</summary>
    Task<PagedResult<PriceBookEntryDto>> ListEntriesAsync(Guid priceBookId, PagedQuery paging, CancellationToken ct);

    /// <summary>Firma varsayılanı (yoksa veya liste silinmişse <c>null</c>).</summary>
    Task<AccountDefaultPriceBookDto?> GetAccountDefaultAsync(Guid accountId, DateOnly today, CancellationToken ct);
}

/// <summary>Belge kaleminin ürün bağı doğrulaması ve fiyat çözümü için ürün özeti (kiracı + yumuşak silme filtresi altında).</summary>
public sealed record ProductInfo(bool IsActive, string Currency, decimal UnitPrice = 0m, decimal? PurchasePrice = null, string? Name = null, string? Code = null);

public interface IProductLookup
{
    Task<IReadOnlyDictionary<Guid, ProductInfo>> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

/// <summary>
/// Numara sayacı (<c>commerce.document_counters</c>): belge INSERT'iyle <b>aynı transaction</b>'da tek atomik
/// <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c> ifadesiyle bir sonraki sırayı ayırır (eşzamanlı çağrılar sıraya girer,
/// transaction geri alınırsa sayaç da geri döner). Aktif transaction yoksa <see cref="InvalidOperationException"/>.
/// </summary>
public interface IDocumentNumberAllocator
{
    Task<long> NextAsync(string kind, int year, CancellationToken ct);
}

/// <summary>
/// Komutun yazma bölümünü atomik çalıştırır. UnitOfWork transaction'ı içinde bir <b>savepoint</b> açar (numara sayacı dahil tüm
/// yazımlar onun içindedir), <c>work</c> başarılıysa <c>SaveChanges</c> yapar. <c>work</c> hata dönerse veya kayıt
/// çakışırsa savepoint'e dönülür (sayaç, sipariş, kalem, outbox olayı birlikte geri alınır; numara boşluğu oluşmaz) ve hata döner.
/// Çakışma eşlemesi: <c>xmin</c> belirteci → <c>commerce.concurrent_update</c> 409; benzersiz indeks ihlalleri (numara, ürün kodu,
/// teklif başına tek sipariş) → ilgili 409 hatası.
/// </summary>
public interface ICommerceTransaction
{
    /// <summary>
    /// Transaction süresince tutulan danışma kilidi (<c>pg_advisory_xact_lock</c>): aynı anahtarı isteyen eşzamanlı komutlar sıraya girer ve kilidi alan komut
    /// <b>güncel</b> veriyi okur (sipariş → fatura dönüşümü ile sipariş iptali aynı siparişte birbirini görsün diye; <c>order-invoice:{id}</c>).
    /// </summary>
    Task LockAsync(string key, CancellationToken ct);

    Task<Result> ExecuteAsync(Func<CancellationToken, Task<Result>> work, CancellationToken ct);

    Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> work, CancellationToken ct);
}
