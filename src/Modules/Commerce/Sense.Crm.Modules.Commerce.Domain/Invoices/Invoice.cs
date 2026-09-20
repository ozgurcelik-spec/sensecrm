using System.Linq.Expressions;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Domain.Invoices;

/// <summary>
/// Fatura durumu. <b>Saklanan</b> değerler yalnız <c>Draft/Sent/Cancelled</c>'tır; <see cref="PartiallyPaid"/>, <see cref="Paid"/>, <see cref="Overdue"/>
/// <b>türetilir</b> (<see cref="InvoiceStatusExpression"/>: tahsilat toplamı ve vade günü; zamanlanmış iş yok, M6A <c>expired</c> deseni).
/// </summary>
public enum InvoiceStatus
{
    Draft,
    Sent,
    PartiallyPaid,
    Paid,
    Overdue,
    Cancelled,
}

/// <summary>Tahsilat yöntemi (isteğe bağlı).</summary>
public enum PaymentMethod
{
    Cash,
    BankTransfer,
    Card,
    Cheque,
    Other,
}

/// <summary>
/// Etkin fatura durumunun tek tanımı (liste filtresi, rapor ve DTO eşlemesi aynı tanımı kullanır; ifade ağacı EF'e çevrilir,
/// <see cref="Compute"/> bellekte aynı kuralı uygular; eşdeğerlik testle korunur). Saklanan <c>Draft</c>/<c>Cancelled</c> aynen kalır.
/// Saklanan <c>Sent</c> için öncelik sırası: (1) <c>paidAmount ≥ grandTotal</c> → <c>paid</c> (sıfır tutarlı gönderilmiş fatura da);
/// (2) <c>dueDate &lt; bugün</c> (vade günü geçmemiş sayılır) → <c>overdue</c>; (3) <c>paidAmount &gt; 0</c> → <c>partiallyPaid</c>; (4) <c>sent</c>.
/// </summary>
public static class InvoiceStatusExpression
{
    public static InvoiceStatus Compute(InvoiceStatus stored, decimal grandTotal, decimal paidAmount, DateOnly? dueDate, DateOnly today)
    {
        if (stored is InvoiceStatus.Draft or InvoiceStatus.Cancelled)
        {
            return stored;
        }

        if (paidAmount >= grandTotal)
        {
            return InvoiceStatus.Paid;
        }

        if (dueDate is { } due && due < today)
        {
            return InvoiceStatus.Overdue;
        }

        return paidAmount > 0m ? InvoiceStatus.PartiallyPaid : InvoiceStatus.Sent;
    }

    /// <summary>Etkin durum filtresi (kiracı "bugün"ü ile). <see cref="InvoiceStatus"/> değerlerinin her biri için ayrı, ayrık ifade.</summary>
    public static Expression<Func<Invoice, bool>> HasEffectiveStatus(InvoiceStatus status, DateOnly today) => status switch
    {
        InvoiceStatus.Draft => i => i.Status == InvoiceStatus.Draft,
        InvoiceStatus.Cancelled => i => i.Status == InvoiceStatus.Cancelled,
        InvoiceStatus.Paid => i => i.Status == InvoiceStatus.Sent && i.PaidAmount >= i.GrandTotal,
        InvoiceStatus.Overdue => i => i.Status == InvoiceStatus.Sent && i.PaidAmount < i.GrandTotal && i.DueDate != null && i.DueDate < today,
        InvoiceStatus.PartiallyPaid => i => i.Status == InvoiceStatus.Sent && i.PaidAmount < i.GrandTotal && !(i.DueDate != null && i.DueDate < today) && i.PaidAmount > 0m,
        InvoiceStatus.Sent => i => i.Status == InvoiceStatus.Sent && i.PaidAmount < i.GrandTotal && !(i.DueDate != null && i.DueDate < today) && i.PaidAmount <= 0m,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>Açık (bakiyesi olan) faturalar: etkin <c>sent | partiallyPaid | overdue</c> = saklanan <c>Sent</c> ve <c>paidAmount &lt; grandTotal</c>.</summary>
    public static Expression<Func<Invoice, bool>> IsOpen { get; } = i => i.Status == InvoiceStatus.Sent && i.PaidAmount < i.GrandTotal;
}

/// <summary>Faturaya özgü alanlar: müşterinin satın alma emri no'su, gider vergisi, satış komisyonu (bilgi amaçlı, toplamlara girmez).</summary>
public sealed record InvoiceExtras(string? CustomerPoNumber, decimal? ExciseTax, decimal? SalesCommission)
{
    public static InvoiceExtras None { get; } = new(null, null, null);
}

/// <summary>Fatura kalemi (<c>commerce.invoice_lines</c>).</summary>
public sealed class InvoiceLine : DocumentLine
{
    private InvoiceLine()
    {
    }

