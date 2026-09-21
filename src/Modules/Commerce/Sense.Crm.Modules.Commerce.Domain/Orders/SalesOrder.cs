using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Domain.Orders;

public enum SalesOrderStatus
{
    Draft,
    Confirmed,
    Fulfilled,
    Cancelled,
}

/// <summary>
/// Siparişe özgü M9C alanları (Zoho "Sales Order" paritesi): müşterinin satın alma emri no'su, son tarih, gider vergisi, satış komisyonu (bilgi amaçlı,
/// toplamlara girmez) ve "bekliyor" notu. Tam değiştirme kuralıyla verilmeyenler temizlenir.
/// </summary>
public sealed record OrderExtras(string? CustomerPoNumber, DateOnly? DueDate, decimal? ExciseTax, decimal? SalesCommission, string? Pending)
{
    public static OrderExtras None { get; } = new(null, null, null, null, null);
}

/// <summary>Satış siparişi kalemi (<c>commerce.sales_order_lines</c>).</summary>
public sealed class SalesOrderLine : DocumentLine
{
    private SalesOrderLine()
    {
    }

    public SalesOrderLine(Guid tenantId, Guid orderId, int position, LineInput input, LineAmounts amounts) : base(tenantId, position, input, amounts) =>
        OrderId = orderId;

    public Guid OrderId { get; private set; }
}

/// <summary>
/// Satış siparişi (Zoho "Sales Order"): doğrudan veya kabul edilmiş tekliften dönüşümle açılır. Durum makinesi
/// <c>draft → confirmed → fulfilled</c>, <c>draft | confirmed → cancelled</c>; <c>fulfilled</c> ve <c>cancelled</c> uçtur.
/// Düzenleme/silme yalnız <c>draft</c>. <see cref="QuoteId"/> yalnız tekliften dönüşümde dolar ve değişmez.
/// Aktif (iptal edilmemiş) faturası olan sipariş iptal edilemez (<c>order.has_active_invoice</c>; çağıran bilgisini verir).
/// </summary>
public sealed class SalesOrder : SalesDocument
{
    private readonly List<SalesOrderLine> _lines = [];

    private SalesOrder()
    {
    }

    private SalesOrder(Guid id, Guid tenantId, string number) : base(id, tenantId, number)
    {
    }

    public SalesOrderStatus Status { get; private set; } = SalesOrderStatus.Draft;

    public DateOnly OrderDate { get; private set; }

    public Guid? QuoteId { get; private set; }

    public string? CustomerPoNumber { get; private set; }

    /// <summary>"Son tarih": sipariş tarihinden önce olamaz.</summary>
    public DateOnly? DueDate { get; private set; }

    public decimal? ExciseTax { get; private set; }

    public decimal? SalesCommission { get; private set; }

    /// <summary>"Bekliyor": serbest metin, bilgi amaçlı.</summary>
    public string? Pending { get; private set; }

    public DateTime? FulfilledAt { get; private set; }

    public DateTime? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    public IReadOnlyList<SalesOrderLine> Lines => _lines;

    /// <summary>Yeni sipariş her zaman <c>draft</c> başlar. <paramref name="quoteId"/> yalnız tekliften dönüşümde verilir.</summary>
    public static Result<SalesOrder> Create(
        Guid tenantId,
        string number,
        DocumentHeader header,
        DateOnly orderDate,
        Guid? quoteId,
        IReadOnlyList<LineInput> lines,
        OrderExtras? extras = null)
    {
        var order = new SalesOrder(Guid.CreateVersion7(), Guard.NotDefault(tenantId), number);
        var replaced = order.ReplaceLines(lines, header.Adjustment);
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        var applied = order.ApplyExtras(extras ?? OrderExtras.None, orderDate);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        order.ApplyHeader(header);
        order.OrderDate = orderDate;
        order.QuoteId = quoteId is { } quote ? Guard.NotDefault(quote) : null;
        return order;
    }

    /// <summary>Tam değiştirme (PUT, yalnız <c>draft</c>): kalem kümesi tümden değişir; numara/durum/<c>quoteId</c> değişmez.</summary>
    public Result Update(DocumentHeader header, DateOnly orderDate, IReadOnlyList<LineInput> lines, OrderExtras? extras = null)
    {
        if (Status != SalesOrderStatus.Draft)
        {
            return Error.Conflict(CommerceErrors.OrderNotEditable);
        }

        var replaced = ReplaceLines(lines, header.Adjustment);
        if (replaced.IsFailure)
        {
            return replaced;
        }

        var applied = ApplyExtras(extras ?? OrderExtras.None, orderDate);
        if (applied.IsFailure)
        {
            return applied;
        }

        ApplyHeader(header);
        OrderDate = orderDate;
        return Result.Success();
    }

