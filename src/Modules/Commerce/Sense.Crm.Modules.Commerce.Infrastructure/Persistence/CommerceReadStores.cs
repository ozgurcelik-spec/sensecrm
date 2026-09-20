using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Shared.Contracts.Paging;

namespace Sense.Crm.Modules.Commerce.Infrastructure.Persistence;

/// <summary>ILIKE arama deseni: kullanıcı girdisindeki joker karakterler kaçışlanır; desen her zaman parametre olarak gider.</summary>
internal static class SearchPattern
{
    public const string Escape = "\\";

    public static string? Contains(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var escaped = trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }
}

/// <summary>Sıralama yardımcısı: beyaz liste dışındaki alanlar yok sayılır; hiçbiri geçerli değilse varsayılan sıralama.</summary>
internal static class OrderingExtensions
{
    public static IOrderedQueryable<T> Add<T, TKey>(this IOrderedQueryable<T>? ordered, IQueryable<T> query, Expression<Func<T, TKey>> key, bool descending) =>
        ordered is null
            ? (descending ? query.OrderByDescending(key) : query.OrderBy(key))
            : (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));
}

/// <summary>Toplu ad çözümü: sahip adları (<c>IMemberLookup</c>) ve firma/kişi/fırsat adları (<c>IRecordLookup</c>, tür başına tek sorgu).</summary>
internal sealed class DocumentNames(IReadOnlyDictionary<Guid, string> users, IReadOnlyDictionary<RecordRef, string> records)
{
    public static async Task<DocumentNames> ResolveAsync(
        IMemberLookup members,
        IRecordLookup recordLookup,
        IEnumerable<(Guid AccountId, Guid? ContactId, Guid? DealId, Guid OwnerUserId)> refs,
        CancellationToken ct)
    {
        var list = refs.ToList();
        var userIds = list.Select(r => r.OwnerUserId).Distinct().ToList();
        var users = userIds.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(userIds, ct).ConfigureAwait(false);

        var records = list.Select(r => new RecordRef(RecordType.Account, r.AccountId))
            .Concat(list.Where(r => r.ContactId is not null).Select(r => new RecordRef(RecordType.Contact, r.ContactId!.Value)))
            .Concat(list.Where(r => r.DealId is not null).Select(r => new RecordRef(RecordType.Deal, r.DealId!.Value)))
            .Distinct()
            .ToList();
        var names = records.Count == 0 ? new Dictionary<RecordRef, string>() : await recordLookup.GetDisplayNamesAsync(records, ct).ConfigureAwait(false);
        return new DocumentNames(users, names);
    }

    public string? Owner(Guid userId) => users.GetValueOrDefault(userId);

    public string? Account(Guid id) => records.GetValueOrDefault(new RecordRef(RecordType.Account, id));

    public string? Contact(Guid? id) => id is { } key ? records.GetValueOrDefault(new RecordRef(RecordType.Contact, key)) : null;

    public string? Deal(Guid? id) => id is { } key ? records.GetValueOrDefault(new RecordRef(RecordType.Deal, key)) : null;
}

