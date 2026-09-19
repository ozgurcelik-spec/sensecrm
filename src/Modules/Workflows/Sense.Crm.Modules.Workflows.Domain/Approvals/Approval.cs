using Sense.Crm.Modules.Workflows.Domain.Executions;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Workflows.Domain.Approvals;

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

public enum ApprovalDecision
{
    Approve,
    Reject,
}

/// <summary>
/// Onay talebi: bir yürütmenin (dealApproval) onaylayıcı roldeki bir üyeye açtığı karar isteği. Tek onay yeter (MVP): ilk karar
/// (onay veya red) sonucu belirler, diğer bekleyen talepler <see cref="ApprovalStatus.Cancelled"/> olur. Reddetmede yorum zorunludur.
/// Karar verilmiş veya iptal edilmiş talep değişmez (<c>approval.already_decided</c>). Başlık/konu adı/tutar anlık görüntüdür.
/// </summary>
public sealed class Approval : TenantAggregateRoot<Guid>, IAuditLogged
{
    private Approval()
    {
    }

    private Approval(
        Guid id,
        Guid tenantId,
        Guid executionId,
        Guid approverUserId,
        string title,
        ExecutionSubjectType subjectType,
        Guid subjectId,
        string? subjectName,
        decimal? amount,
        string? currency,
        DateTime requestedAt) : base(id, tenantId)
    {
        ExecutionId = executionId;
        ApproverUserId = approverUserId;
        Title = title;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectName = subjectName;
        Amount = amount;
        Currency = currency;
        RequestedAt = requestedAt;
        Status = ApprovalStatus.Pending;
    }

    public Guid ExecutionId { get; private set; }

    public Guid ApproverUserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public ExecutionSubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public string? SubjectName { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    public DateTime RequestedAt { get; private set; }

    public ApprovalStatus Status { get; private set; }

    public DateTime? DecidedAt { get; private set; }

    public string? Comment { get; private set; }

    public bool IsPending => Status == ApprovalStatus.Pending;

    public static Approval Create(
        Guid tenantId,
        Guid executionId,
        Guid approverUserId,
        string title,
        ExecutionSubjectType subjectType,
        Guid subjectId,
        string? subjectName,
        decimal? amount,
        string? currency,
        DateTime nowUtc) =>
        new(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.NotDefault(executionId),
            Guard.NotDefault(approverUserId),
            Guard.MaxLength(Guard.NotEmpty(title), WorkflowLimits.TitleMaxLength),
            subjectType,
            Guard.NotDefault(subjectId),
            Clean(subjectName, WorkflowLimits.SubjectNameMaxLength),
            amount,
            Clean(currency, WorkflowLimits.CurrencyMaxLength),
            nowUtc);

    /// <summary>
    /// Karar verir: bekleyen talep için onay/red; reddetmede yorum zorunlu (<c>approval.comment_required</c>, 400);
    /// zaten karar verilmiş/iptal ise <c>approval.already_decided</c> (409).
    /// </summary>
    public Result Decide(ApprovalDecision decision, string? comment, DateTime nowUtc)
    {
        if (!IsPending)
        {
            return Error.Conflict(WorkflowsErrors.ApprovalAlreadyDecided);
        }

        var text = Clean(comment, WorkflowLimits.CommentMaxLength);
        if (decision == ApprovalDecision.Reject && text is null)
        {
            return Error.Validation(WorkflowsErrors.CommentRequired);
        }

        Status = decision == ApprovalDecision.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        Comment = text;
        DecidedAt = nowUtc;
        return Result.Success();
    }

    /// <summary>Bekleyen talebi iptal eder (başka onaylayıcı karar verdi ya da yürütme sonlandı); zaten sonuçlanmışsa false.</summary>
    public bool Cancel(DateTime nowUtc)
    {
        if (!IsPending)
        {
            return false;
        }

        Status = ApprovalStatus.Cancelled;
        DecidedAt = nowUtc;
        return true;
    }

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
