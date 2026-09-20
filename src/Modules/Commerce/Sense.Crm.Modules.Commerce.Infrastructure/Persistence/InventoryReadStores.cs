using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Vendors;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Shared.Contracts.Paging;

namespace Sense.Crm.Modules.Commerce.Infrastructure.Persistence;

// M9C okuma tarafı: tüm sorgular kiracı + yumuşak silme filtresi altında; `q` ILIKE + kaçışlı parametre; sıralama beyaz liste (bilinmeyen alan yok sayılır,
// her zaman Id ile kararlı); adlar sayfa başına toplu çözülür (N+1 yok).

/// <summary>Sahip ve kişi adlarının toplu çözümü (satın alma emri, tedarikçi, fiyat listesi: firma/fırsat yok).</summary>
internal static class OwnerContactNames
{
    public static async Task<(IReadOnlyDictionary<Guid, string> Users, IReadOnlyDictionary<RecordRef, string> Contacts)> ResolveAsync(
        IMemberLookup members,
        IRecordLookup records,
        IEnumerable<Guid> ownerIds,
        IEnumerable<Guid> contactIds,
        CancellationToken ct)
    {
        var owners = ownerIds.Distinct().ToList();
        var users = owners.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(owners, ct).ConfigureAwait(false);
        var contacts = contactIds.Distinct().Select(id => new RecordRef(RecordType.Contact, id)).ToList();
        var names = contacts.Count == 0 ? new Dictionary<RecordRef, string>() : await records.GetDisplayNamesAsync(contacts, ct).ConfigureAwait(false);
        return (users, names);
    }
}

/// <summary>
/// Fatura sorguları. Etkin durum <see cref="InvoiceStatusExpression"/> ile filtrelenir/hesaplanır (liste, detay ve rapor aynı tanımı kullanır);
/// <c>balanceAmount = grandTotal − paidAmount</c>; <c>orderNumber</c> siparişten okunur.
/// </summary>
public sealed class InvoiceReadStore(CommerceDbContext db, IMemberLookup members, IRecordLookup records) : IInvoiceReadStore
{
    public async Task<PagedResult<InvoiceSummaryDto>> ListAsync(PagedQuery paging, InvoiceFilter filter, DateOnly today, CancellationToken ct)
    {
        var query = db.Invoices.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(InvoiceStatusExpression.HasEffectiveStatus(status, today));
        }

        if (filter.AccountId is { } account)
        {
            query = query.Where(i => i.AccountId == account);
        }

        if (filter.ContactId is { } contact)
        {
            query = query.Where(i => i.ContactId == contact);
        }

        if (filter.DealId is { } deal)
        {
            query = query.Where(i => i.DealId == deal);
        }

        if (filter.OrderId is { } order)
        {
            query = query.Where(i => i.OrderId == order);
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(i => i.OwnerUserId == owner);
        }

        if (filter.InvoiceFrom is { } invoiceFrom)
        {
            query = query.Where(i => i.InvoiceDate >= invoiceFrom);
        }

        if (filter.InvoiceTo is { } invoiceTo)
        {
            query = query.Where(i => i.InvoiceDate <= invoiceTo);
        }

        if (filter.DueFrom is { } dueFrom)
        {
            query = query.Where(i => i.DueDate != null && i.DueDate >= dueFrom);
        }

        if (filter.DueTo is { } dueTo)
        {
            query = query.Where(i => i.DueDate != null && i.DueDate <= dueTo);
        }

