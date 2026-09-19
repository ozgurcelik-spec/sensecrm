using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crm.Modules.Workflows.Domain.Rules;

/// <summary>Kuralın türü; her tür kendi tetikleyicisi ve akışı olan hazır bir workflow'a karşılık gelir.</summary>
public enum WorkflowRuleKind
{
    /// <summary>Tetik: yeni potansiyel müşteri (lead) oluşturuldu → round-robin atama + takip görevi.</summary>
    LeadAssignment,

    /// <summary>Tetik: fırsat kazanıldı ve tutar eşiği aşıldı → onay talepleri + karar sonucu notu.</summary>
    DealApproval,
}

/// <summary>Lead kaynağı tel adları (<c>Sales.Contracts.LeadCreated.Source</c> ile aynı; camelCase).</summary>
public static class LeadSources
{
    public static readonly IReadOnlyList<string> All = ["web", "referral", "campaign", "coldCall", "other"];

    public static bool IsKnown(string source) => All.Contains(source, StringComparer.Ordinal);
}

/// <summary>Kural parametrelerinin ortak tabanı. Depoda camelCase JSON olarak saklanır ve API'de aynı biçimde döner.</summary>
public abstract record RuleParams
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, GetType(), Json);
}

/// <summary>
/// <c>leadAssignment</c> parametreleri: <see cref="Sources"/> boşsa tüm kaynaklar; <see cref="AssigneeRoleId"/> atama havuzunu
/// (rolün aktif üyeleri) belirler; <see cref="FollowUpHours"/> takip görevinin vadesi (1–720 saat).
/// </summary>
public sealed record LeadAssignmentParams(IReadOnlyList<string> Sources, Guid AssigneeRoleId, int FollowUpHours) : RuleParams
{
    /// <summary>Kaynak filtresi bu kaynağı kapsıyor mu (boş liste = hepsi).</summary>
    public bool Matches(string source) => Sources.Count == 0 || Sources.Contains(source, StringComparer.Ordinal);
}

/// <summary><c>dealApproval</c> parametreleri: <c>amount &gt;= MinAmount</c> olan kazanılmış fırsatlar için <see cref="ApproverRoleId"/> rolündeki herkese onay açılır.</summary>
public sealed record DealApprovalParams(decimal MinAmount, Guid ApproverRoleId) : RuleParams
{
    public bool Matches(decimal? amount) => amount is { } value && value >= MinAmount;
}