    public Result EnsureEditable() => Status == SalesOrderStatus.Draft ? Result.Success() : Error.Conflict(CommerceErrors.OrderNotEditable);

    /// <summary>Siparişe özgü alanları doğrular ve yazar: <c>dueDate ≥ orderDate</c> (<c>validation.due_before_order</c>).</summary>
    private Result ApplyExtras(OrderExtras extras, DateOnly orderDate)
    {
        if (extras.DueDate is { } due && due < orderDate)
        {
            return Error.Validation(CommerceErrors.DueBeforeOrder);
        }

        CustomerPoNumber = Clean(extras.CustomerPoNumber, CommerceLimits.CustomerPoNumberMaxLength);
        DueDate = extras.DueDate;
        ExciseTax = extras.ExciseTax is { } excise ? Guard.InRange(excise, 0m, CommerceLimits.MaxAdjustment) : null;
        SalesCommission = extras.SalesCommission is { } commission ? Guard.InRange(commission, 0m, CommerceLimits.MaxAdjustment) : null;
        Pending = Clean(extras.Pending, CommerceLimits.PendingMaxLength);
        return Result.Success();
    }

    /// <summary><c>draft → confirmed</c>: en az bir kalem (<c>order.no_lines</c> 422).</summary>
    public Result Confirm()
    {
        if (Status != SalesOrderStatus.Draft)
        {
            return Invalid(SalesOrderStatus.Confirmed);
        }

        if (_lines.Count == 0)
        {
            return Error.Rule(CommerceErrors.OrderNoLines);
        }

        Status = SalesOrderStatus.Confirmed;
        return Result.Success();
    }

    /// <summary><c>confirmed → fulfilled</c> (stok/sevkiyat yok).</summary>
    public Result Fulfill(DateTime nowUtc)
    {
        if (Status != SalesOrderStatus.Confirmed)
        {
            return Invalid(SalesOrderStatus.Fulfilled);
        }

        Status = SalesOrderStatus.Fulfilled;
        FulfilledAt = nowUtc;
        return Result.Success();
    }

    /// <summary>
    /// <c>draft | confirmed → cancelled</c>. Aktif (iptal edilmemiş, silinmemiş) faturası olan sipariş iptal edilemez: <c>order.has_active_invoice</c>
    /// 409 (önce fatura iptal edilir; <paramref name="hasActiveInvoice"/> çağıran tarafından sorgulanır).
    /// </summary>
    public Result Cancel(string? reason, DateTime nowUtc, bool hasActiveInvoice = false)
    {
        if (Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Confirmed))
        {
            return Invalid(SalesOrderStatus.Cancelled);
        }

        if (hasActiveInvoice)
        {
            return Error.Conflict(CommerceErrors.OrderHasActiveInvoice);
        }

        var trimmed = reason?.Trim();
        Status = SalesOrderStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelReason = string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, CommerceLimits.ReasonMaxLength);
        return Result.Success();
    }

    /// <summary>Siparişten faturaya dönüştürülebilir mi: yalnız <c>confirmed</c> veya <c>fulfilled</c> (<c>order.not_invoiceable</c> 409).</summary>
    public Result EnsureInvoiceable() =>
        Status is SalesOrderStatus.Confirmed or SalesOrderStatus.Fulfilled ? Result.Success() : Error.Conflict(CommerceErrors.OrderNotInvoiceable);

    private Result ReplaceLines(IReadOnlyList<LineInput> inputs, decimal adjustment)
    {
        var calculated = CalculateLines(inputs, adjustment);
        if (calculated.IsFailure)
        {
            return calculated;
        }

        _lines.Clear();
        var position = 0;
        foreach (var (input, amounts) in calculated.Value)
        {
            _lines.Add(new SalesOrderLine(TenantId, Id, position++, input, amounts));
        }

        return Result.Success();
    }

    private Error Invalid(SalesOrderStatus to) =>
        Error.Conflict(CommerceErrors.OrderInvalidTransition, ("from", CommerceNames.Camel(Status)), ("to", CommerceNames.Camel(to)));
}