/// <summary>Belgedeki (yumuşak bağ) fiyat listesinin adı; liste silinmişse veya bağ yoksa <c>null</c> (soft-delete filtresi altında).</summary>
internal static class PriceBookNames
{
    public static async Task<string?> ForAsync(CommerceDbContext db, Guid? priceBookId, CancellationToken ct) =>
        priceBookId is { } id
            ? await db.PriceBooks.AsNoTracking().Where(b => b.Id == id).Select(b => b.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
}

internal static class LineMappingExtensions
{
    public static DocumentLineDto ToDto(this DocumentLine l) =>
        new(l.Id, l.Position, l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxRate, l.LineSubtotal, l.LineDiscount, l.LineTax, l.LineTotal);
}

/// <summary>Ürün sorguları (kiracı + yumuşak silme filtresi altında). <c>q</c>: ad + kod + açıklama (<c>ILIKE</c>, kaçışlı parametre).</summary>
public sealed class ProductReadStore(CommerceDbContext db) : IProductReadStore
{
    public async Task<PagedResult<ProductDto>> ListAsync(PagedQuery paging, ProductFilter filter, CancellationToken ct)
    {
        var query = db.Products.AsNoTracking();
        if (filter.IsActive is { } active)
        {
            query = query.Where(p => p.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            var currency = filter.Currency.Trim().ToUpperInvariant();
            query = query.Where(p => p.Currency == currency);
        }

        if (filter.VendorId is { } vendorId)
        {
            query = query.Where(p => p.VendorId == vendorId);
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(p => EF.Functions.ILike(p.Name, q, SearchPattern.Escape)
                || (p.Code != null && EF.Functions.ILike(p.Code, q, SearchPattern.Escape))
                || (p.Description != null && EF.Functions.ILike(p.Description, q, SearchPattern.Escape)));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses).ThenBy(p => p.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        var vendorNames = await VendorNames(rows, ct).ConfigureAwait(false);
        return new PagedResult<ProductDto>(rows.Select(p => ToDto(p, vendorNames)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<ProductDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        return product is null ? null : ToDto(product, await VendorNames([product], ct).ConfigureAwait(false));
    }

    /// <summary>Tedarikçi adları sayfa başına tek sorguyla (silinmiş tedarikçi → ad yok).</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> VendorNames(IEnumerable<Product> products, CancellationToken ct)
    {
        var ids = products.Where(p => p.VendorId is not null).Select(p => p.VendorId!.Value).Distinct().ToArray();
        return ids.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Vendors.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct).ConfigureAwait(false);
    }

    private static ProductDto ToDto(Product p, IReadOnlyDictionary<Guid, string> vendorNames) =>
        new(
            p.Id,
            p.Name,
            p.Code,
            p.Description,
            p.UnitPrice,
            p.Currency,
            p.TaxRate,
            p.Unit,
            p.IsActive,
            p.VendorId,
            p.VendorId is { } vendorId ? vendorNames.GetValueOrDefault(vendorId) : null,
            p.PurchasePrice,
            p.CreatedAt,
            p.ModifiedDate);

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>name</c> artan.</summary>
    private static IOrderedQueryable<Product> Order(IQueryable<Product> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<Product>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "name":
                    ordered = ordered.Add(query, p => p.Name, clause.Descending);
                    break;

                case "code":
                    // Kodu olmayanlar her yönde sonda.
                    ordered = ordered is null ? query.OrderBy(p => p.Code == null) : ordered.ThenBy(p => p.Code == null);
                    ordered = ordered.Add(query, p => p.Code, clause.Descending);
                    break;

                case "unitprice":
                    ordered = ordered.Add(query, p => p.UnitPrice, clause.Descending);
                    break;

                case "createdat":
                    ordered = ordered.Add(query, p => p.CreatedAt, clause.Descending);
                    break;

                case "updatedat":
                    ordered = ordered is null ? query.OrderBy(p => p.ModifiedDate == null) : ordered.ThenBy(p => p.ModifiedDate == null);
                    ordered = ordered.Add(query, p => p.ModifiedDate, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderBy(p => p.Name);
    }
}

/// <summary>
/// Teklif sorguları. Etkin durum (<c>expired</c>) <see cref="QuoteStatusExpression"/> ile filtrelenir; <c>convertedOrderId</c> teklife bağlı
/// silinmemiş siparişten türetilir. Adlar sayfa başına toplu çözülür (N+1 yok).
/// </summary>
public sealed class QuoteReadStore(CommerceDbContext db, IMemberLookup members, IRecordLookup records) : IQuoteReadStore
{
    public async Task<PagedResult<QuoteSummaryDto>> ListAsync(PagedQuery paging, QuoteFilter filter, DateOnly today, CancellationToken ct)
    {
        var query = db.Quotes.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(QuoteStatusExpression.HasEffectiveStatus(status, today));
        }

        if (filter.AccountId is { } account)
        {
            query = query.Where(q => q.AccountId == account);
        }

        if (filter.ContactId is { } contact)
        {
            query = query.Where(q => q.ContactId == contact);
        }

        if (filter.DealId is { } deal)
        {
            query = query.Where(q => q.DealId == deal);
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(q => q.OwnerUserId == owner);
        }

        if (filter.ValidFrom is { } validFrom)
        {
            query = query.Where(q => q.ValidUntil != null && q.ValidUntil >= validFrom);
        }

        if (filter.ValidTo is { } validTo)
        {
            query = query.Where(q => q.ValidUntil != null && q.ValidUntil <= validTo);
        }

        if (filter.Converted is true)
        {
            query = query.Where(q => db.SalesOrders.Any(o => o.QuoteId == q.Id));
        }
        else if (filter.Converted is false)
        {
            query = query.Where(q => !db.SalesOrders.Any(o => o.QuoteId == q.Id));
        }

        if (SearchPattern.Contains(paging.Q) is { } text)
        {
            query = query.Where(q => EF.Functions.ILike(q.Number, text, SearchPattern.Escape) || EF.Functions.ILike(q.Subject, text, SearchPattern.Escape));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses)
            .ThenBy(q => q.Id)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .Select(q => new
            {
                Quote = q,
                ConvertedOrderId = db.SalesOrders.Where(o => o.QuoteId == q.Id).Select(o => (Guid?)o.Id).FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var names = await DocumentNames.ResolveAsync(members, records, rows.Select(r => (r.Quote.AccountId, r.Quote.ContactId, r.Quote.DealId, r.Quote.OwnerUserId)), ct).ConfigureAwait(false);
        var items = rows.Select(r => new QuoteSummaryDto(
            r.Quote.Id,
            r.Quote.Number,
            r.Quote.Subject,
            r.Quote.EffectiveStatus(today),
            r.Quote.AccountId,
            names.Account(r.Quote.AccountId),
            r.Quote.ContactId,
            names.Contact(r.Quote.ContactId),
            r.Quote.DealId,
            names.Deal(r.Quote.DealId),
            r.Quote.OwnerUserId,
            names.Owner(r.Quote.OwnerUserId),
            r.Quote.Currency,
            r.Quote.GrandTotal,
            r.Quote.ValidUntil,
            r.ConvertedOrderId,
            r.Quote.CreatedAt)).ToList();
        return new PagedResult<QuoteSummaryDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<QuoteDto?> GetAsync(Guid id, DateOnly today, CancellationToken ct)
    {
        var q = await db.Quotes.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (q is null)
        {
            return null;
        }

        var order = await db.SalesOrders.AsNoTracking().Where(o => o.QuoteId == id).Select(o => new { o.Id, o.Number }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var names = await DocumentNames.ResolveAsync(members, records, [(q.AccountId, q.ContactId, q.DealId, q.OwnerUserId)], ct).ConfigureAwait(false);
        var priceBookName = await PriceBookNames.ForAsync(db, q.PriceBookId, ct).ConfigureAwait(false);
        return new QuoteDto(
            q.Id,
            q.Number,
            q.Subject,
            q.EffectiveStatus(today),
            q.AccountId,
            names.Account(q.AccountId),
            q.ContactId,
            names.Contact(q.ContactId),
            q.DealId,
            names.Deal(q.DealId),
            q.OwnerUserId,
            names.Owner(q.OwnerUserId),
            q.Currency,
            q.Subtotal,
            q.DiscountTotal,
            q.TaxTotal,
            q.Adjustment,
            q.GrandTotal,
            q.ValidUntil,
            q.Carrier,
            q.BillingAddress.ToDto(),
            q.ShippingAddress.ToDto(),
            q.PriceBookId,
            priceBookName,
            q.Terms,
            q.Notes,
            q.SentAt,
            q.AcceptedAt,
            q.RejectedAt,
            q.RejectionReason,
            order?.Id,
            order?.Number,
            q.CreatedAt,
            q.ModifiedDate,
            q.Lines.OrderBy(l => l.Position).Select(l => l.ToDto()).ToList());
    }

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>createdAt</c> azalan. Boş <c>validUntil</c> her yönde sonda.</summary>
    private static IOrderedQueryable<Quote> Order(IQueryable<Quote> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<Quote>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "number":
                    ordered = ordered.Add(query, q => q.Number, clause.Descending);
                    break;

                case "subject":
                    ordered = ordered.Add(query, q => q.Subject, clause.Descending);
                    break;

                case "grandtotal":
                    ordered = ordered.Add(query, q => q.GrandTotal, clause.Descending);
                    break;

                case "validuntil":
                    ordered = ordered is null ? query.OrderBy(q => q.ValidUntil == null) : ordered.ThenBy(q => q.ValidUntil == null);
                    ordered = ordered.Add(query, q => q.ValidUntil, clause.Descending);
                    break;

                case "createdat":
                    ordered = ordered.Add(query, q => q.CreatedAt, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderByDescending(q => q.CreatedAt);
    }
}

public sealed class OrderReadStore(CommerceDbContext db, IMemberLookup members, IRecordLookup records) : IOrderReadStore
{
    public async Task<PagedResult<OrderSummaryDto>> ListAsync(PagedQuery paging, OrderFilter filter, CancellationToken ct)
    {
        var query = db.SalesOrders.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(o => o.Status == status);
        }

        if (filter.AccountId is { } account)
        {
            query = query.Where(o => o.AccountId == account);
        }

        if (filter.ContactId is { } contact)
        {
            query = query.Where(o => o.ContactId == contact);
        }

        if (filter.DealId is { } deal)
        {
            query = query.Where(o => o.DealId == deal);
        }

        if (filter.QuoteId is { } quote)
        {
            query = query.Where(o => o.QuoteId == quote);
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(o => o.OwnerUserId == owner);
        }

        if (filter.OrderFrom is { } from)
        {
            query = query.Where(o => o.OrderDate >= from);
        }

        if (filter.OrderTo is { } to)
        {
            query = query.Where(o => o.OrderDate <= to);
        }

        if (SearchPattern.Contains(paging.Q) is { } text)
        {
            query = query.Where(o => EF.Functions.ILike(o.Number, text, SearchPattern.Escape) || EF.Functions.ILike(o.Subject, text, SearchPattern.Escape));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses)
            .ThenBy(o => o.Id)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .Select(o => new
            {
                Order = o,
                QuoteNumber = db.Quotes.Where(x => x.Id == o.QuoteId).Select(x => x.Number).FirstOrDefault(),
                InvoiceId = db.Invoices.Where(i => i.OrderId == o.Id && i.Status != InvoiceStatus.Cancelled).Select(i => (Guid?)i.Id).FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var names = await DocumentNames.ResolveAsync(members, records, rows.Select(r => (r.Order.AccountId, r.Order.ContactId, r.Order.DealId, r.Order.OwnerUserId)), ct).ConfigureAwait(false);
        var items = rows.Select(r => new OrderSummaryDto(
            r.Order.Id,
            r.Order.Number,
            r.Order.Subject,
            r.Order.Status,
            r.Order.AccountId,
            names.Account(r.Order.AccountId),
            r.Order.ContactId,
            names.Contact(r.Order.ContactId),
            r.Order.DealId,
            names.Deal(r.Order.DealId),
            r.Order.QuoteId,
            r.QuoteNumber,
            r.InvoiceId,
            r.Order.OwnerUserId,
            names.Owner(r.Order.OwnerUserId),
            r.Order.Currency,
            r.Order.GrandTotal,
            r.Order.OrderDate,
            r.Order.CreatedAt)).ToList();
        return new PagedResult<OrderSummaryDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<OrderDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var o = await db.SalesOrders.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (o is null)
        {
            return null;
        }

        var quoteNumber = o.QuoteId is { } quoteId
            ? await db.Quotes.AsNoTracking().Where(x => x.Id == quoteId).Select(x => x.Number).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var names = await DocumentNames.ResolveAsync(members, records, [(o.AccountId, o.ContactId, o.DealId, o.OwnerUserId)], ct).ConfigureAwait(false);
        var invoice = await db.Invoices.AsNoTracking()
            .Where(i => i.OrderId == o.Id && i.Status != InvoiceStatus.Cancelled)
            .Select(i => new { i.Id, i.Number })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var priceBookName = await PriceBookNames.ForAsync(db, o.PriceBookId, ct).ConfigureAwait(false);
        return new OrderDto(
            o.Id,
            o.Number,
            o.Subject,
            o.Status,
            o.AccountId,
            names.Account(o.AccountId),
            o.ContactId,
            names.Contact(o.ContactId),
            o.DealId,
            names.Deal(o.DealId),
            o.QuoteId,
            quoteNumber,
            invoice?.Id,
            invoice?.Number,
            o.OwnerUserId,
            names.Owner(o.OwnerUserId),
            o.Currency,
            o.Subtotal,
            o.DiscountTotal,
            o.TaxTotal,
            o.Adjustment,
            o.GrandTotal,
            o.OrderDate,
            o.DueDate,
            o.CustomerPoNumber,
            o.ExciseTax,
            o.SalesCommission,
            o.Pending,
            o.Carrier,
            o.BillingAddress.ToDto(),
            o.ShippingAddress.ToDto(),
            o.PriceBookId,
            priceBookName,
            o.Terms,
            o.Notes,
            o.FulfilledAt,
            o.CancelledAt,
            o.CancelReason,
            o.CreatedAt,
            o.ModifiedDate,
            o.Lines.OrderBy(l => l.Position).Select(l => l.ToDto()).ToList());
    }

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>createdAt</c> azalan.</summary>
    private static IOrderedQueryable<SalesOrder> Order(IQueryable<SalesOrder> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<SalesOrder>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "number":
                    ordered = ordered.Add(query, o => o.Number, clause.Descending);
                    break;

                case "subject":
                    ordered = ordered.Add(query, o => o.Subject, clause.Descending);
                    break;

                case "grandtotal":
                    ordered = ordered.Add(query, o => o.GrandTotal, clause.Descending);
                    break;

                case "orderdate":
                    ordered = ordered.Add(query, o => o.OrderDate, clause.Descending);
                    break;

                case "createdat":
                    ordered = ordered.Add(query, o => o.CreatedAt, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderByDescending(o => o.CreatedAt);
    }
}

/// <summary>Ticaret raporu toplamları: durum başına adet/tutar veritabanında gruplanır (kiracı + yumuşak silme filtresi altında).</summary>
public sealed class CommerceReportStore(CommerceDbContext db) : ICommerceReportStore
{
    public async Task<IReadOnlyList<StatusTotal<QuoteStatus>>> GetQuoteTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateOnly today, CancellationToken ct)
    {
        var range = db.Quotes.AsNoTracking().Where(q => q.CreatedAt >= fromUtc && q.CreatedAt < toExclusiveUtc);

        // Etkin durum tek tanımdan (QuoteStatusExpression): süresi dolmuşlar ayrı, kalanlar saklanan duruma göre gruplanır.
        var expired = await range.Where(QuoteStatusExpression.IsExpired(today))
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Amount = g.Sum(q => q.GrandTotal) })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var others = await range.Where(QuoteStatusExpression.IsNotExpired(today))
            .GroupBy(q => q.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(q => q.GrandTotal) })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = others.Select(o => new StatusTotal<QuoteStatus>(o.Status, o.Count, o.Amount)).ToList();
        if (expired is not null)
        {
            result.Add(new StatusTotal<QuoteStatus>(QuoteStatus.Expired, expired.Count, expired.Amount));
        }

        return result;
    }

    public async Task<IReadOnlyList<StatusTotal<SalesOrderStatus>>> GetOrderTotalsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.SalesOrders.AsNoTracking()
            .Where(o => o.OrderDate >= from && o.OrderDate <= to)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(o => o.GrandTotal) })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new StatusTotal<SalesOrderStatus>(r.Status, r.Count, r.Amount)).ToList();
    }

    public async Task<IReadOnlyList<string>> GetCurrenciesAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var quoteCurrencies = await db.Quotes.AsNoTracking()
            .Where(q => q.CreatedAt >= fromUtc && q.CreatedAt < toExclusiveUtc)
            .Select(q => q.Currency).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var orderCurrencies = await db.SalesOrders.AsNoTracking()
            .Where(o => o.OrderDate >= from && o.OrderDate <= to)
            .Select(o => o.Currency).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var invoiceCurrencies = await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to)
            .Select(i => i.Currency).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var purchaseCurrencies = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.PoDate >= from && o.PoDate <= to)
            .Select(o => o.Currency).Distinct().ToListAsync(ct).ConfigureAwait(false);
        return quoteCurrencies.Concat(orderCurrencies).Concat(invoiceCurrencies).Concat(purchaseCurrencies).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<StatusTotal<InvoiceStatus>>> GetInvoiceTotalsAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        var range = db.Invoices.AsNoTracking().Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to);
        var result = new List<StatusTotal<InvoiceStatus>>();

