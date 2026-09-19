using System.Linq.Expressions;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Domain.Quotes;

/// <summary>
/// Teklif durumu. Saklanan değerler <c>Draft/Sent/Accepted/Rejected</c>; <see cref="Expired"/> yalnız <b>türetilir</b>
/// (<see cref="QuoteStatusExpression"/>): süresi dolmuş gönderilmiş teklif.
/// </summary>
public enum QuoteStatus
{
    Draft,
    Sent,
    Accepted,
    Rejected,
    Expired,
}

/// <summary>
/// Etkin teklif durumunun tek tanımı: <c>status = sent</c> ve <c>validUntil</c> dolu ve <c>validUntil &lt; bugün (kiracı saat dilimi)</c>
/// ⇒ <c>expired</c>. Liste filtresi, rapor ve DTO eşlemesi aynı tanımı kullanır (ifade ağacı EF'e çevrilir, <see cref="Compute"/> bellekte
/// aynı kuralı uygular; ikisinin eşdeğerliği testle korunur).
/// </summary>
public static class QuoteStatusExpression
{
    public static QuoteStatus Compute(QuoteStatus status, DateOnly? validUntil, DateOnly today) =>
        status == QuoteStatus.Sent && validUntil is { } until && until < today ? QuoteStatus.Expired : status;

    /// <summary>Süresi dolmuş (etkin durum <c>expired</c>) teklifler.</summary>
    public static Expression<Func<Quote, bool>> IsExpired(DateOnly today) =>
        q => q.Status == QuoteStatus.Sent && q.ValidUntil != null && q.ValidUntil < today;

    /// <summary><see cref="IsExpired"/>'ın tersi (aynı ifadeden türetilir).</summary>
    public static Expression<Func<Quote, bool>> IsNotExpired(DateOnly today)
    {
        var expired = IsExpired(today);
        return Expression.Lambda<Func<Quote, bool>>(Expression.Not(expired.Body), expired.Parameters);
    }

    /// <summary>Etkin durum filtresi: <c>expired</c> → süresi dolmuşlar; <c>sent</c> → süresi dolmamış gönderilmişler; diğerleri saklanan durum.</summary>
    public static Expression<Func<Quote, bool>> HasEffectiveStatus(QuoteStatus status, DateOnly today) => status switch
    {
        QuoteStatus.Expired => IsExpired(today),
        QuoteStatus.Sent => And(q => q.Status == QuoteStatus.Sent, IsNotExpired(today)),
        _ => q => q.Status == status,
    };

