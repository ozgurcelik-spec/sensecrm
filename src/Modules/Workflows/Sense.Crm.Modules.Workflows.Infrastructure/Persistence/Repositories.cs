using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Domain;
using Sense.Crm.Modules.Workflows.Domain.Approvals;
using Sense.Crm.Modules.Workflows.Domain.Executions;
using Sense.Crm.Modules.Workflows.Domain.Rules;

namespace Sense.Crm.Modules.Workflows.Infrastructure.Persistence;

// Tüm sorgular ModuleDbContext'in "Tenant" ve "SoftDelete" global filtreleri altında çalışır.

public sealed class WorkflowRuleRepository(WorkflowsDbContext db) : IWorkflowRuleRepository
{
    public Task<WorkflowRule?> GetByIdAsync(Guid id, CancellationToken ct) => db.Rules.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<WorkflowRule>> ListEnabledAsync(WorkflowRuleKind kind, CancellationToken ct) =>
        await db.Rules.Where(r => r.Kind == kind && r.IsEnabled).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);

    public void Add(WorkflowRule rule) => db.Rules.Add(rule);

    public void Remove(WorkflowRule rule) => db.Rules.Remove(rule);
}

public sealed class WorkflowExecutionRepository(WorkflowsDbContext db) : IWorkflowExecutionRepository
{
    public Task<WorkflowExecution?> GetByIdAsync(Guid id, CancellationToken ct) => db.Executions.FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<WorkflowExecution?> FindAsync(Guid ruleId, Guid triggerEventId, int attempt, CancellationToken ct) =>
        db.Executions.FirstOrDefaultAsync(e => e.RuleId == ruleId && e.TriggerEventId == triggerEventId && e.Attempt == attempt, ct);

    public async Task<int> GetMaxAttemptAsync(Guid ruleId, Guid triggerEventId, CancellationToken ct) =>
        await db.Executions.Where(e => e.RuleId == ruleId && e.TriggerEventId == triggerEventId)
            .Select(e => (int?)e.Attempt)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;

    public void Add(WorkflowExecution execution) => db.Executions.Add(execution);
}

public sealed class ApprovalRepository(WorkflowsDbContext db) : IApprovalRepository
{
    public Task<Approval?> GetByIdAsync(Guid id, CancellationToken ct) => db.Approvals.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<Approval>> ListByExecutionAsync(Guid executionId, CancellationToken ct) =>
        await db.Approvals.Where(a => a.ExecutionId == executionId).OrderBy(a => a.RequestedAt).ThenBy(a => a.Id).ToListAsync(ct).ConfigureAwait(false);

    public void Add(Approval approval) => db.Approvals.Add(approval);
}

/// <summary>
/// Durum senkronu için çalışan yürütmeleri <b>kiracı filtresi dışında</b> ve dar bir projeksiyonla (kiracı + yürütme kimliği) okur —
/// kiracılar arası okuma yalnız bu sınıftadır (Identity'nin giriş/organizasyon listesi gibi bilinçli bir filtre atlamadır). Her
/// yürütme sonra kendi kiracı kapsamında işlenir. En eski başlayan önce, <paramref name="limit"/> ile sınırlı.
/// </summary>
public sealed class RunningExecutionSource(WorkflowsDbContext db) : IRunningExecutionSource
{
    public async Task<IReadOnlyList<RunningExecutionRef>> ListRunningAsync(int limit, CancellationToken ct) =>
        await db.Executions.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Running)
            .OrderBy(e => e.StartedAt)
            .Take(limit)
            .Select(e => new RunningExecutionRef(e.TenantId, e.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