        // Etkin durum tek tanımdan (InvoiceStatusExpression): her durum ayrık bir süzgeçtir; kümeler birbirini kesmez ve saklanan tüm faturaları kapsar.
        foreach (var status in Enum.GetValues<InvoiceStatus>())
        {
            var row = await range.Where(InvoiceStatusExpression.HasEffectiveStatus(status, today))
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Amount = g.Sum(i => i.GrandTotal) })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is not null)
            {
                result.Add(new StatusTotal<InvoiceStatus>(status, row.Count, row.Amount));
            }
        }

        return result;
    }

    public async Task<InvoiceSums> GetInvoiceSumsAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        var range = db.Invoices.AsNoTracking().Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to);
        var paid = await range.Where(i => i.Status != InvoiceStatus.Cancelled).SumAsync(i => (decimal?)i.PaidAmount, ct).ConfigureAwait(false) ?? 0m;
        var outstanding = await range.Where(InvoiceStatusExpression.IsOpen).SumAsync(i => (decimal?)(i.GrandTotal - i.PaidAmount), ct).ConfigureAwait(false) ?? 0m;
        var overdue = await range.Where(InvoiceStatusExpression.HasEffectiveStatus(InvoiceStatus.Overdue, today))
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Amount = g.Sum(i => i.GrandTotal - i.PaidAmount) })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return new InvoiceSums(paid, outstanding, overdue?.Count ?? 0, overdue?.Amount ?? 0m);
    }

    public async Task<IReadOnlyList<StatusTotal<PurchaseOrderStatus>>> GetPurchaseOrderTotalsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.PoDate >= from && o.PoDate <= to)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(o => o.GrandTotal) })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new StatusTotal<PurchaseOrderStatus>(r.Status, r.Count, r.Amount)).ToList();
    }
}
