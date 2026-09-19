using Crm.Modules.Commerce.Domain.Documents;
using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Commerce.Domain.Orders;

public enum SalesOrderStatus
{
    Draft,
    Confirmed,
    Fulfilled,
    Cancelled,
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
        IReadOnlyList<LineInput> lines)
    {
        var order = new SalesOrder(Guid.CreateVersion7(), Guard.NotDefault(tenantId), number);
        order.ApplyHeader(header);
        order.OrderDate = orderDate;
        order.QuoteId = quoteId is { } quote ? Guard.NotDefault(quote) : null;
        var replaced = order.ReplaceLines(lines);
        return replaced.IsFailure ? replaced.Error : order;
    }

    /// <summary>Tam değiştirme (PUT, yalnız <c>draft</c>): kalem kümesi tümden değişir; numara/durum/<c>quoteId</c> değişmez.</summary>
    public Result Update(DocumentHeader header, DateOnly orderDate, IReadOnlyList<LineInput> lines)
    {
        if (Status != SalesOrderStatus.Draft)
        {
            return Error.Conflict(CommerceErrors.OrderNotEditable);
        }

        var replaced = ReplaceLines(lines);
        if (replaced.IsFailure)
        {
            return replaced;
        }

        ApplyHeader(header);
        OrderDate = orderDate;
        return Result.Success();
    }

    public Result EnsureEditable() => Status == SalesOrderStatus.Draft ? Result.Success() : Error.Conflict(CommerceErrors.OrderNotEditable);

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

    /// <summary><c>draft | confirmed → cancelled</c>.</summary>
    public Result Cancel(string? reason, DateTime nowUtc)
    {
        if (Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Confirmed))
        {
            return Invalid(SalesOrderStatus.Cancelled);
        }

        var trimmed = reason?.Trim();
        Status = SalesOrderStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelReason = string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, CommerceLimits.ReasonMaxLength);
        return Result.Success();
    }

    private Result ReplaceLines(IReadOnlyList<LineInput> inputs)
    {
        var calculated = CalculateLines(inputs);
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
