using System.Text.Json;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Workflows.Application;
using Crm.Modules.Workflows.Domain.Approvals;
using Crm.Modules.Workflows.Domain.Executions;
using Crm.Shared.Contracts.Paging;
using Microsoft.EntityFrameworkCore;

namespace Crm.Modules.Workflows.Infrastructure.Persistence;

/// <summary>
/// Sorgu projeksiyonları (kiracı + yumuşak silme filtresi altında). Yürütmeler en yeni başlayan önce, onaylar en yeni talep önce
/// (her zaman <c>Id</c> ile kararlı). Onaylayıcı adları sayfa başına toplu çözülür (<see cref="IMemberLookup"/>, pasif üyeler dahil).
/// </summary>
public sealed class WorkflowReadStore(WorkflowsDbContext db, IMemberLookup members) : IWorkflowReadStore
{
    public async Task<IReadOnlyList<RuleDto>> ListRulesAsync(CancellationToken ct) =>
        (await db.Rules.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => new RuleDto(r.Id, r.Name, r.Kind, r.IsEnabled, JsonDocument.Parse(r.ParamsJson).RootElement.Clone(), r.CreatedAt, r.ModifiedDate))
            .ToList();

    public async Task<RuleDto?> GetRuleAsync(Guid id, CancellationToken ct)
    {
        var r = await db.Rules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        return r is null ? null : new RuleDto(r.Id, r.Name, r.Kind, r.IsEnabled, JsonDocument.Parse(r.ParamsJson).RootElement.Clone(), r.CreatedAt, r.ModifiedDate);
    }

    public async Task<PagedResult<ExecutionDto>> ListExecutionsAsync(PagedQuery paging, ExecutionFilter filter, CancellationToken ct)
    {
        var query = db.Executions.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(e => e.Status == status);
        }

        if (filter.RuleId is { } ruleId)
        {
            query = query.Where(e => e.RuleId == ruleId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.StartedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.StartedAt <= to);
        }

        if (filter.SubjectType is { } subjectType)
        {
            query = query.Where(e => e.SubjectType == subjectType);
        }

        if (filter.SubjectId is { } subjectId)
        {
            query = query.Where(e => e.SubjectId == subjectId);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id)
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new PagedResult<ExecutionDto>(rows.Select(ToDto).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<(ExecutionDto Execution, string? EngineWorkflowId, IReadOnlyList<ExecutionApprovalDto> Approvals)?> GetExecutionAsync(Guid id, CancellationToken ct)
    {
        var execution = await db.Executions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (execution is null)
        {
            return null;
        }

        var approvals = await db.Approvals.AsNoTracking().Where(a => a.ExecutionId == id).OrderBy(a => a.RequestedAt).ThenBy(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        var names = await members.GetDisplayNamesAsync(approvals.Select(a => a.ApproverUserId).ToList(), ct).ConfigureAwait(false);
        var dtos = approvals.Select(a => new ExecutionApprovalDto(a.Id, Name(names, a.ApproverUserId), a.Status, a.DecidedAt)).ToList();
        return (ToDto(execution), execution.EngineWorkflowId, dtos);
    }

    public async Task<PagedResult<ApprovalDto>> ListApprovalsAsync(PagedQuery paging, ApprovalFilter filter, CancellationToken ct)
    {
        var query = db.Approvals.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        if (filter.ApproverUserId is { } approver)
        {
            query = query.Where(a => a.ApproverUserId == approver);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(a => a.RequestedAt).ThenByDescending(a => a.Id)
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new PagedResult<ApprovalDto>(await MapAsync(rows, ct).ConfigureAwait(false), paging.Page, paging.PageSize, total);
    }

    public async Task<ApprovalDto?> GetApprovalAsync(Guid id, CancellationToken ct)
    {
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
        return approval is null ? null : (await MapAsync([approval], ct).ConfigureAwait(false))[0];
    }

    public Task<int> CountPendingAsync(Guid approverUserId, CancellationToken ct) =>
        db.Approvals.AsNoTracking().CountAsync(a => a.ApproverUserId == approverUserId && a.Status == ApprovalStatus.Pending, ct);

    private async Task<IReadOnlyList<ApprovalDto>> MapAsync(IReadOnlyList<Domain.Approvals.Approval> rows, CancellationToken ct)
    {
        var names = await members.GetDisplayNamesAsync(rows.Select(a => a.ApproverUserId).Distinct().ToList(), ct).ConfigureAwait(false);
        return rows.Select(a => new ApprovalDto(
            a.Id,
            a.ExecutionId,
            a.Title,
            a.SubjectType,
            a.SubjectId,
            a.SubjectName,
            a.Amount,
            a.Currency,
            a.RequestedAt,
            a.Status,
            a.ApproverUserId,
            Name(names, a.ApproverUserId),
            a.DecidedAt,
            a.Comment)).ToList();
    }

    private static string Name(IReadOnlyDictionary<Guid, string> names, Guid userId) => names.TryGetValue(userId, out var name) ? name : string.Empty;

    private static ExecutionDto ToDto(WorkflowExecution e) => new(
        e.Id,
        e.RuleId,
        e.RuleName,
        e.Kind,
        e.Status,
        e.StartedAt,
        e.EndedAt,
        e.SubjectType,
        e.SubjectId,
        e.SubjectName,
        e.Error);
}