    public InvoiceLine(Guid tenantId, Guid invoiceId, int position, LineInput input, LineAmounts amounts) : base(tenantId, position, input, amounts) =>
        InvoiceId = invoiceId;

    public Guid InvoiceId { get; private set; }
}

/// <summary>
/// Tahsilat defteri satırı (<c>commerce.invoice_payments</c>). Denetim kaydı (<c>IAuditLogged</c>) yoktur: fark faturanın <c>paidAmount</c> alanında görünür,
/// satırın kendisi <c>recordedByUserId/recordedAt</c> taşır. Güncelleme yoktur (sil + yeniden gir).
/// </summary>
public sealed class InvoicePayment : TenantEntity<Guid>
{
    private InvoicePayment()
    {
    }

    internal InvoicePayment(Guid tenantId, Guid invoiceId, decimal amount, DateOnly paidOn, PaymentMethod? method, string? reference, string? notes, Guid recordedByUserId, DateTime recordedAt)
        : base(Guid.CreateVersion7(), tenantId)
    {
        InvoiceId = invoiceId;
        Amount = amount;
        PaidOn = paidOn;
        Method = method;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : Guard.MaxLength(reference.Trim(), CommerceLimits.PaymentReferenceMaxLength);
        Notes = string.IsNullOrWhiteSpace(notes) ? null : Guard.MaxLength(notes.Trim(), CommerceLimits.PaymentNotesMaxLength);
        RecordedByUserId = recordedByUserId;
        RecordedAt = recordedAt;
    }

    public Guid InvoiceId { get; private set; }

    public decimal Amount { get; private set; }

    public DateOnly PaidOn { get; private set; }

    public PaymentMethod? Method { get; private set; }

    public string? Reference { get; private set; }

    public string? Notes { get; private set; }

    public Guid RecordedByUserId { get; private set; }

    public DateTime RecordedAt { get; private set; }
}

/// <summary>
/// Fatura (Zoho "Invoice"): firmaya bağlı, kalemli, KDV/iskontolu; doğrudan veya <b>tam sipariş dönüşümüyle</b> (<see cref="OrderId"/>) oluşur.
/// Saklanan durum makinesi <c>draft → sent</c>, <c>sent → draft</c> (tahsilat yoksa), <c>draft | sent → cancelled</c> (tahsilat yoksa); <c>cancelled</c> uçtur.
/// Düzenleme/silme yalnız <c>draft</c>. Tahsilatlar <see cref="Payments"/> defterindedir; <see cref="PaidAmount"/> = Σ tahsilat (denormalize; her yazmada yeniden hesaplanır).
/// <see cref="CommerceDocument.Version"/> (<c>xmin</c>) eşzamanlı tahsilatların aşırı ödeme yaratmasını engeller (ikinci yazan 409).
/// </summary>
public sealed class Invoice : SalesDocument
{
    private readonly List<InvoiceLine> _lines = [];
    private readonly List<InvoicePayment> _payments = [];

    private Invoice()
    {
    }

    private Invoice(Guid id, Guid tenantId, string number) : base(id, tenantId, number)
    {
    }

    /// <summary>Saklanan durum (<c>Draft/Sent/Cancelled</c>); etkin durum için <see cref="EffectiveStatus"/>.</summary>
    public InvoiceStatus Status { get; private set; } = InvoiceStatus.Draft;

    public DateOnly InvoiceDate { get; private set; }

    /// <summary>Vade ("Son tarih"): fatura tarihinden önce olamaz; vade günü geçmemiş sayılır.</summary>
    public DateOnly? DueDate { get; private set; }

    /// <summary>Yalnız siparişten dönüşümle dolar; değişmez.</summary>
    public Guid? OrderId { get; private set; }

    public string? CustomerPoNumber { get; private set; }

    public decimal? ExciseTax { get; private set; }

    public decimal? SalesCommission { get; private set; }

