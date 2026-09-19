using Crm.Modules.Workflows.Domain.Rules;

namespace Crm.Modules.Workflows.Application;

/// <summary>
/// Conductor'daki workflow/görev adları ve sürümleri (kodda sürümlü JSON tanımlarıyla eşleşir; bkz.
/// <c>Crm.Modules.Workflows.Infrastructure/Definitions</c>). Bir tanım değişirse sürüm artırılır ve yeni dosya eklenir.
/// </summary>
public static class WorkflowNames
{
    public const string LeadAssignmentWorkflow = "crm_lead_assignment";
    public const string DealApprovalWorkflow = "crm_deal_approval";
    public const int DefinitionVersion = 1;

    // SIMPLE görev türleri (Worker'da işlenir).
    public const string AssignLeadOwnerTask = "crm_assign_lead_owner";
    public const string CreateFollowUpTask = "crm_create_followup_task";
    public const string CreateApprovalsTask = "crm_create_approvals";
    public const string RecordDecisionTask = "crm_record_decision";
    public const string CancelPendingApprovalsTask = "crm_cancel_pending_approvals";

    /// <summary>Onay kararını bekleyen HUMAN görevinin referans adı (API karar verince bunu tamamlar).</summary>
    public const string WaitDecisionTaskRef = "wait_decision";

    /// <summary>Worker'ın poll ettiği tüm SIMPLE görev türleri.</summary>
    public static IReadOnlyList<string> TaskTypes { get; } =
    [
        AssignLeadOwnerTask,
        CreateFollowUpTask,
        CreateApprovalsTask,
        RecordDecisionTask,
        CancelPendingApprovalsTask,
    ];

    public static string WorkflowFor(WorkflowRuleKind kind) => kind switch
    {
        WorkflowRuleKind.LeadAssignment => LeadAssignmentWorkflow,
        WorkflowRuleKind.DealApproval => DealApprovalWorkflow,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
