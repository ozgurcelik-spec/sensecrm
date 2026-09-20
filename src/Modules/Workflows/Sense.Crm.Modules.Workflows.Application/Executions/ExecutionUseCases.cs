using Sense.Crm.Modules.Workflows.Application.Triggering;
using Sense.Crm.Modules.Workflows.Contracts;
using Sense.Crm.Modules.Workflows.Domain;
using Sense.Crm.Modules.Workflows.Domain.Approvals;
using Sense.Crm.Modules.Workflows.Domain.Executions;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Observability;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Workflows.Application.Executions;

/// <summary>Yürütme listesi: filtreler <c>status, ruleId, from, to, subjectType, subjectId</c>; en yeni önce.</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record ListExecutionsQuery(PagedQuery Paging, ExecutionFilter Filter) : IQuery<PagedResult<ExecutionDto>>;

public sealed class ListExecutionsHandler(IWorkflowReadStore store) : IQueryHandler<ListExecutionsQuery, PagedResult<ExecutionDto>>
{
    public async Task<Result<PagedResult<ExecutionDto>>> Handle(ListExecutionsQuery query, CancellationToken cancellationToken) =>
        await store.ListExecutionsAsync(
            query.Paging,
            query.Filter with { From = ToUtc(query.Filter.From), To = ToUtc(query.Filter.To) },
            cancellationToken).ConfigureAwait(false);

    /// <summary>Kind'ı belirtilmemiş sorgu zamanı UTC kabul edilir.</summary>
    private static DateTime? ToUtc(DateTime? value) => value is null
        ? null
        : value.Value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        };
}

/// <summary>Yürütme detayı: özet + motordan adımlar (ulaşılamazsa boş) + (varsa) onaylar.</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record GetExecutionQuery(Guid Id) : IQuery<ExecutionDetailDto>;

