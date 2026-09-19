using System.Text.Json;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Workflows.Domain.Rules;

/// <summary>
/// Workflow kuralı: kiracının yöneticisinin tanımladığı, bir tür (<see cref="Kind"/>) + parametrelerden oluşan tetikleyici.
/// Parametreler tür şemasına göre doğrulanmış olarak (<see cref="RuleParams"/>) gelir ve JSON olarak saklanır. Aynı türden birden
/// çok etkin kural olabilir (hepsi çalışır). Silme yumuşaktır (yürütme geçmişi kural adını ve türünü kendi üzerinde tutar).
/// </summary>
public sealed class WorkflowRule : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private WorkflowRule()
    {
    }

    private WorkflowRule(Guid id, Guid tenantId, string name, WorkflowRuleKind kind, string paramsJson, bool isEnabled) : base(id, tenantId)
    {
        Name = name;
        Kind = kind;
        ParamsJson = paramsJson;
        IsEnabled = isEnabled;
    }

    public string Name { get; private set; } = string.Empty;

    public WorkflowRuleKind Kind { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>Doğrulanmış parametrelerin camelCase JSON'u (<see cref="RuleParams"/>).</summary>
    public string ParamsJson { get; private set; } = "{}";

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    /// <summary>Türüyle uyumlu <paramref name="ruleParams"/> gerekir (uyumsuzluk programlama hatasıdır).</summary>
    public static WorkflowRule Create(Guid tenantId, string name, WorkflowRuleKind kind, RuleParams ruleParams, bool isEnabled)
    {
        Guard.Against(!Matches(kind, ruleParams), "Rule params do not match the rule kind.");
        return new WorkflowRule(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(name).Trim(), WorkflowLimits.NameMaxLength),
            kind,
            ruleParams.ToJson(),
            isEnabled);
    }

    /// <summary>Tam değiştirme (PUT): ad, tür ve parametreler; etkin durumu ayrıca <see cref="SetEnabled"/> ile değişir.</summary>
    public void Update(string name, WorkflowRuleKind kind, RuleParams ruleParams)
    {
        Guard.Against(!Matches(kind, ruleParams), "Rule params do not match the rule kind.");
        Name = Guard.MaxLength(Guard.NotEmpty(name).Trim(), WorkflowLimits.NameMaxLength);
        Kind = kind;
        ParamsJson = ruleParams.ToJson();
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;

    /// <summary>Kural parametreleri (yalnız <see cref="WorkflowRuleKind.LeadAssignment"/> için).</summary>
    public LeadAssignmentParams LeadAssignment =>
        Kind == WorkflowRuleKind.LeadAssignment
            ? JsonSerializer.Deserialize<LeadAssignmentParams>(ParamsJson, RuleParams.Json)!
            : throw new InvalidOperationException("Rule is not a lead assignment rule.");

    /// <summary>Kural parametreleri (yalnız <see cref="WorkflowRuleKind.DealApproval"/> için).</summary>
    public DealApprovalParams DealApproval =>
        Kind == WorkflowRuleKind.DealApproval
            ? JsonSerializer.Deserialize<DealApprovalParams>(ParamsJson, RuleParams.Json)!
            : throw new InvalidOperationException("Rule is not a deal approval rule.");

    private static bool Matches(WorkflowRuleKind kind, RuleParams ruleParams) => kind switch
    {
        WorkflowRuleKind.LeadAssignment => ruleParams is LeadAssignmentParams,
        WorkflowRuleKind.DealApproval => ruleParams is DealApprovalParams,
        _ => false,
    };
}
