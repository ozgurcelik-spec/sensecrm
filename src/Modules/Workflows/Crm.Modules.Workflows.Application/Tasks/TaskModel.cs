using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crm.Modules.Identity.Contracts;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Context;

namespace Crm.Modules.Workflows.Application.Tasks;

/// <summary>
/// Bir Conductor görev işleyicisinin sonucu. <see cref="Terminal"/> başarısızlık yeniden denenmez (iş kuralı hatası:
/// <c>no_assignee</c> gibi); terminal olmayan başarısızlık motorun görev tanımındaki yeniden deneme ayarına göre tekrarlanır.
/// </summary>
public sealed record WorkflowTaskResult(bool Succeeded, bool Terminal, string? Reason, JsonObject? Output)
{
    public static WorkflowTaskResult Completed(JsonObject? output = null) => new(true, false, null, output ?? []);

    /// <summary>İş kuralı hatası: yeniden denenmez, workflow <c>failed</c> olur (<paramref name="reason"/> yürütme hatası olarak gösterilir).</summary>
    public static WorkflowTaskResult Failed(string reason) => new(false, true, reason, null);

    /// <summary>Geçici hata: motor görev tanımına göre yeniden dener.</summary>
    public static WorkflowTaskResult Retry(string reason) => new(false, false, reason, null);
}

/// <summary>Bir workflow SIMPLE görevini işler. Kiracı ve sistem bağlamı çağıran (Worker'daki <c>WorkflowTaskRunner</c>) tarafından kurulur.</summary>
public interface IWorkflowTaskWorker
{
    /// <summary>Conductor görev adı (<see cref="WorkflowNames"/>).</summary>
    string TaskType { get; }

    Task<WorkflowTaskResult> ExecuteAsync(TaskInput input, CancellationToken cancellationToken);
}

/// <summary>Conductor görev girdisi (<c>inputData</c>) üzerinde tipli okuma yardımcıları.</summary>
public sealed class TaskInput(JsonElement data)
{
    public Guid TenantId => GetGuid("tenantId") ?? Guid.Empty;

    public Guid? GetGuid(string name) =>
        Property(name) is { ValueKind: JsonValueKind.String } e && Guid.TryParse(e.GetString(), CultureInfo.InvariantCulture, out var id) && id != Guid.Empty ? id : null;

    public string? GetString(string name) =>
        Property(name) is { ValueKind: JsonValueKind.String } e && !string.IsNullOrWhiteSpace(e.GetString()) ? e.GetString() : null;

    public int? GetInt(string name) =>
        Property(name) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var value) ? value : null;

    public decimal? GetDecimal(string name) =>
        Property(name) is { ValueKind: JsonValueKind.Number } e && e.TryGetDecimal(out var value) ? value : null;

    private JsonElement? Property(string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) ? value : null;
}

/// <summary>
/// Workflow'un oluşturduğu görev/not metinleri (organizasyonun varsayılan diline göre tr/en). Metinler sözleşmenin parçasıdır
/// (docs/plan/m4-workflow.md): "Yeni potansiyel: &lt;ad&gt;", "Fırsat onayı: &lt;ad&gt;", "Onay: onaylandı/reddedildi (yorum)".
/// </summary>
public sealed class WorkflowTexts(ITenantDirectory directory, ITenantContext tenant)
{
    private string? _language;

    public async Task<string> NewLeadTaskSubjectAsync(string name, CancellationToken ct) =>
        await IsEnglishAsync(ct).ConfigureAwait(false) ? $"New lead: {name}" : $"Yeni potansiyel: {name}";

    public async Task<string> DealApprovalTitleAsync(string name, CancellationToken ct) =>
        await IsEnglishAsync(ct).ConfigureAwait(false) ? $"Deal approval: {name}" : $"Fırsat onayı: {name}";

    public async Task<string> DecisionNoteAsync(bool approved, string? comment, CancellationToken ct)
    {
        var english = await IsEnglishAsync(ct).ConfigureAwait(false);
        var outcome = english ? (approved ? "approved" : "rejected") : (approved ? "onaylandı" : "reddedildi");
        var prefix = english ? "Approval" : "Onay";
        return string.IsNullOrWhiteSpace(comment) ? $"{prefix}: {outcome}" : $"{prefix}: {outcome} ({comment.Trim()})";
    }

    private async Task<bool> IsEnglishAsync(CancellationToken ct)
    {
        _language ??= (await directory.FindAsync(tenant.TenantId, ct).ConfigureAwait(false))?.DefaultLocale ?? Cultures.TurkishLanguage;
        return string.Equals(_language, Cultures.EnglishLanguage, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Round-robin aday: kullanıcı, açık lead sayısı ve son atanma zamanı (hiç atanmamışsa null).</summary>
public sealed record AssigneeCandidate(Guid UserId, string DisplayName, int OpenLeadCount, DateTime? LastAssignedAt);

/// <summary>
/// Round-robin seçimi (saf hesap, birim testli): en az açık lead'e sahip aday; eşitlikte en uzun süredir atama almamış
/// (hiç atanmamış en başta); hâlâ eşitse kullanıcı kimliği (kararlı, deterministik).
/// </summary>
public static class RoundRobinSelector
{
    public static AssigneeCandidate? Pick(IEnumerable<AssigneeCandidate> candidates) =>
        candidates
            .OrderBy(c => c.OpenLeadCount)
            .ThenBy(c => c.LastAssignedAt ?? DateTime.MinValue)
            .ThenBy(c => c.UserId)
            .FirstOrDefault();
}