    private static Expression<Func<Quote, bool>> And(Expression<Func<Quote, bool>> left, Expression<Func<Quote, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<Quote, bool>>(Expression.AndAlso(left.Body, rightBody), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>Teklif kalemi (<c>commerce.quote_lines</c>).</summary>
public sealed class QuoteLine : DocumentLine
{
    private QuoteLine()
    {
    }

    public QuoteLine(Guid tenantId, Guid quoteId, int position, LineInput input, LineAmounts amounts) : base(tenantId, position, input, amounts) =>
        QuoteId = quoteId;

    public Guid QuoteId { get; private set; }
}

/// <summary>
/// Teklif (Zoho "Quote"): bir firmaya (isteğe bağlı kişi ve fırsata) bağlı kalemli, KDV/iskontolu belge. Durum makinesi
/// <c>draft → sent → accepted | rejected</c> (bkz. plan): <c>accepted</c> uçtur; <c>revert</c> ile <c>sent/rejected → draft</c>;
/// düzenleme/silme yalnız <c>draft</c>. Toplamlar her yazmada baştan hesaplanır (<see cref="DocumentTotals"/>).
/// </summary>
public sealed class Quote : SalesDocument
{
    private readonly List<QuoteLine> _lines = [];

    private Quote()
    {
    }

    private Quote(Guid id, Guid tenantId, string number) : base(id, tenantId, number)
    {
    }

    public QuoteStatus Status { get; private set; } = QuoteStatus.Draft;

    public DateOnly? ValidUntil { get; private set; }

    public DateTime? SentAt { get; private set; }

    public DateTime? AcceptedAt { get; private set; }

    public DateTime? RejectedAt { get; private set; }

    public string? RejectionReason { get; private set; }

    public IReadOnlyList<QuoteLine> Lines => _lines;

    /// <summary>Yeni teklif her zaman <c>draft</c> başlar.</summary>
    public static Result<Quote> Create(Guid tenantId, string number, DocumentHeader header, DateOnly? validUntil, IReadOnlyList<LineInput> lines)
    {
        var quote = new Quote(Guid.CreateVersion7(), Guard.NotDefault(tenantId), number);
        quote.ApplyHeader(header);
        quote.ValidUntil = validUntil;
        var replaced = quote.ReplaceLines(lines);
        return replaced.IsFailure ? replaced.Error : quote;
    }

    /// <summary>Etkin durum (<c>expired</c> türetilir).</summary>
    public QuoteStatus EffectiveStatus(DateOnly today) => QuoteStatusExpression.Compute(Status, ValidUntil, today);

    /// <summary>Tam değiştirme (PUT, yalnız <c>draft</c>): kalem kümesi tümden değişir; numara/durum değişmez.</summary>
    public Result Update(DocumentHeader header, DateOnly? validUntil, IReadOnlyList<LineInput> lines)
    {
        if (Status != QuoteStatus.Draft)
        {
            return Error.Conflict(CommerceErrors.QuoteNotEditable);
        }

        var replaced = ReplaceLines(lines);
        if (replaced.IsFailure)
        {
            return replaced;
        }

        ApplyHeader(header);
        ValidUntil = validUntil;
        return Result.Success();
    }

    /// <summary>Düzenlemeye/silmeye uygun mu (yalnız <c>draft</c>).</summary>
    public Result EnsureEditable() => Status == QuoteStatus.Draft ? Result.Success() : Error.Conflict(CommerceErrors.QuoteNotEditable);

    /// <summary>
    /// <c>draft → sent</c>: en az bir kalem (<c>quote.no_lines</c> 422); <c>validUntil</c> doluysa bugünden önce olamaz
    /// (<c>validation.valid_until_past</c>; çağıran <c>errors.validUntil</c> olarak sunar).
    /// </summary>
    public Result Send(DateOnly today, DateTime nowUtc)
    {
        if (Status != QuoteStatus.Draft)
        {
            return Invalid(QuoteStatus.Sent, today);
        }

        if (_lines.Count == 0)
        {
            return Error.Rule(CommerceErrors.QuoteNoLines);
        }

        if (ValidUntil is { } until && until < today)
        {
            return Error.Validation(CommerceErrors.ValidUntilPast);
        }

        Status = QuoteStatus.Sent;
        SentAt = nowUtc;
        return Result.Success();
    }

    /// <summary><c>sent → accepted</c> (süresi dolmamış): süresi dolmuşsa <c>quote.expired</c> 409.</summary>
    public Result Accept(DateOnly today, DateTime nowUtc)
    {
        var effective = EffectiveStatus(today);
        if (effective == QuoteStatus.Expired)
        {
            return Error.Conflict(CommerceErrors.QuoteExpired);
        }

        if (effective != QuoteStatus.Sent)
        {
            return Invalid(QuoteStatus.Accepted, today);
        }

        Status = QuoteStatus.Accepted;
        AcceptedAt = nowUtc;
        return Result.Success();
    }

    /// <summary><c>sent → rejected</c> (süresi dolmuş olsa da).</summary>
    public Result Reject(string? reason, DateOnly today, DateTime nowUtc)
    {
        if (Status != QuoteStatus.Sent)
        {
            return Invalid(QuoteStatus.Rejected, today);
        }

        var trimmed = reason?.Trim();
        Status = QuoteStatus.Rejected;
        RejectedAt = nowUtc;
        RejectionReason = string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, CommerceLimits.ReasonMaxLength);
        return Result.Success();
    }

    /// <summary><c>sent | rejected → draft</c> (etkin <c>expired</c> dahil): yeniden düzenlemek için; <c>sentAt/rejectedAt/rejectionReason</c> temizlenir.</summary>
    public Result Revert(DateOnly today)
    {
        if (Status is not (QuoteStatus.Sent or QuoteStatus.Rejected))
        {
            return Invalid(QuoteStatus.Draft, today);
        }

        Status = QuoteStatus.Draft;
        SentAt = null;
        RejectedAt = null;
        RejectionReason = null;
        return Result.Success();
    }

    /// <summary><c>sent → sent</c> (etkin <c>expired</c> dahil): yeni tarih bugünden önce olamaz (<c>validation.valid_until_past</c>).</summary>
    public Result Extend(DateOnly newValidUntil, DateOnly today)
    {
        if (Status != QuoteStatus.Sent)
        {
            return Invalid(QuoteStatus.Sent, today);
        }

        if (newValidUntil < today)
        {
            return Error.Validation(CommerceErrors.ValidUntilPast);
        }

        ValidUntil = newValidUntil;
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
            _lines.Add(new QuoteLine(TenantId, Id, position++, input, amounts));
        }

        return Result.Success();
    }

    private Error Invalid(QuoteStatus to, DateOnly today) =>
        Error.Conflict(
            CommerceErrors.QuoteInvalidTransition,
            ("from", CommerceNames.Camel(EffectiveStatus(today))),
            ("to", CommerceNames.Camel(to)));
}
