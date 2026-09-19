using System.Text.Json.Nodes;
using Sense.Crm.Modules.Activities.Contracts;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Workflows.Domain;
using Sense.Crm.Modules.Workflows.Domain.Approvals;
using Sense.Crm.Modules.Workflows.Domain.Executions;
using Sense.Crm.Modules.Workflows.Domain.Rules;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Workflows.Application.Tasks;

// Güven modeli (H1): Conductor'dan gelen görev girdisi (inputData) GÜVENİLMEZDİR. Görev çalışmadan önce WorkflowTaskRunner
// (tenantId, executionId, motor workflow kimliği) üçlüsünü workflow_executions ile doğrular ve işleyiciye TrustedTask verir:
// rol kimlikleri/parametreler kayıtlı kuraldan, konu (lead/fırsat) yürütmenin saklı anlık görüntüsünden, onay kararı ise
// approvals satırlarından okunur. HUMAN görev çıktısı ve task input'undaki rol/karar/onaylayıcı alanları hiçbir yerde kullanılmaz.

/// <summary>
/// <c>crm_assign_lead_owner</c>: kuraldaki atama rolünün aktif üyeleri arasından round-robin (en az açık lead, eşitlikte en eski atama)
/// ile lead'e sahip atar. Rolde aktif üye yoksa terminal <c>no_assignee</c> (lead sahibi değişmez). Lead yürütmenin konusudur, rol
/// kuraldandır. Çıktı: <c>ownerUserId</c>, <c>ownerName</c>. Görevler bir Worker'da sıralı işlenir; birden çok Worker yarışı en kötü
/// hâlde aynı adayı iki kez seçtirir (adalet küçük sapabilir, veri bozulmaz).
/// </summary>
public sealed class AssignLeadOwnerTaskHandler(IRoleMemberLookup roles, ILeadOwnerService leads) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.AssignLeadOwnerTask;

    public WorkflowRuleKind ExecutionKind => WorkflowRuleKind.LeadAssignment;

    public async Task<WorkflowTaskResult> ExecuteAsync(TrustedTask task, CancellationToken cancellationToken)
    {
        if (task.Rule is not { Kind: WorkflowRuleKind.LeadAssignment } rule)
        {
            return WorkflowTaskResult.Failed(WorkflowsErrors.RuleNotFound);
        }

        var leadId = task.SubjectId;
        var members = await roles.GetActiveMembersAsync(rule.LeadAssignment.AssigneeRoleId, cancellationToken).ConfigureAwait(false);
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
/// ilişkili kayıt lead) açar. Lead adı yürütmeden, <c>followUpHours</c> ve atama rolü kuraldan gelir. Atanan kullanıcı önceki görevin
/// çıktısıdır (<c>ownerUserId</c>): güvenilmez olduğundan kuralın rolünün <b>aktif üyesi</b> olduğu yeniden doğrulanır. Çıktı: <c>activityId</c>.
/// </summary>
public sealed class CreateFollowUpTaskHandler(IRoleMemberLookup roles, IActivityCreator activities, WorkflowTexts texts, TimeProvider clock) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.CreateFollowUpTask;

    public WorkflowRuleKind ExecutionKind => WorkflowRuleKind.LeadAssignment;

    public async Task<WorkflowTaskResult> ExecuteAsync(TrustedTask task, CancellationToken cancellationToken)
    {
        if (task.Rule is not { Kind: WorkflowRuleKind.LeadAssignment } rule)
        {
            return WorkflowTaskResult.Failed(WorkflowsErrors.RuleNotFound);
        }

        if (task.RawInput.GetGuid("ownerUserId") is not { } ownerId)
        {
            return WorkflowTaskResult.Failed("invalid_input");
        }

        var parameters = rule.LeadAssignment;
        var members = await roles.GetActiveMembersAsync(parameters.AssigneeRoleId, cancellationToken).ConfigureAwait(false);
        if (members.All(m => m.UserId != ownerId))
        {
            return WorkflowTaskResult.Failed("invalid_input"); // atanan, kuralın rolünün aktif üyesi değil: sahte/bayat çıktı
        }

        var subject = await texts.NewLeadTaskSubjectAsync(task.SubjectName, cancellationToken).ConfigureAwait(false);
        var dueAt = clock.GetUtcNow().UtcDateTime.AddHours(parameters.FollowUpHours);

        var created = await activities.CreateTaskAsync(ownerId, subject, description: null, dueAt, new RecordRef(RecordType.Lead, task.SubjectId), cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? WorkflowTaskResult.Completed(new JsonObject { ["activityId"] = created.Value })
            : Rejected(created.Error);
    }

    internal static WorkflowTaskResult Rejected(Error error) =>
        error.Type == ErrorType.Failure ? WorkflowTaskResult.Retry(error.Code) : WorkflowTaskResult.Failed(error.Code);
}

/// <summary>
/// <c>crm_create_approvals</c>: onaylayıcı roldeki her aktif üye için bir <c>approvals</c> kaydı ve "Fırsat onayı: &lt;ad&gt;" görevi
/// açar. Rol kuraldandır; fırsatın adı/tutarı yürütmenin anlık görüntüsündendir. <b>Fırsatın sahibi onaylayıcılardan çıkarılır</b> (L3:
/// kimse kendi fırsatını onaylayamaz); geriye kimse kalmazsa (rolde kimse yok ya da tek üye sahibin kendisi) terminal <c>no_approver</c>.
/// İdempotent: aynı yürütme + onaylayıcı için ikinci kayıt/görev açılmaz. Çıktı: <c>approvalIds</c>, <c>approverCount</c>.
/// </summary>
public sealed class CreateApprovalsTaskHandler(
    IRoleMemberLookup roles,
    IRecordLookup records,
    IApprovalRepository approvals,
    IWorkflowsUnitOfWork unitOfWork,
    IActivityCreator activities,
    WorkflowTexts texts,
    ITenantContext tenant,
    TimeProvider clock) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.CreateApprovalsTask;

    public WorkflowRuleKind ExecutionKind => WorkflowRuleKind.DealApproval;

    public async Task<WorkflowTaskResult> ExecuteAsync(TrustedTask task, CancellationToken cancellationToken)
    {
        if (task.Rule is not { Kind: WorkflowRuleKind.DealApproval } rule)
        {
            return WorkflowTaskResult.Failed(WorkflowsErrors.RuleNotFound);
        }

        var dealId = task.SubjectId;
        var dealOwner = await records.GetOwnerUserIdAsync(RecordType.Deal, dealId, cancellationToken).ConfigureAwait(false);
        var members = (await roles.GetActiveMembersAsync(rule.DealApproval.ApproverRoleId, cancellationToken).ConfigureAwait(false))
            .Where(m => m.UserId != dealOwner)
            .ToList();
        if (members.Count == 0)
        {
            return WorkflowTaskResult.Failed(WorkflowsErrors.NoApprover);
        }

        var dealName = task.SubjectName;
        var title = await texts.DealApprovalTitleAsync(dealName, cancellationToken).ConfigureAwait(false);
        var existing = (await approvals.ListByExecutionAsync(task.ExecutionId, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.ApproverUserId);
        var now = clock.GetUtcNow().UtcDateTime;

        var fresh = new List<Approval>();
        foreach (var member in members.Where(m => !existing.ContainsKey(m.UserId)))
        {
            var approval = Approval.Create(
                tenant.TenantId,
                task.ExecutionId,
                member.UserId,
                title,
                ExecutionSubjectType.Deal,
                dealId,
                dealName,
                task.Amount,
                task.Currency,
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
            var created = await activities.CreateTaskAsync(approval.ApproverUserId, title, description: null, dueAt: null, new RecordRef(RecordType.Deal, dealId), cancellationToken).ConfigureAwait(false);
            if (created.IsFailure && created.Error.Type == ErrorType.NotFound)
            {
                return WorkflowTaskResult.Failed(created.Error.Code);
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
/// <c>note</c>, kararı veren kullanıcıya atanır). Karar, yorum ve onaylayıcı <b>HUMAN görev çıktısından değil</b>, yürütmenin
/// <c>approvals</c> satırlarından türetilir: karar verilmiş (onaylandı/reddedildi) satır yoksa hiçbir not yazılmaz (karar yazımı henüz
/// commit edilmemiş olabilir → geçici hata, motor yeniden dener). Fırsatın aşaması değişmez (bilgilendirici onay). Çıktı: <c>noteId</c>.
/// </summary>
public sealed class RecordDecisionTaskHandler(IApprovalRepository approvals, IActivityCreator activities, WorkflowTexts texts) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.RecordDecisionTask;

    public WorkflowRuleKind ExecutionKind => WorkflowRuleKind.DealApproval;

    public async Task<WorkflowTaskResult> ExecuteAsync(TrustedTask task, CancellationToken cancellationToken)
    {
        var decided = (await approvals.ListByExecutionAsync(task.ExecutionId, cancellationToken).ConfigureAwait(false))
            .Where(a => a.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected)
            .OrderBy(a => a.DecidedAt)
            .FirstOrDefault();
        if (decided is null)
        {
            return WorkflowTaskResult.Retry(WorkflowsErrors.DecisionNotFound);
        }

        var subject = await texts.DecisionNoteAsync(decided.Status == ApprovalStatus.Approved, decided.Comment, cancellationToken).ConfigureAwait(false);
        var created = await activities.CreateNoteAsync(decided.ApproverUserId, subject, decided.Comment, new RecordRef(RecordType.Deal, task.SubjectId), cancellationToken).ConfigureAwait(false);
        return created.IsSuccess
            ? WorkflowTaskResult.Completed(new JsonObject { ["noteId"] = created.Value })
            : CreateFollowUpTaskHandler.Rejected(created.Error);
    }
}

/// <summary>
/// <c>crm_cancel_pending_approvals</c>: yürütmenin kararı beklenen diğer onaylarını <c>cancelled</c> yapar (güvenlik ağı:
/// karar uç noktası bunları aynı transaction'da zaten iptal eder). Yalnız doğrulanmış yürütmenin onaylarına dokunur. Çıktı: <c>cancelled</c>.
/// </summary>
public sealed class CancelPendingApprovalsTaskHandler(IApprovalRepository approvals, IWorkflowsUnitOfWork unitOfWork, TimeProvider clock) : IWorkflowTaskWorker
{
    public string TaskType => WorkflowNames.CancelPendingApprovalsTask;

    public WorkflowRuleKind ExecutionKind => WorkflowRuleKind.DealApproval;

    public async Task<WorkflowTaskResult> ExecuteAsync(TrustedTask task, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cancelled = (await approvals.ListByExecutionAsync(task.ExecutionId, cancellationToken).ConfigureAwait(false)).Count(a => a.Cancel(now));
        if (cancelled > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return WorkflowTaskResult.Completed(new JsonObject { ["cancelled"] = cancelled });
    }
}
