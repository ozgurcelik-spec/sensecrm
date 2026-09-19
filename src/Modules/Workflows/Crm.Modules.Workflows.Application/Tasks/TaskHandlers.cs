using System.Text.Json.Nodes;
using Crm.Modules.Activities.Contracts;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Contracts;
using Crm.Modules.Workflows.Domain;
using Crm.Modules.Workflows.Domain.Approvals;
using Crm.Modules.Workflows.Domain.Executions;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Workflows.Application.Tasks;

/// <summary>
/// <c>crm_assign_lead_owner</c>: rolün aktif üyeleri arasından round-robin (en az açık lead, eşitlikte en eski atama) ile lead'e
/// sahip atar. Rolde aktif üye yoksa terminal <c>no_assignee</c> (lead sahibi değişmez). Girdi: <c>leadId</c>, <c>assigneeRoleId</c>.
/// Çıktı: <c>ownerUserId</c>, <c>ownerName</c>. Görevler bir Worker'da sıralı işlenir; birden çok Worker yarışı en kötü hâlde
/// aynı adayı iki kez seçtirir (adalet küçük sapabilir, veri bozulmaz).
/// </summary>
public sealed class AssignLeadOwnerTaskHandler(IRoleMemberLookup roles, ILeadOwnerService leads) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.AssignLeadOwnerTask;

    public async Task<WorkflowTaskResult> ExecuteAsync(TaskInput input, CancellationToken cancellationToken)
    {
        if (input.GetGuid("leadId") is not { } leadId || input.GetGuid("assigneeRoleId") is not { } roleId)
        {
            return WorkflowTaskResult.Failed("invalid_input");
        }

        var members = await roles.GetActiveMembersAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (members.Count == 0)
        {
            return WorkflowTaskResult.Failed(WorkflowsErrors.NoAssignee);
        }

        var ids = members.Select(m => m.UserId).ToList();
        var counts = await leads.CountOpenLeadsAsync(ids, cancellationToken).ConfigureAwait(false);
        var lastAssigned = await leads.GetLastAssignedAtAsync(ids, cancellationToken).ConfigureAwait(false);
        var candidates = members.Select(m => new AssigneeCandidate(
            m.UserId,
            m.DisplayName,
            counts.TryGetValue(m.UserId, out var count) ? count : 0,
            lastAssigned.TryGetValue(m.UserId, out var last) ? last : null));

        var chosen = RoundRobinSelector.Pick(candidates)!;
        var assigned = await leads.AssignOwnerAsync(leadId, chosen.UserId, cancellationToken).ConfigureAwait(false);
        if (assigned.IsSuccess)
        {
            return WorkflowTaskResult.Completed(new JsonObject
            {
                ["ownerUserId"] = chosen.UserId,
                ["ownerName"] = chosen.DisplayName,
            });
        }

        // Lead silinmiş/dönüşmüş: kalıcı; aday üye durumu yarışı (owner.not_member): geçici, yeniden denenir.
        return assigned.Error.Type == ErrorType.Validation
            ? WorkflowTaskResult.Retry(assigned.Error.Code)
            : WorkflowTaskResult.Failed(assigned.Error.Code);
    }
}

/// <summary>
/// <c>crm_create_followup_task</c>: atanan kişiye "Yeni potansiyel: &lt;ad&gt;" görevi (<c>dueAt = şimdi + followUpHours</c>,
/// ilişkili kayıt lead) açar. Girdi: <c>leadId</c>, <c>leadName</c>, <c>ownerUserId</c>, <c>followUpHours</c>. Çıktı: <c>activityId</c>.
/// </summary>
public sealed class CreateFollowUpTaskHandler(IActivityCreator activities, WorkflowTexts texts, TimeProvider clock) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.CreateFollowUpTask;

    public async Task<WorkflowTaskResult> ExecuteAsync(TaskInput input, CancellationToken cancellationToken)
    {
        if (input.GetGuid("leadId") is not { } leadId || input.GetGuid("ownerUserId") is not { } ownerId)
        {
            return WorkflowTaskResult.Failed("invalid_input");
        }

        var hours = input.GetInt("followUpHours") ?? WorkflowLimits.DefaultFollowUpHours;
        var subject = await texts.NewLeadTaskSubjectAsync(input.GetString("leadName") ?? string.Empty, cancellationToken).ConfigureAwait(false);
        var dueAt = clock.GetUtcNow().UtcDateTime.AddHours(hours);

        var created = await activities.CreateTaskAsync(ownerId, subject, description: null, dueAt, new RecordRef(RecordType.Lead, leadId), cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? WorkflowTaskResult.Completed(new JsonObject { ["activityId"] = created.Value })
            : Rejected(created.Error);
    }

    internal static WorkflowTaskResult Rejected(Error error) =>
        error.Type == ErrorType.Failure ? WorkflowTaskResult.Retry(error.Code) : WorkflowTaskResult.Failed(error.Code);
}