    /// <summary>Σ tahsilat (<c>decimal(18,2)</c>, varsayılan 0).</summary>
    public decimal PaidAmount { get; private set; }

    public DateTime? SentAt { get; private set; }

    public DateTime? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public IReadOnlyList<InvoicePayment> Payments => _payments;

    /// <summary>Kalan tutar: <c>grandTotal − paidAmount</c>.</summary>
    public decimal BalanceAmount => GrandTotal - PaidAmount;

    public InvoiceStatus EffectiveStatus(DateOnly today) => InvoiceStatusExpression.Compute(Status, GrandTotal, PaidAmount, DueDate, today);

    /// <summary>Yeni fatura her zaman <c>draft</c> başlar. <paramref name="orderId"/> yalnız siparişten dönüşümde verilir.</summary>
    public static Result<Invoice> Create(
        Guid tenantId,
        string number,
        DocumentHeader header,
        DateOnly invoiceDate,
        DateOnly? dueDate,
        Guid? orderId,
        IReadOnlyList<LineInput> lines,
        InvoiceExtras? extras = null)
    {
        var invoice = new Invoice(Guid.CreateVersion7(), Guard.NotDefault(tenantId), number);
        var replaced = invoice.ReplaceLines(lines, header.Adjustment);
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        var applied = invoice.ApplyExtras(extras ?? InvoiceExtras.None, invoiceDate, dueDate);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        invoice.ApplyHeader(header);
        invoice.InvoiceDate = invoiceDate;
        invoice.DueDate = dueDate;
        invoice.OrderId = orderId is { } order ? Guard.NotDefault(order) : null;
        return invoice;
    }

    /// <summary>Tam değiştirme (PUT, yalnız <c>draft</c>): kalem kümesi tümden değişir; numara/durum/<c>orderId</c> değişmez.</summary>
    public Result Update(DocumentHeader header, DateOnly invoiceDate, DateOnly? dueDate, IReadOnlyList<LineInput> lines, InvoiceExtras? extras = null)
    {
        if (Status != InvoiceStatus.Draft)
        {
            return Error.Conflict(CommerceErrors.InvoiceNotEditable);
        }

        var replaced = ReplaceLines(lines, header.Adjustment);
        if (replaced.IsFailure)
        {
            return replaced;
        }

        var applied = ApplyExtras(extras ?? InvoiceExtras.None, invoiceDate, dueDate);
        if (applied.IsFailure)
        {
            return applied;
        }

        ApplyHeader(header);
        InvoiceDate = invoiceDate;
        DueDate = dueDate;
        return Result.Success();
    }

    public Result EnsureEditable() => Status == InvoiceStatus.Draft ? Result.Success() : Error.Conflict(CommerceErrors.InvoiceNotEditable);

    private Result ApplyExtras(InvoiceExtras extras, DateOnly invoiceDate, DateOnly? dueDate)
    {
        if (dueDate is { } due && due < invoiceDate)
        {
            return Error.Validation(CommerceErrors.DueBeforeInvoice);
        }

        CustomerPoNumber = Clean(extras.CustomerPoNumber, CommerceLimits.CustomerPoNumberMaxLength);
        ExciseTax = extras.ExciseTax is { } excise ? Guard.InRange(excise, 0m, CommerceLimits.MaxAdjustment) : null;
        SalesCommission = extras.SalesCommission is { } commission ? Guard.InRange(commission, 0m, CommerceLimits.MaxAdjustment) : null;
        return Result.Success();
    }

    /// <summary><c>draft → sent</c>: en az bir kalem (<c>invoice.no_lines</c> 422); <c>dueDate</c> doluysa ≥ <c>invoiceDate</c>.</summary>
    public Result Send(DateTime nowUtc)
    {
        if (Status != InvoiceStatus.Draft)
        {
            return Invalid(InvoiceStatus.Sent);
        }

        if (_lines.Count == 0)
        {
            return Error.Rule(CommerceErrors.InvoiceNoLines);
        }

        if (DueDate is { } due && due < InvoiceDate)
        {
            return Error.Validation(CommerceErrors.DueBeforeInvoice);
        }

        Status = InvoiceStatus.Sent;
        SentAt = nowUtc;
        return Result.Success();
    }