        if (SearchPattern.Contains(paging.Q) is { } text)
        {
            query = query.Where(i => EF.Functions.ILike(i.Number, text, SearchPattern.Escape)
                || EF.Functions.ILike(i.Subject, text, SearchPattern.Escape)
                || (i.CustomerPoNumber != null && EF.Functions.ILike(i.CustomerPoNumber, text, SearchPattern.Escape)));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses)
            .ThenBy(i => i.Id)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .Select(i => new
            {
                Invoice = i,
                OrderNumber = db.SalesOrders.Where(o => o.Id == i.OrderId).Select(o => o.Number).FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var names = await DocumentNames.ResolveAsync(members, records, rows.Select(r => (r.Invoice.AccountId, r.Invoice.ContactId, r.Invoice.DealId, r.Invoice.OwnerUserId)), ct).ConfigureAwait(false);
        var items = rows.Select(r => new InvoiceSummaryDto(
            r.Invoice.Id,
            r.Invoice.Number,
            r.Invoice.Subject,
            r.Invoice.EffectiveStatus(today),
            r.Invoice.AccountId,
            names.Account(r.Invoice.AccountId),
            r.Invoice.ContactId,
            names.Contact(r.Invoice.ContactId),
            r.Invoice.DealId,
            names.Deal(r.Invoice.DealId),
            r.Invoice.OrderId,
            r.OrderNumber,
            r.Invoice.OwnerUserId,
            names.Owner(r.Invoice.OwnerUserId),
            r.Invoice.Currency,
            r.Invoice.GrandTotal,
            r.Invoice.PaidAmount,
            r.Invoice.BalanceAmount,
            r.Invoice.InvoiceDate,
            r.Invoice.DueDate,
            r.Invoice.CreatedAt)).ToList();
        return new PagedResult<InvoiceSummaryDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<InvoiceDto?> GetAsync(Guid id, DateOnly today, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (i is null)
        {
            return null;
        }

        var orderNumber = i.OrderId is { } orderId
            ? await db.SalesOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Number).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var names = await DocumentNames.ResolveAsync(members, records, [(i.AccountId, i.ContactId, i.DealId, i.OwnerUserId)], ct).ConfigureAwait(false);
        var priceBookName = await PriceBookNames.ForAsync(db, i.PriceBookId, ct).ConfigureAwait(false);
        return new InvoiceDto(
            i.Id,
            i.Number,
            i.Subject,
            i.EffectiveStatus(today),
            i.AccountId,
            names.Account(i.AccountId),
            i.ContactId,
            names.Contact(i.ContactId),
            i.DealId,
            names.Deal(i.DealId),
            i.OrderId,
            orderNumber,
            i.OwnerUserId,
            names.Owner(i.OwnerUserId),
            i.Currency,
            i.Subtotal,
            i.DiscountTotal,
            i.TaxTotal,
            i.Adjustment,
            i.GrandTotal,
            i.PaidAmount,
            i.BalanceAmount,
            i.InvoiceDate,
            i.DueDate,
            i.CustomerPoNumber,
            i.ExciseTax,
            i.SalesCommission,
            i.Carrier,
            i.BillingAddress.ToDto(),
            i.ShippingAddress.ToDto(),
            i.PriceBookId,
            priceBookName,
            i.Terms,
            i.Notes,
            i.SentAt,
            i.CancelledAt,
            i.CancelReason,
            i.CreatedAt,
            i.ModifiedDate,
            i.Payments.OrderBy(p => p.PaidOn).ThenBy(p => p.RecordedAt).ThenBy(p => p.Id)
                .Select(p => new InvoicePaymentDto(p.Id, p.Amount, p.PaidOn, p.Method, p.Reference, p.Notes, p.RecordedByUserId, p.RecordedAt)).ToList(),
            i.Lines.OrderBy(l => l.Position).Select(l => l.ToDto()).ToList());
    }

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>createdAt</c> azalan. Boş <c>dueDate</c> her yönde sonda.</summary>
    private static IOrderedQueryable<Invoice> Order(IQueryable<Invoice> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<Invoice>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "number":
                    ordered = ordered.Add(query, i => i.Number, clause.Descending);
                    break;

                case "subject":
                    ordered = ordered.Add(query, i => i.Subject, clause.Descending);
                    break;

                case "grandtotal":
                    ordered = ordered.Add(query, i => i.GrandTotal, clause.Descending);
                    break;

                case "balanceamount":
                    ordered = ordered.Add(query, i => i.GrandTotal - i.PaidAmount, clause.Descending);
                    break;

                case "invoicedate":
                    ordered = ordered.Add(query, i => i.InvoiceDate, clause.Descending);
                    break;

                case "duedate":
                    ordered = ordered is null ? query.OrderBy(i => i.DueDate == null) : ordered.ThenBy(i => i.DueDate == null);
                    ordered = ordered.Add(query, i => i.DueDate, clause.Descending);
                    break;

                case "createdat":
                    ordered = ordered.Add(query, i => i.CreatedAt, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderByDescending(i => i.CreatedAt);
    }
}

public sealed class PurchaseOrderReadStore(CommerceDbContext db, IMemberLookup members, IRecordLookup records) : IPurchaseOrderReadStore
{
    public async Task<PagedResult<PurchaseOrderSummaryDto>> ListAsync(PagedQuery paging, PurchaseOrderFilter filter, CancellationToken ct)
    {
        var query = db.PurchaseOrders.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(o => o.Status == status);
        }

        if (filter.VendorId is { } vendor)
        {
            query = query.Where(o => o.VendorId == vendor);
        }

        if (filter.ContactId is { } contact)
        {
            query = query.Where(o => o.ContactId == contact);
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(o => o.OwnerUserId == owner);
        }

        if (filter.PoFrom is { } from)
        {
            query = query.Where(o => o.PoDate >= from);
        }

        if (filter.PoTo is { } to)
        {
            query = query.Where(o => o.PoDate <= to);
        }

        if (SearchPattern.Contains(paging.Q) is { } text)
        {
            query = query.Where(o => EF.Functions.ILike(o.Number, text, SearchPattern.Escape) || EF.Functions.ILike(o.Subject, text, SearchPattern.Escape));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses).ThenBy(o => o.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        var vendorIds = rows.Select(o => o.VendorId).Distinct().ToArray();
        var vendorNames = await db.Vendors.AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct).ConfigureAwait(false);
        var (users, contacts) = await OwnerContactNames.ResolveAsync(members, records, rows.Select(o => o.OwnerUserId), rows.Where(o => o.ContactId is not null).Select(o => o.ContactId!.Value), ct).ConfigureAwait(false);
        var items = rows.Select(o => new PurchaseOrderSummaryDto(
            o.Id,
            o.Number,
            o.Subject,
            o.Status,
            o.VendorId,
            vendorNames.GetValueOrDefault(o.VendorId),
            o.ContactId,
            o.ContactId is { } c ? contacts.GetValueOrDefault(new RecordRef(RecordType.Contact, c)) : null,
            o.OwnerUserId,
            users.GetValueOrDefault(o.OwnerUserId),
            o.Currency,
            o.GrandTotal,
            o.PoDate,
            o.DueDate,
            o.CreatedAt)).ToList();
        return new PagedResult<PurchaseOrderSummaryDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<PurchaseOrderDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var o = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (o is null)
        {
            return null;
        }

        var vendorName = await db.Vendors.AsNoTracking().Where(v => v.Id == o.VendorId).Select(v => v.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var (users, contacts) = await OwnerContactNames.ResolveAsync(members, records, [o.OwnerUserId], o.ContactId is { } c ? [c] : [], ct).ConfigureAwait(false);
        return new PurchaseOrderDto(
            o.Id,
            o.Number,
            o.Subject,
            o.Status,
            o.VendorId,
            vendorName,
            o.ContactId,
            o.ContactId is { } contactId ? contacts.GetValueOrDefault(new RecordRef(RecordType.Contact, contactId)) : null,
            o.OwnerUserId,
            users.GetValueOrDefault(o.OwnerUserId),
            o.Currency,
            o.Subtotal,
            o.DiscountTotal,
            o.TaxTotal,
            o.Adjustment,
            o.GrandTotal,
            o.PoDate,
            o.DueDate,
            o.ExciseTax,
            o.SalesCommission,
            o.Carrier,
            o.BillingAddress.ToDto(),
            o.ShippingAddress.ToDto(),
            o.Terms,
            o.Notes,
            o.ConfirmedAt,
            o.ReceivedAt,
            o.CancelledAt,
            o.CancelReason,
            o.CreatedAt,
            o.ModifiedDate,
            o.Lines.OrderBy(l => l.Position).Select(l => l.ToDto()).ToList());
    }

    private static IOrderedQueryable<PurchaseOrder> Order(IQueryable<PurchaseOrder> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<PurchaseOrder>? ordered = null;
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

                case "podate":
                    ordered = ordered.Add(query, o => o.PoDate, clause.Descending);
                    break;

                case "duedate":
                    ordered = ordered is null ? query.OrderBy(o => o.DueDate == null) : ordered.ThenBy(o => o.DueDate == null);
                    ordered = ordered.Add(query, o => o.DueDate, clause.Descending);
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

/// <summary>Tedarikçi sorguları. <c>q</c>: ad + e-posta + telefon + kategori; <c>productCount</c> / <c>purchaseOrderCount</c> silinmemiş kayıtlardan sayılır.</summary>
public sealed class VendorReadStore(CommerceDbContext db, IMemberLookup members) : IVendorReadStore
{
    public async Task<PagedResult<VendorDto>> ListAsync(PagedQuery paging, VendorFilter filter, CancellationToken ct)
    {
        var query = db.Vendors.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim().ToUpperInvariant();
            query = query.Where(v => v.Category != null && v.Category.ToUpper() == category);
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(v => v.OwnerUserId == owner);
        }

        if (filter.EmailOptOut is { } optOut)
        {
            query = query.Where(v => v.EmailOptOut == optOut);
        }

        if (SearchPattern.Contains(paging.Q) is { } text)
        {
            query = query.Where(v => EF.Functions.ILike(v.Name, text, SearchPattern.Escape)
                || (v.Email != null && EF.Functions.ILike(v.Email, text, SearchPattern.Escape))
                || (v.Phone != null && EF.Functions.ILike(v.Phone, text, SearchPattern.Escape))
                || (v.Category != null && EF.Functions.ILike(v.Category, text, SearchPattern.Escape)));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses)
            .ThenBy(v => v.Id)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .Select(v => new
            {
                Vendor = v,
                ProductCount = db.Products.Count(p => p.VendorId == v.Id),
                PurchaseOrderCount = db.PurchaseOrders.Count(o => o.VendorId == v.Id),
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var users = await OwnerNamesAsync(rows.Select(r => r.Vendor.OwnerUserId), ct).ConfigureAwait(false);
        return new PagedResult<VendorDto>(rows.Select(r => ToDto(r.Vendor, r.ProductCount, r.PurchaseOrderCount, users)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<VendorDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Vendors.AsNoTracking()
            .Where(v => v.Id == id)
            .Select(v => new
            {
                Vendor = v,
                ProductCount = db.Products.Count(p => p.VendorId == v.Id),
                PurchaseOrderCount = db.PurchaseOrders.Count(o => o.VendorId == v.Id),
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null
            ? null
            : ToDto(row.Vendor, row.ProductCount, row.PurchaseOrderCount, await OwnerNamesAsync([row.Vendor.OwnerUserId], ct).ConfigureAwait(false));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> OwnerNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return distinct.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(distinct, ct).ConfigureAwait(false);
    }

    private static VendorDto ToDto(Vendor v, int productCount, int purchaseOrderCount, IReadOnlyDictionary<Guid, string> users) =>
        new(
            v.Id,
            v.Name,
            v.OwnerUserId,
            users.GetValueOrDefault(v.OwnerUserId),
            v.Phone,
            v.Email,
            v.Website,
            v.Category,
            v.GlAccount,
            v.Address.ToDto(),
            v.Description,
            v.EmailOptOut,
            productCount,
            purchaseOrderCount,
            v.CreatedAt,
            v.ModifiedDate);

    private static IOrderedQueryable<Vendor> Order(IQueryable<Vendor> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<Vendor>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "name":
                    ordered = ordered.Add(query, v => v.Name, clause.Descending);
                    break;

                case "category":
                    // Kategorisi olmayanlar her yönde sonda.
                    ordered = ordered is null ? query.OrderBy(v => v.Category == null) : ordered.ThenBy(v => v.Category == null);
                    ordered = ordered.Add(query, v => v.Category, clause.Descending);
                    break;

                case "createdat":
                    ordered = ordered.Add(query, v => v.CreatedAt, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderBy(v => v.Name);
    }
}

/// <summary>Fiyat listesi sorguları. Etkinlik <see cref="PriceBook.EffectiveExpression"/> ile filtrelenir (kiracı "bugün"ü).</summary>
public sealed class PriceBookReadStore(CommerceDbContext db, IMemberLookup members) : IPriceBookReadStore
{
    public async Task<PagedResult<PriceBookDto>> ListAsync(PagedQuery paging, PriceBookFilter filter, DateOnly today, CancellationToken ct)
    {
        var query = db.PriceBooks.AsNoTracking();
        if (filter.IsActive is { } active)
        {
            query = query.Where(b => b.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            var currency = filter.Currency.Trim().ToUpperInvariant();
            query = query.Where(b => b.Currency == currency);
        }

        if (filter.Effective is { } effective)
        {
            var isEffective = PriceBook.EffectiveExpression(today);
            query = effective ? query.Where(isEffective) : query.Where(NotExpression(isEffective));
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(b => b.OwnerUserId == owner);
        }

        if (SearchPattern.Contains(paging.Q) is { } text)
        {
            query = query.Where(b => EF.Functions.ILike(b.Name, text, SearchPattern.Escape)
                || (b.Description != null && EF.Functions.ILike(b.Description, text, SearchPattern.Escape)));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses)
            .ThenBy(b => b.Id)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .Select(b => new { Book = b, EntryCount = db.PriceBookEntries.Count(e => e.PriceBookId == b.Id) })
            .ToListAsync(ct).ConfigureAwait(false);
        var users = await OwnerNamesAsync(rows.Select(r => r.Book.OwnerUserId), ct).ConfigureAwait(false);
        return new PagedResult<PriceBookDto>(rows.Select(r => ToDto(r.Book, r.EntryCount, today, users)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<PriceBookDto?> GetAsync(Guid id, DateOnly today, CancellationToken ct)
    {
        var row = await db.PriceBooks.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new { Book = b, EntryCount = db.PriceBookEntries.Count(e => e.PriceBookId == b.Id) })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : ToDto(row.Book, row.EntryCount, today, await OwnerNamesAsync([row.Book.OwnerUserId], ct).ConfigureAwait(false));
    }

    public async Task<PagedResult<PriceBookEntryDto>> ListEntriesAsync(Guid priceBookId, PagedQuery paging, CancellationToken ct)
    {
        var query =
            from e in db.PriceBookEntries.AsNoTracking()
            join p in db.Products.AsNoTracking() on e.ProductId equals p.Id
            where e.PriceBookId == priceBookId
            select new EntryRow
            {
                ProductId = e.ProductId,
                ProductName = p.Name,
                ProductCode = p.Code,
                CatalogPrice = p.UnitPrice,
                UnitPrice = e.UnitPrice,
                UpdatedAt = e.ModifiedDate ?? e.CreatedAt,
            };

        if (SearchPattern.Contains(paging.Q) is { } text)
        {
            query = query.Where(r => EF.Functions.ILike(r.ProductName, text, SearchPattern.Escape) || (r.ProductCode != null && EF.Functions.ILike(r.ProductCode, text, SearchPattern.Escape)));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await OrderEntries(query, paging.SortClauses).ThenBy(r => r.ProductId).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<PriceBookEntryDto>(
            rows.Select(r => new PriceBookEntryDto(r.ProductId, r.ProductName, r.ProductCode, r.CatalogPrice, r.UnitPrice, r.UpdatedAt)).ToList(),
            paging.Page,
            paging.PageSize,
            total);
    }

    public async Task<AccountDefaultPriceBookDto?> GetAccountDefaultAsync(Guid accountId, DateOnly today, CancellationToken ct)
    {
        var row = await (
            from a in db.AccountPriceBooks.AsNoTracking()
            join b in db.PriceBooks.AsNoTracking() on a.PriceBookId equals b.Id
            where a.AccountId == accountId
            select b).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : new AccountDefaultPriceBookDto(row.Id, row.Name, row.IsEffective(today));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> OwnerNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return distinct.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(distinct, ct).ConfigureAwait(false);
    }

    private static PriceBookDto ToDto(PriceBook b, int entryCount, DateOnly today, IReadOnlyDictionary<Guid, string> users) =>
        new(
            b.Id,
            b.Name,
            b.OwnerUserId,
            users.GetValueOrDefault(b.OwnerUserId),
            b.IsActive,
            b.PricingModel,
            b.AdjustmentPercent,
            b.Currency,
            b.ValidFrom,
            b.ValidTo,
            b.IsEffective(today),
            b.Description,
            entryCount,
            b.CreatedAt,
            b.ModifiedDate);

    private static System.Linq.Expressions.Expression<Func<PriceBook, bool>> NotExpression(System.Linq.Expressions.Expression<Func<PriceBook, bool>> expression) =>
        System.Linq.Expressions.Expression.Lambda<Func<PriceBook, bool>>(System.Linq.Expressions.Expression.Not(expression.Body), expression.Parameters);

    /// <summary>Girdi + ürün birleşimi projeksiyonu (üye başlatıcılı: sıralama/süzgeç SQL'e çevrilir).</summary>
    private sealed class EntryRow
    {
        public Guid ProductId { get; init; }

        public string ProductName { get; init; } = string.Empty;

        public string? ProductCode { get; init; }

        public decimal CatalogPrice { get; init; }

        public decimal UnitPrice { get; init; }

        public DateTime UpdatedAt { get; init; }
    }

    private static IOrderedQueryable<EntryRow> OrderEntries(IQueryable<EntryRow> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<EntryRow>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "productname":
                    ordered = ordered.Add(query, r => r.ProductName, clause.Descending);
                    break;

                case "unitprice":
                    ordered = ordered.Add(query, r => r.UnitPrice, clause.Descending);
                    break;

                case "updatedat":
                    ordered = ordered.Add(query, r => r.UpdatedAt, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderBy(r => r.ProductName);
    }

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>name</c> artan. Boş <c>validTo</c> her yönde sonda.</summary>
    private static IOrderedQueryable<PriceBook> Order(IQueryable<PriceBook> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<PriceBook>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "name":
                    ordered = ordered.Add(query, b => b.Name, clause.Descending);
                    break;

                case "createdat":
                    ordered = ordered.Add(query, b => b.CreatedAt, clause.Descending);
                    break;

                case "validto":
                    ordered = ordered is null ? query.OrderBy(b => b.ValidTo == null) : ordered.ThenBy(b => b.ValidTo == null);
                    ordered = ordered.Add(query, b => b.ValidTo, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderBy(b => b.Name);
    }
}
