using System.Text.Json;

namespace Sense.Crm.Modules.Integrations.Application;

// HTTP sözleşmesi (docs/plan/m8b-entegrasyonlar.md): camelCase, null alanlar yazılmaz, zaman damgaları UTC ISO 8601.

/// <summary>
/// Webhook aboneliği. <c>secret</c> yalnız oluşturma yanıtında bir kez döner (<c>Cache-Control: no-store</c>); sonrasında yalnız <c>secretHint</c> (<c>…</c> + son 4 karakter).
/// <c>health</c>: <c>disabled | failing | degraded | healthy</c>.
/// </summary>
public sealed record WebhookSubscriptionDto(
    Guid Id,
    string Name,
    string Url,
    string Host,
    IReadOnlyList<string> EventTypes,
    bool Enabled,
    string? DisabledReason,
    string? Description,
    string SecretHint,
    int SecretVersion,
    DateTime? PreviousSecretExpiresAt,
    int ConsecutiveFailures,
    string Health,
    DateTime? LastSuccessAt,
    DateTime? LastFailureAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    Guid CreatedByUserId,
    string? CreatedByName,
    string? Secret = null);

/// <summary><c>POST …/rotate-secret</c> yanıtı (<c>no-store</c>): yeni ham sır bir kez döner.</summary>
public sealed record RotatedSecretDto(string Secret, string SecretHint, int SecretVersion, DateTime? PreviousSecretExpiresAt);

/// <summary><c>202</c> yanıtı: kuyruğa alınan teslimat kimliği (test ping'i / yeniden gönderme).</summary>
public sealed record DeliveryAcceptedDto(Guid DeliveryId);

public sealed record DeliveryListItemDto(
    Guid Id,
    Guid SubscriptionId,
    string SubscriptionName,
    Guid EventId,
    string EventType,
    string Kind,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTime? NextAttemptAt,
    DateTime? LastAttemptAt,
    DateTime? CompletedAt,
    int? ResponseStatus,
    int? DurationMs,
    string? FailureReason,
    string Host,
    DateTime CreatedAt);

public sealed record DeliveryAttemptDto(
    int AttemptNo,
    DateTime StartedAt,
    int? DurationMs,
    int? ResponseStatus,
    string? FailureReason,
    string? ErrorDetail,
    string? ResponseSnippet);

/// <summary>Teslimat ayrıntısı: zarf, gönderilen başlıklar (<c>X-Crm-Signature</c> <c>[redacted]</c>) ve deneme günlüğü. URL sorgu dizgisi asla gösterilmez.</summary>
public sealed record DeliveryDetailDto(
    Guid Id,
    Guid SubscriptionId,
    string SubscriptionName,
    Guid EventId,
    string EventType,
    string Kind,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTime? NextAttemptAt,
    DateTime? LastAttemptAt,
    DateTime? CompletedAt,
    int? ResponseStatus,
    int? DurationMs,
    string? FailureReason,
    string Host,
    DateTime CreatedAt,
    JsonElement Payload,
    IReadOnlyDictionary<string, string> RequestHeaders,
    IReadOnlyList<DeliveryAttemptDto> AttemptLog);

/// <summary>Olay kataloğu satırı (<c>GET /integrations/webhook-events</c>): <c>available</c> = kaynak modül kiracı planında açık.</summary>
public sealed record WebhookEventDto(
    string Type,
    int Version,
    string Group,
    string Module,
    bool Available,
    bool Deprecated,
    DateOnly? SunsetOn,
    string? Description,
    JsonElement Sample);

public sealed record IntegrationsStatusApiKeysDto(int MaxLifetimeDays, int DefaultLifetimeDays, int RateLimitPerMinute);

public sealed record IntegrationsStatusLimitsDto(int? MaxWebhooks, int? MaxApiKeys);

public sealed record IntegrationsStatusUsageDto(int Webhooks, int ApiKeys);

public sealed record IntegrationsStatusDto(
    bool WebhooksEnabled,
    bool RestrictedHosts,
    int MaxAttempts,
    int TimeoutSeconds,
    int SignatureToleranceSeconds,
    int DeliveryRetentionDays,
    IntegrationsStatusApiKeysDto ApiKeys,
    IntegrationsStatusLimitsDto Limits,
    IntegrationsStatusUsageDto Usage);

/// <summary>API anahtarı (özet/<c>secret_hash</c> asla): <c>key</c> (ham anahtar) yalnız oluşturma yanıtında bir kez döner (<c>no-store</c>).</summary>
public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string? Description,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTime ExpiresAt,
    IReadOnlyList<string> AllowedCidrs,
    string Status,
    DateTime CreatedAt,
    Guid CreatedByUserId,
    string? CreatedByName,
    DateTime? LastUsedAt,
    string? LastUsedIp,
    DateTime? RevokedAt,
    string? RevokedByName,
    string? Key = null);

public sealed record ApiKeyUsageDayDto(DateOnly Day, int Requests, int Errors, int Throttled);

public sealed record ApiKeyUsageDto(IReadOnlyList<ApiKeyUsageDayDto> Items);

/// <summary><c>GET /integrations/api-keys/current</c>: istemcinin anahtarını sınaması için (yalnız anahtar kimliğiyle).</summary>
public sealed record CurrentApiKeyDto(Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, IReadOnlyList<string> EffectiveScopes, DateTime ExpiresAt, Guid TenantId);
