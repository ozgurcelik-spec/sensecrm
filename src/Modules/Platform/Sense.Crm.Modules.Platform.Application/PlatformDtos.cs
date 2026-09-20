using System.Text.Json;
using Sense.Crm.Shared.Contracts.Entitlements;

namespace Sense.Crm.Modules.Platform.Application;

// HTTP sözleşmesi (docs/plan/m7-saas-hazirlik.md): alan adları camelCase, null alanlar yazılmaz, enum'lar camelCase string.

/// <summary>Konsol satırındaki son anlık görüntü özeti.</summary>
public sealed record OrganizationUsageDto(DateOnly Day, int UsersActive, int UsersPending, IReadOnlyDictionary<string, long> Records);

/// <summary>
/// Organizasyon satırı. <c>Status</c> = <b>etkin</b> durum (<c>trial | active | trial_expired | suspended | pending_deletion | deleted</c>),
/// <c>AccessLevel</c> = <c>full | readOnly | none</c>.
/// </summary>
public sealed record OrganizationRowDto(
    Guid TenantId,
    string Name,
    string Slug,
    string PlanCode,
    string PlanName,
    string Source,
    string Status,
    AccessLevel AccessLevel,
    DateOnly? TrialEndsOn,
    bool IsSystem,
    DateTimeOffset CreatedAt,
    OrganizationUsageDto? Usage);

/// <summary>Etkin limitler (plan + istisna): <c>maxUsers</c> yoksa sınırsız; <c>maxRecords</c> yalnız sonlu olanları yazar.</summary>
public sealed record EffectiveLimitsDto(int? MaxUsers, IReadOnlyDictionary<string, int> MaxRecords, IReadOnlyDictionary<string, bool> Modules, int? MaxWebhooks = null, int? MaxApiKeys = null, int? MaxStorageMb = null);

public sealed record SuspensionDto(string Mode, string? Reason, DateTimeOffset? At);

public sealed record DeletionDto(
    Guid RequestId,
    string Status,
    DateTimeOffset RequestedAt,
    string? RequestedByEmail,
    string Reason,
    int RetentionDays,
    DateTimeOffset ScheduledFor,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? CompletedAt,
    int Attempts,
    string? LastError);

/// <summary>Organizasyon detayı: satır alanları + etkin limitler, istisna, askı ve silme durumu.</summary>
public sealed record OrganizationDetailDto(
    Guid TenantId,
    string Name,
    string Slug,
    string PlanCode,
    string PlanName,
    string Source,
    string Status,
    AccessLevel AccessLevel,
    DateOnly? TrialEndsOn,
    bool IsSystem,
    DateTimeOffset CreatedAt,
    OrganizationUsageDto? Usage,
    EffectiveLimitsDto Limits,
    JsonElement? Overrides,
    DateTimeOffset? PlanChangedAt,
    SuspensionDto? Suspension,
    DeletionDto? Deletion);

public sealed record OverLimitDto(string Limit, string? Module, long Max, long Used);

public sealed record SubscriptionUpdateResultDto(IReadOnlyList<OverLimitDto> OverLimit);

public sealed record DeletionRequestResultDto(Guid RequestId, DateTimeOffset ScheduledFor);

public sealed record PlanLimitsDto(int? MaxUsers, IReadOnlyDictionary<string, int?> MaxRecords, int? MaxWebhooks = null, int? MaxApiKeys = null, int? MaxStorageMb = null);

public sealed record PlanDto(
    string Code,
    string Name,
    string? Description,
    bool IsActive,
    int SortOrder,
    int? TrialDays,
    PlanLimitsDto Limits,
    IReadOnlyDictionary<string, bool> Modules,
    int AssignedCount);

public sealed record PlatformAuditDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    Guid? ActorUserId,
    string? ActorEmail,
    Guid? TargetTenantId,
    string? TargetTenantName,
    JsonElement Details,
    string? CorrelationId);

public sealed record UsageDayDto(DateOnly Day, int UsersActive, int UsersPending, IReadOnlyDictionary<string, long> Metrics);

public sealed record UsageSeriesDto(IReadOnlyList<UsageDayDto> Items);

/// <summary>Dışa aktarma isteği doğrulandı ve denetlendi; gövde <c>IUsageExportWriter</c> ile akıtılır.</summary>
public sealed record UsageExportInfoDto(DateOnly From, DateOnly To, long Rows);

// ---- Kiracı tarafı ----

public sealed record SubscriptionLimitsDto(int? MaxUsers, IReadOnlyDictionary<string, int> MaxRecords, int? MaxWebhooks = null, int? MaxApiKeys = null, int? MaxStorageMb = null);

public sealed record SubscriptionUsageDto(DateTimeOffset AsOf, int Users, int PendingUsers, IReadOnlyDictionary<string, long> Records, long? Webhooks = null, long? ApiKeys = null, long StorageBytes = 0, long FileCount = 0);

/// <summary><c>GET /subscription</c>: <c>limits</c> yalnız <b>sonlu</b> olanları yazar (sınırsız = anahtar yok).</summary>
public sealed record SubscriptionDto(
    string PlanCode,
    string PlanName,
    string Status,
    AccessLevel AccessLevel,
    DateOnly? TrialEndsOn,
    int? TrialDaysLeft,
    IReadOnlyDictionary<string, bool> Modules,
    SubscriptionLimitsDto Limits,
    SubscriptionUsageDto Usage,
    IReadOnlyList<OverLimitDto> OverLimit);

public sealed record OnboardingItemDto(string Key, bool Done);

public sealed record OnboardingDto(bool Dismissed, int CompletedCount, int TotalCount, IReadOnlyList<OnboardingItemDto> Items);

/// <summary>Onboarding adım anahtarları (sözleşme; sabit).</summary>
public static class OnboardingKeys
{
    public const string Profile = "profile";
    public const string InviteUser = "invite_user";
    public const string CreateLead = "create_lead";
    public const string CreateWorkflowRule = "create_workflow_rule";
}
