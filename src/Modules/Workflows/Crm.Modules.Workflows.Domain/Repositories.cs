using Crm.Modules.Workflows.Domain.Approvals;
using Crm.Modules.Workflows.Domain.Executions;
using Crm.Modules.Workflows.Domain.Rules;

namespace Crm.Modules.Workflows.Domain;

// Depo arayüzleri komut tarafıdır (agregat yükle/ekle/sil). Tüm sorgular kiracı ve yumuşak silme filtresi altında çalışır:
// başka organizasyonun kaydı burada hiç bulunmaz (null → not_found).

public interface IWorkflowRuleRepository
{
    Task<WorkflowRule?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Türün etkin kuralları (tetikleme değerlendirmesi).</summary>
    Task<IReadOnlyList<WorkflowRule>> ListEnabledAsync(WorkflowRuleKind kind, CancellationToken ct);

    void Add(WorkflowRule rule);

    void Remove(WorkflowRule rule);
}

public interface IWorkflowExecutionRepository
{
    Task<WorkflowExecution?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>İdempotency anahtarıyla (kural, olay, deneme) yürütme; yoksa null.</summary>
    Task<WorkflowExecution?> FindAsync(Guid ruleId, Guid triggerEventId, int attempt, CancellationToken ct);

    /// <summary>Aynı kural + olay için en yüksek deneme numarası (yoksa 0).</summary>
    Task<int> GetMaxAttemptAsync(Guid ruleId, Guid triggerEventId, CancellationToken ct);

    void Add(WorkflowExecution execution);
}

public interface IApprovalRepository
{
    Task<Approval?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Bir yürütmenin tüm onayları (sonuçlanmış olanlar dahil).</summary>
    Task<IReadOnlyList<Approval>> ListByExecutionAsync(Guid executionId, CancellationToken ct);

    void Add(Approval approval);
}