/// <summary>
/// <c>crm_create_approvals</c>: onaylayıcı roldeki her aktif üye için bir <c>approvals</c> kaydı ve "Fırsat onayı: &lt;ad&gt;" görevi
/// açar. Rolde aktif üye yoksa terminal <c>no_approver</c>. İdempotent: aynı yürütme + onaylayıcı için ikinci kayıt/görev açılmaz.
/// Girdi: <c>executionId</c>, <c>dealId</c>, <c>dealName</c>, <c>amount</c>, <c>currency</c>, <c>approverRoleId</c>.
/// Çıktı: <c>approvalIds</c>, <c>approverCount</c>.
/// </summary>
public sealed class CreateApprovalsTaskHandler(
    IRoleMemberLookup roles,
    IApprovalRepository approvals,
    IWorkflowsUnitOfWork unitOfWork,
    IActivityCreator activities,
    WorkflowTexts texts,
    ITenantContext tenant,
    TimeProvider clock) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.CreateApprovalsTask;

    public async Task<WorkflowTaskResult> ExecuteAsync(TaskInput input, CancellationToken cancellationToken)
    {
        if (input.GetGuid("executionId") is not { } executionId
            || input.GetGuid("dealId") is not { } dealId
            || input.GetGuid("approverRoleId") is not { } roleId)
        {
            return WorkflowTaskResult.Failed("invalid_input");
        }

        var members = await roles.GetActiveMembersAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (members.Count == 0)
        {
            return WorkflowTaskResult.Failed(WorkflowsErrors.NoApprover);
        }

        var dealName = input.GetString("dealName") ?? string.Empty;
        var title = await texts.DealApprovalTitleAsync(dealName, cancellationToken).ConfigureAwait(false);
        var existing = (await approvals.ListByExecutionAsync(executionId, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.ApproverUserId);
        var now = clock.GetUtcNow().UtcDateTime;

        var fresh = new List<Approval>();
        foreach (var member in members.Where(m => !existing.ContainsKey(m.UserId)))
        {
            var approval = Approval.Create(
                tenant.TenantId,
                executionId,
                member.UserId,
                title,
                ExecutionSubjectType.Deal,
                dealId,
                dealName,
                input.GetDecimal("amount"),
                input.GetString("currency"),
                now);
            approvals.Add(approval);
            fresh.Add(approval);
        }

        if (fresh.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var approval in fresh)
        {
            var task = await activities.CreateTaskAsync(approval.ApproverUserId, title, description: null, dueAt: null, new RecordRef(RecordType.Deal, dealId), cancellationToken).ConfigureAwait(false);
            if (task.IsFailure && task.Error.Type == ErrorType.NotFound)
            {
                return WorkflowTaskResult.Failed(task.Error.Code);
            }
        }

        var all = existing.Values.Concat(fresh).Select(a => a.Id).ToList();
        return WorkflowTaskResult.Completed(new JsonObject
        {
            ["approvalIds"] = new JsonArray(all.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
            ["approverCount"] = all.Count,
        });
    }
}

/// <summary>
/// <c>crm_record_decision</c>: karar sonucunu fırsata "Onay: onaylandı/reddedildi (yorum)" notu olarak ekler (Activities
/// <c>note</c>, kararı veren kullanıcıya atanır). Girdi: <c>dealId</c>, <c>decision</c> (<c>approved|rejected</c>), <c>comment?</c>,
/// <c>approverUserId</c>. Fırsatın aşaması değişmez (bilgilendirici onay). Çıktı: <c>noteId</c>.
/// </summary>
public sealed class RecordDecisionTaskHandler(IActivityCreator activities, WorkflowTexts texts) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.RecordDecisionTask;

    public async Task<WorkflowTaskResult> ExecuteAsync(TaskInput input, CancellationToken cancellationToken)
    {
        if (input.GetGuid("dealId") is not { } dealId || input.GetGuid("approverUserId") is not { } approverId)
        {
            return WorkflowTaskResult.Failed("invalid_input");
        }

        var comment = input.GetString("comment");
        var approved = string.Equals(input.GetString("decision"), "approved", StringComparison.Ordinal);
        var subject = await texts.DecisionNoteAsync(approved, comment, cancellationToken).ConfigureAwait(false);

        var created = await activities.CreateNoteAsync(approverId, subject, comment, new RecordRef(RecordType.Deal, dealId), cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? WorkflowTaskResult.Completed(new JsonObject { ["noteId"] = created.Value })
            : CreateFollowUpTaskHandler.Rejected(created.Error);
    }
}

/// <summary>
/// <c>crm_cancel_pending_approvals</c>: yürütmenin kararı beklenen diğer onaylarını <c>cancelled</c> yapar (güvenlik ağı:
/// karar uç noktası bunları aynı transaction'da zaten iptal eder). Girdi: <c>executionId</c>. Çıktı: <c>cancelled</c>.
/// </summary>
public sealed class CancelPendingApprovalsTaskHandler(IApprovalRepository approvals, IWorkflowsUnitOfWork unitOfWork, TimeProvider clock) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.CancelPendingApprovalsTask;

    public async Task<WorkflowTaskResult> ExecuteAsync(TaskInput input, CancellationToken cancellationToken)
    {
        if (input.GetGuid("executionId") is not { } executionId)
        {
            return WorkflowTaskResult.Failed("invalid_input");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var cancelled = (await approvals.ListByExecutionAsync(executionId, cancellationToken).ConfigureAwait(false)).Count(a => a.Cancel(now));
        if (cancelled > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return WorkflowTaskResult.Completed(new JsonObject { ["cancelled"] = cancelled });
    }
}
