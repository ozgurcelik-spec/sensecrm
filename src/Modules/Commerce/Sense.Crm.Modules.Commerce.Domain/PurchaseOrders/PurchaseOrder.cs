using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;

public enum PurchaseOrderStatus
{
    Draft,
    Confirmed,
    Received,
    Cancelled,
}

/// <summary>Satın alma emrine özgü bilgi amaçlı alanlar (Zoho paritesi; toplamlara girmez): son tarih, gider vergisi, satış komisyonu.</summary>
public sealed record PurchaseOrderExtras(DateOnly? DueDate, decimal? ExciseTax, decimal? SalesCommission)
{
    public static PurchaseOrderExtras None { get; } = new(null, null, null);
}

/// <summary>Satın alma emri kalemi (<c>commerce.purchase_order_lines</c>).</summary>
public sealed class PurchaseOrderLine : DocumentLine
{
    private PurchaseOrderLine()
    {
    }

    public PurchaseOrderLine(Guid tenantId, Guid purchaseOrderId, int position, LineInput input, LineAmounts amounts) : base(tenantId, position, input, amounts) =>
        PurchaseOrderId = purchaseOrderId;

    public Guid PurchaseOrderId { get; private set; }
}

/// <summary>
/// Satın alma emri (Zoho "Purchase Order"): tedarikçiye kalemli belge (fiyatlar tedarikçi tarafı). Satış belgeleriyle hiçbir ilişkisi yoktur (D11), stok yoktur.
/// Durum makinesi <c>draft → confirmed → received</c>, <c>draft | confirmed → cancelled</c>; <c>received</c> ve <c>cancelled</c> uçtur; düzenleme/silme yalnız
/// <c>draft</c>. Numara (<c>PO-2026-0001</c>) sunucudan atanır ve değişmez.
/// </summary>
public sealed class PurchaseOrder : CommerceDocument
{
    private readonly List<PurchaseOrderLine> _lines = [];

    private PurchaseOrder()
    {
    }

    private PurchaseOrder(Guid id, Guid tenantId, string number) : base(id, tenantId, number)
    {
    }

    public PurchaseOrderStatus Status { get; private set; } = PurchaseOrderStatus.Draft;

    public Guid VendorId { get; private set; }

    public DateOnly PoDate { get; private set; }

    public DateOnly? DueDate { get; private set; }

    public decimal? ExciseTax { get; private set; }

    public decimal? SalesCommission { get; private set; }

    public DateTime? ConfirmedAt { get; private set; }

    public DateTime? ReceivedAt { get; private set; }

    public DateTime? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    public static Result<PurchaseOrder> Create(
        Guid tenantId,
        string number,
        PurchaseOrderHeader header,
        DateOnly poDate,
        IReadOnlyList<LineInput> lines,
        PurchaseOrderExtras? extras = null)
    {
        var order = new PurchaseOrder(Guid.CreateVersion7(), Guard.NotDefault(tenantId), number);
        var replaced = order.ReplaceLines(lines, header.Adjustment);
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        var applied = order.ApplyExtras(extras ?? PurchaseOrderExtras.None, poDate);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        order.ApplyHeader(header);
        order.PoDate = poDate;
        return order;
    }

    /// <summary>Tam değiştirme (PUT, yalnız <c>draft</c>): kalem kümesi tümden değişir; numara/durum değişmez.</summary>
    public Result Update(PurchaseOrderHeader header, DateOnly poDate, IReadOnlyList<LineInput> lines, PurchaseOrderExtras? extras = null)
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            return Error.Conflict(CommerceErrors.PurchaseOrderNotEditable);
        }

        var replaced = ReplaceLines(lines, header.Adjustment);
        if (replaced.IsFailure)
        {
            return replaced;
        }

        var applied = ApplyExtras(extras ?? PurchaseOrderExtras.None, poDate);
        if (applied.IsFailure)
        {
            return applied;
        }

        ApplyHeader(header);
        PoDate = poDate;
        return Result.Success();
    }

    public Result EnsureEditable() => Status == PurchaseOrderStatus.Draft ? Result.Success() : Error.Conflict(CommerceErrors.PurchaseOrderNotEditable);

    private void ApplyHeader(PurchaseOrderHeader header)
    {
        ApplyCommon(header.Subject, header.ContactId, header.OwnerUserId, header.Currency, header.Terms, header.Notes, header.Carrier, header.BillingAddress, header.ShippingAddress);
        VendorId = Guard.NotDefault(header.VendorId);
    }

    private Result ApplyExtras(PurchaseOrderExtras extras, DateOnly poDate)
    {
        if (extras.DueDate is { } due && due < poDate)
        {
            return Error.Validation(CommerceErrors.DueBeforePo);
        }

        DueDate = extras.DueDate;
        ExciseTax = extras.ExciseTax is { } excise ? Guard.InRange(excise, 0m, CommerceLimits.MaxAdjustment) : null;
        SalesCommission = extras.SalesCommission is { } commission ? Guard.InRange(commission, 0m, CommerceLimits.MaxAdjustment) : null;
        return Result.Success();
    }

    /// <summary><c>draft → confirmed</c>: en az bir kalem (<c>purchase_order.no_lines</c> 422).</summary>
    public Result Confirm(DateTime nowUtc)
    {
        if (Status != PurchaseOrderStatus.Draft)
        {
            return Invalid(PurchaseOrderStatus.Confirmed);
        }

        if (_lines.Count == 0)
        {
            return Error.Rule(CommerceErrors.PurchaseOrderNoLines);
        }

        Status = PurchaseOrderStatus.Confirmed;
        ConfirmedAt = nowUtc;
        return Result.Success();
    }

    /// <summary><c>confirmed → received</c> (stok yok).</summary>
    public Result Receive(DateTime nowUtc)
    {
        if (Status != PurchaseOrderStatus.Confirmed)
        {
            return Invalid(PurchaseOrderStatus.Received);
        }

        Status = PurchaseOrderStatus.Received;
        ReceivedAt = nowUtc;
        return Result.Success();
    }

    /// <summary><c>draft | confirmed → cancelled</c>.</summary>
    public Result Cancel(string? reason, DateTime nowUtc)
    {
        if (Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Confirmed))
        {
            return Invalid(PurchaseOrderStatus.Cancelled);
        }

        var trimmed = reason?.Trim();
        Status = PurchaseOrderStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelReason = string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, CommerceLimits.ReasonMaxLength);
        return Result.Success();
    }

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
            _lines.Add(new PurchaseOrderLine(TenantId, Id, position++, input, amounts));
        }

        return Result.Success();
    }

    private Error Invalid(PurchaseOrderStatus to) =>
        Error.Conflict(CommerceErrors.PurchaseOrderInvalidTransition, ("from", CommerceNames.Camel(Status)), ("to", CommerceNames.Camel(to)));
}