public sealed class GetExecutionHandler(IWorkflowReadStore store, IWorkflowEngine engine) : IQueryHandler<GetExecutionQuery, ExecutionDetailDto>
{
    public async Task<Result<ExecutionDetailDto>> Handle(GetExecutionQuery query, CancellationToken cancellationToken)
    {
        var found = await store.GetExecutionAsync(query.Id, cancellationToken).ConfigureAwait(false);
        if (found is not { } data)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var (execution, engineId, approvals) = data;

        var steps = new List<WorkflowStepDto>();
        if (engineId is not null)
        {
            try
            {
                if (await engine.GetAsync(engineId, cancellationToken).ConfigureAwait(false) is { } state)
                {
                    steps.AddRange(state.Steps.Select(s => new WorkflowStepDto(s.Name, s.Status, s.StartedAt, s.EndedAt, s.Output)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Motora ulaşılamıyor: detay yine de gösterilir (adımlar boş).
            }
        }

        return new ExecutionDetailDto(
            execution.Id,
            execution.RuleId,
            execution.RuleName,
            execution.Kind,
            execution.Status,
            execution.StartedAt,
            execution.EndedAt,
            execution.SubjectType,
            execution.SubjectId,
            execution.SubjectName,
            execution.Error,
            steps,
            approvals.Count > 0 ? approvals : null);
    }
}

/// <summary>Çalışan yürütmeyi sonlandırır (yalnız running; aksi <c>workflow.not_running</c> 409). Bekleyen onaylar iptal edilir.</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record TerminateExecutionCommand(Guid Id) : ICommand;

public sealed class TerminateExecutionHandler(
    IWorkflowExecutionRepository executions,
    IApprovalRepository approvals,
    IWorkflowEngine engine,
    TimeProvider clock) : ICommandHandler<TerminateExecutionCommand>
{
    public async Task<Result> Handle(TerminateExecutionCommand command, CancellationToken cancellationToken)
    {
        var execution = await executions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!execution.IsRunning)
        {
            return Error.Conflict(WorkflowsErrors.NotRunning);
        }

        if (execution.EngineWorkflowId is { } engineId)
        {
            try
            {
                await engine.TerminateAsync(engineId, "terminated by user", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error.Failure(WorkflowsErrors.EngineUnavailable);
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        execution.MarkTerminated(now);
        await ApprovalCancellation.CancelPendingAsync(approvals, execution.Id, now, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>
/// Başarısız yürütmeyi yeniden dener: aynı olay/kural için bir sonraki denemeyi (yeni yürütme) açar ve motorda başlatır (yalnız failed;
/// aksi <c>workflow.not_failed</c> 409). Kural silinmişse <c>not_found</c>. Kural parametreleri çalışma anındaki güncel kuraldan alınır
/// (yönetici kuralı düzelttiyse yeni deneme onu kullanır).
/// </summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record RetryExecutionCommand(Guid Id) : ICommand;

public sealed class RetryExecutionHandler(
    IWorkflowExecutionRepository executions,
    IWorkflowRuleRepository rules,
    WorkflowStarter starter,
    TimeProvider clock) : ICommandHandler<RetryExecutionCommand>
{
    public async Task<Result> Handle(RetryExecutionCommand command, CancellationToken cancellationToken)
    {
        var failed = await executions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (failed is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (failed.Status != ExecutionStatus.Failed)
        {
            return Error.Conflict(WorkflowsErrors.NotFailed);
        }

        var rule = await rules.GetByIdAsync(failed.RuleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var attempt = await executions.GetMaxAttemptAsync(failed.RuleId, failed.TriggerEventId, cancellationToken).ConfigureAwait(false) + 1;
        var next = WorkflowExecution.Start(
            failed.TenantId,
            rule.Id,
            rule.Name,
            rule.Kind,
            failed.TriggerEventId,
            attempt,
            failed.SubjectType,
            failed.SubjectId,
            failed.SubjectName,
            failed.InputJson,
            clock.GetUtcNow().UtcDateTime);
        executions.Add(next);

        // Motor kabul etmezse yeni yürütme de failed kaydedilir (yeniden denenebilir); istek yine de başarılıdır.
        await starter.StartAsync(next, rule, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>Bekleyen onayların iptali (yürütme sonlandı/başarısız oldu ya da karar verildi).</summary>
public static class ApprovalCancellation
{
    public static async Task<int> CancelPendingAsync(IApprovalRepository approvals, Guid executionId, DateTime nowUtc, CancellationToken ct) =>
        (await approvals.ListByExecutionAsync(executionId, ct).ConfigureAwait(false)).Count(a => a.Cancel(nowUtc));
}

/// <summary>
/// Motordaki durumu <c>workflow_executions</c>'a yansıtır (Worker periyodik çağırır; testler doğrudan çağırır): running → completed |
/// failed | terminated. Başarısız/sonlanan yürütmenin bekleyen onayları iptal edilir. Motora hiç iletilememiş yürütme
/// (<see cref="StartGrace"/> sonra) <c>workflow.engine_unavailable</c> ile failed olur; motorda bulunamayan workflow failed olur.
/// </summary>
public sealed class ExecutionStatusSynchronizer(
    IWorkflowExecutionRepository executions,
    IApprovalRepository approvals,
    IWorkflowEngine engine,
    IWorkflowsUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(2);

    public const string EngineWorkflowMissing = "engine_workflow_not_found";

    /// <returns>Yürütmenin durumu değiştiyse true.</returns>
    public async Task<bool> SyncAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await executions.GetByIdAsync(executionId, ct).ConfigureAwait(false);
        if (execution is not { IsRunning: true })
        {
            return false;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var changed = false;
        if (execution.EngineWorkflowId is not { } engineId)
        {
            changed = now - execution.StartedAt > StartGrace && execution.MarkFailed(WorkflowsErrors.EngineUnavailable, now);
        }
        else
        {
            var state = await engine.GetAsync(engineId, ct).ConfigureAwait(false);
            changed = state is null
                ? execution.MarkFailed(EngineWorkflowMissing, now)
                : state.Status switch
                {
                    ExecutionStatus.Completed => execution.MarkCompleted(now),
                    ExecutionStatus.Failed => execution.MarkFailed(state.Error ?? "failed", now),
                    ExecutionStatus.Terminated => execution.MarkTerminated(now),
                    _ => false,
                };
        }

        if (!changed)
        {
            return false;
        }

        if (execution.Status != ExecutionStatus.Completed)
        {
            await ApprovalCancellation.CancelPendingAsync(approvals, execution.Id, now, ct).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        CrmMetrics.WorkflowExecutionFinished(execution.Status.ToString().ToLowerInvariant());
        return true;
    }
}