    /// <summary><c>sent → draft</c> (tahsilat yoksa; varsa <c>invoice.has_payments</c> 409): <c>sentAt</c> temizlenir.</summary>
    public Result Revert()
    {
        if (Status != InvoiceStatus.Sent)
        {
            return Invalid(InvoiceStatus.Draft);
        }

        if (PaidAmount > 0m || _payments.Count > 0)
        {
            return Error.Conflict(CommerceErrors.InvoiceHasPayments);
        }

        Status = InvoiceStatus.Draft;
        SentAt = null;
        return Result.Success();
    }

    /// <summary><c>draft | sent → cancelled</c> (tahsilat varsa önce silinmelidir: <c>invoice.has_payments</c> 409); sipariş yeniden faturalanabilir hâle gelir.</summary>
    public Result Cancel(string? reason, DateTime nowUtc)
    {
        if (Status is not (InvoiceStatus.Draft or InvoiceStatus.Sent))
        {
            return Invalid(InvoiceStatus.Cancelled);
        }

        if (PaidAmount > 0m || _payments.Count > 0)
        {
            return Error.Conflict(CommerceErrors.InvoiceHasPayments);
        }

        var trimmed = reason?.Trim();
        Status = InvoiceStatus.Cancelled;
        CancelledAt = nowUtc;
        CancelReason = string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, CommerceLimits.ReasonMaxLength);
        return Result.Success();
    }

    /// <summary>
    /// Tahsilat kaydı. Yalnız etkin <c>sent | partiallyPaid | overdue</c> fatura (<c>draft</c>, <c>cancelled</c>, <c>paid</c> → <c>invoice.not_payable</c> 409);
    /// <c>paidOn</c> ≤ bugün (<c>validation.paid_in_future</c>) ve ≥ <c>invoiceDate</c> (<c>validation.paid_before_invoice</c>);
    /// <c>amount</c> &gt; bakiye → <c>invoice.payment_exceeds_balance</c> 422 (<c>balance</c> argümanı). <c>paidAmount</c> aynı çağrıda yeniden hesaplanır.
    /// </summary>
    public Result<InvoicePayment> RecordPayment(
        decimal amount,
        DateOnly paidOn,
        PaymentMethod? method,
        string? reference,
        string? notes,
        Guid recordedByUserId,
        DateOnly today,
        DateTime nowUtc)
    {
        Guard.Against(amount <= 0m || decimal.Round(amount, CommerceLimits.AmountScale) != amount, nameof(amount));
        var effective = EffectiveStatus(today);
        if (effective is not (InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid or InvoiceStatus.Overdue))
        {
            return Error.Conflict(CommerceErrors.InvoiceNotPayable);
        }

        if (paidOn > today)
        {
            return Error.Validation(CommerceErrors.PaidInFuture);
        }

        if (paidOn < InvoiceDate)
        {
            return Error.Validation(CommerceErrors.PaidBeforeInvoice);
        }

        var balance = BalanceAmount;
        if (amount > balance)
        {
            return Error.Rule(CommerceErrors.InvoicePaymentExceedsBalance, ("balance", balance));
        }

        var payment = new InvoicePayment(TenantId, Id, amount, paidOn, method, reference, notes, recordedByUserId, nowUtc);
        _payments.Add(payment);
        PaidAmount = _payments.Sum(p => p.Amount);
        return payment;
    }

    /// <summary>Yanlış girişi düzeltir: tahsilatı siler, <c>paidAmount</c> geri düşer. Yalnız iptal edilmemiş fatura; tahsilat yoksa <c>null</c> döner (çağıran 404 verir).</summary>
    public Result RemovePayment(Guid paymentId, out InvoicePayment? removed)
    {
        removed = _payments.Find(p => p.Id == paymentId);
        if (removed is null)
        {
            return Result.Success();
        }

        if (Status == InvoiceStatus.Cancelled)
        {
            return Error.Conflict(CommerceErrors.InvoiceNotPayable);
        }

        _payments.Remove(removed);
        PaidAmount = _payments.Sum(p => p.Amount);
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
            _lines.Add(new InvoiceLine(TenantId, Id, position++, input, amounts));
        }

        return Result.Success();
    }

    private Error Invalid(InvoiceStatus to) =>
        Error.Conflict(CommerceErrors.InvoiceInvalidTransition, ("from", CommerceNames.Camel(Status)), ("to", CommerceNames.Camel(to)));
}
