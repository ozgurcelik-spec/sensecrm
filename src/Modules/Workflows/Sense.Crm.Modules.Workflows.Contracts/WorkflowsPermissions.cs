using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Workflows.Contracts;

/// <summary>
/// Workflows modülü izinleri (docs/plan/m4-workflow.md). <c>org.workflows.manage</c> kural + yürütme yönetimi (grup <c>org</c>),
/// <c>crm.approvals.decide</c> onay verebilme (grup <c>crm</c>). Administrator tümünü alır; Standard bu iki anahtarı almaz
/// (Identity'deki sistem rolü tanımı). Görünen adlar istemcide çevrilir (K8).
/// </summary>
public static class WorkflowsPermissions
{
    public const string Module = "workflows";

    public const string Manage = "org.workflows.manage";
    public const string ApprovalsDecide = "crm.approvals.decide";

    /// <summary>All Workflows module permissions.</summary>
    public static IReadOnlyList<Permission> All { get; } =
    [
        new(Manage, Module, "org"),
        new(ApprovalsDecide, Module, "crm"),
    ];
}

/// <summary>Workflows varlık türlerinin denetim kaydındaki adları (CLR tip adı) ve kayıt bazlı okuma izinleri.</summary>
public static class WorkflowsAuditEntities
{
    public const string Rule = "WorkflowRule";
    public const string Execution = "WorkflowExecution";
    public const string Approval = "Approval";

    public static IReadOnlyDictionary<string, string> ReadPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Rule] = WorkflowsPermissions.Manage,
        [Execution] = WorkflowsPermissions.Manage,
        [Approval] = WorkflowsPermissions.Manage,
    };
}
