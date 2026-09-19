using Sense.Crm.Modules.Workflows.Domain.Rules;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Workflows.Domain.Executions;

public enum ExecutionStatus
{
    Running,
    Completed,
    Failed,
    Terminated,
}

/// <summary>Yürütmenin konusu olan Sales kaydı türü (tel: <c>lead</c>, <c>deal</c>).</summary>
public enum ExecutionSubjectType
{
    Lead,
    Deal,
}

/// <summary>
/// Bir kuralın bir olay için başlattığı workflow yürütmesi (Conductor'daki karşılığı <see cref="EngineWorkflowId"/>).
/// İdempotency anahtarı <c>(kural, tetik olayı, deneme)</c>: aynı olay aynı kural için iki kez başlamaz; "yeniden dene" aynı
/// olay için bir sonraki <see cref="Attempt"/>'i açar. Durum Conductor'dan senkronlanır (running → completed|failed|terminated,
/// bir kez sonlanınca değişmez). Kural adı/türü ve konu adı anlık görüntü olarak saklanır (kural sonradan silinse de geçmiş okunur).
/// </summary>
public sealed class WorkflowExecution : TenantAggregateRoot<Guid>, IAuditLogged
{
    private WorkflowExecution()
    {
    }

    private WorkflowExecution(
        Guid id,
        Guid tenantId,
        Guid ruleId,
        string ruleName,
        WorkflowRuleKind kind,
        Guid triggerEventId,
        int attempt,
        ExecutionSubjectType subjectType,
        Guid subjectId,
        string? subjectName,
        string inputJson,
        DateTime startedAt) : base(id, tenantId)
    {
        RuleId = ruleId;
        RuleName = ruleName;
        Kind = kind;
        TriggerEventId = triggerEventId;
        Attempt = attempt;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectName = subjectName;
        InputJson = inputJson;
        StartedAt = startedAt;
        Status = ExecutionStatus.Running;
    }

    public Guid RuleId { get; private set; }

    public string RuleName { get; private set; } = string.Empty;

    public WorkflowRuleKind Kind { get; private set; }

    public ExecutionStatus Status { get; private set; }

    public DateTime StartedAt { get; private set; }

    public DateTime? EndedAt { get; private set; }

    public ExecutionSubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public string? SubjectName { get; private set; }

    /// <summary>Hata kodu/mesajı (<c>no_assignee</c> gibi kararlı kod ya da motorun ham nedeni); yalnız failed.</summary>
    public string? Error { get; private set; }

    /// <summary>Conductor workflow kimliği; motor çağrısı başarılı olana kadar null.</summary>
    public string? EngineWorkflowId { get; private set; }

    /// <summary>Tetikleyen integration olayın kimliği (idempotency).</summary>
    public Guid TriggerEventId { get; private set; }

    /// <summary>1'den başlar; "yeniden dene" her seferinde artırır.</summary>
    public int Attempt { get; private set; }

    /// <summary>Workflow girdisinin JSON anlık görüntüsü (konu bilgileri; yeniden denemede kullanılır).</summary>
    public string InputJson { get; private set; } = "{}";

    public bool IsRunning => Status == ExecutionStatus.Running;

    public static WorkflowExecution Start(
        Guid tenantId,
        Guid ruleId,
        string ruleName,
        WorkflowRuleKind kind,
        Guid triggerEventId,
        int attempt,
        ExecutionSubjectType subjectType,
        Guid subjectId,
        string? subjectName,
        string inputJson,
        DateTime nowUtc) =>
        new(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.NotDefault(ruleId),
            Guard.MaxLength(Guard.NotEmpty(ruleName), WorkflowLimits.NameMaxLength),
            kind,
            Guard.NotDefault(triggerEventId),
            Guard.Positive(attempt),
            subjectType,
            Guard.NotDefault(subjectId),
            Trim(subjectName, WorkflowLimits.SubjectNameMaxLength),
            Guard.NotEmpty(inputJson),
            nowUtc);

    public void AttachEngine(string engineWorkflowId) =>
        EngineWorkflowId = Guard.MaxLength(Guard.NotEmpty(engineWorkflowId), WorkflowLimits.EngineIdMaxLength);

    /// <summary>Tamamlandı işaretler (yalnız çalışırken; sonlanmış yürütme değişmez). Değiştiyse true.</summary>
    public bool MarkCompleted(DateTime nowUtc) => Finish(ExecutionStatus.Completed, error: null, nowUtc);

    public bool MarkFailed(string error, DateTime nowUtc) => Finish(ExecutionStatus.Failed, Trim(Guard.NotEmpty(error), WorkflowLimits.ErrorMaxLength), nowUtc);

    public bool MarkTerminated(DateTime nowUtc) => Finish(ExecutionStatus.Terminated, error: null, nowUtc);

    private bool Finish(ExecutionStatus target, string? error, DateTime nowUtc)
    {
        if (!IsRunning)
        {
            return false;
        }

        Status = target;
        Error = error;
        EndedAt = nowUtc;
        return true;
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
