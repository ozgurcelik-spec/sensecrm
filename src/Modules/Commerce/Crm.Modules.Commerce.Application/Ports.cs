using Crm.Modules.Commerce.Domain.Orders;
using Crm.Modules.Commerce.Domain.Quotes;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Commerce.Application;

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

    /// <summary>Aynı aralıklardaki teklif ve siparişlerin farklı para birimleri (sıralı, tekil).</summary>
    Task<IReadOnlyList<string>> GetCurrenciesAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateOnly from, DateOnly to, CancellationToken ct);
}

public sealed record StatusTotal<TStatus>(TStatus Status, int Count, decimal Amount);

/// <summary>Belge kaleminin ürün bağı doğrulaması için ürün özeti (kiracı + yumuşak silme filtresi altında).</summary>
public sealed record ProductInfo(bool IsActive, string Currency);

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
    Task<Result> ExecuteAsync(Func<CancellationToken, Task<Result>> work, CancellationToken ct);

    Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> work, CancellationToken ct);
}
