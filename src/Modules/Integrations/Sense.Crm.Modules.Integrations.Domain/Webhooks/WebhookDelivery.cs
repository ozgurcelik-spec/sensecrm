using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Integrations.Domain.Webhooks;

/// <summary>Teslimat durumu (tel değerleri).</summary>
public static class DeliveryStatuses
{
    public const string Pending = "pending";
    public const string Delivering = "delivering";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>Teslimat türü (tel değerleri).</summary>
public static class DeliveryKinds
{
    public const string Event = "event";
    public const string Ping = "ping";
    public const string Redelivery = "redelivery";
}

/// <summary>Teslimat başarısızlık nedenleri (tel değerleri; <c>failure_reason</c> sütunu ve arayüz çevirisi).</summary>
public static class DeliveryFailureReasons
{
    public const string Timeout = "timeout";
    public const string ConnectionError = "connection_error";
    public const string HttpError = "http_error";
    public const string Redirect = "redirect";
    public const string BlockedDestination = "blocked_destination";
    public const string TlsError = "tls_error";
    public const string DnsError = "dns_error";
    public const string RetriesExhausted = "retries_exhausted";
    public const string Expired = "expired";
    public const string PayloadTooLarge = "payload_too_large";
}

/// <summary>
/// Bir olayın bir aboneliğe teslimatı (denetimsiz günlük tablosu). <c>payload</c> imzalanan <b>tam bayt dizisinin</b> metin karşılığıdır (<c>text</c>, <c>jsonb</c> değil: anahtar sırası
/// bozulup imza geçersiz olurdu). <c>host</c> anlık görüntüdür (URL/sorgu dizgisi hiçbir yerde saklanmaz/gösterilmez). En az bir kez teslimat:
/// <c>(subscription_id, event_id)</c> yalnız <c>kind = event</c> için benzersizdir.
/// </summary>
public sealed class WebhookDelivery : TenantEntity<Guid>
{
    private WebhookDelivery()
    {
    }

    private WebhookDelivery(Guid id, Guid tenantId) : base(id, tenantId)
    {
    }

    public Guid SubscriptionId { get; private set; }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string Kind { get; private set; } = string.Empty;

    public Guid? RedeliveryOf { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public string Host { get; private set; } = string.Empty;

    public int Attempts { get; private set; }

    public DateTime? NextAttemptAt { get; private set; }

    public DateTime? LastAttemptAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public int? ResponseStatus { get; private set; }

    public int? DurationMs { get; private set; }

    public string? FailureReason { get; private set; }

    public static WebhookDelivery Create(
        Guid tenantId,
        Guid subscriptionId,
        Guid eventId,
        string eventType,
        string kind,
        Guid? redeliveryOf,
        string payload,
        string host,
        DateTime now) =>
        new(Guid.CreateVersion7(), tenantId)
        {
            SubscriptionId = subscriptionId,
            EventId = eventId,
            EventType = Guard.MaxLength(eventType, IntegrationsLimits.EventTypeMaxLength),
            Kind = kind,
            RedeliveryOf = redeliveryOf,
            Status = DeliveryStatuses.Pending,
            Payload = payload,
            Host = Guard.MaxLength(host, IntegrationsLimits.HostMaxLength),
            NextAttemptAt = now,
            CreatedAt = now,
        };

    /// <summary>Üretilemeyen (boyut aşımı) zarf: denenmeden <c>failed</c>.</summary>
    public void FailWithoutAttempt(string reason, DateTime now)
    {
        Status = DeliveryStatuses.Failed;
        FailureReason = Guard.MaxLength(reason, IntegrationsLimits.FailureReasonMaxLength);
        CompletedAt = now;
        NextAttemptAt = null;
    }

    public void BeginAttempt(DateTime now)
    {
        Attempts += 1;
        LastAttemptAt = now;
        Status = DeliveryStatuses.Delivering;
    }

    public void Succeed(int? responseStatus, int durationMs, DateTime now)
    {
        Status = DeliveryStatuses.Succeeded;
        ResponseStatus = responseStatus;
        DurationMs = durationMs;
        FailureReason = null;
        CompletedAt = now;
        NextAttemptAt = null;
    }

    /// <summary>Yeniden denenecek başarısızlık: durum <c>pending</c>, sonraki deneme zamanı yazılır.</summary>
    public void ScheduleRetry(int? responseStatus, int durationMs, string reason, DateTime nextAttemptAt)
    {
        Status = DeliveryStatuses.Pending;
        ResponseStatus = responseStatus;
        DurationMs = durationMs;
        FailureReason = Guard.MaxLength(reason, IntegrationsLimits.FailureReasonMaxLength);
        NextAttemptAt = nextAttemptAt;
    }

    /// <summary>Terminal başarısızlık (yeniden denenmez); elle yeniden gönderilebilir.</summary>
    public void Fail(int? responseStatus, int durationMs, string reason, DateTime now)
    {
        Status = DeliveryStatuses.Failed;
        ResponseStatus = responseStatus;
        DurationMs = durationMs;
        FailureReason = Guard.MaxLength(reason, IntegrationsLimits.FailureReasonMaxLength);
        CompletedAt = now;
        NextAttemptAt = null;
    }

    public bool IsRedeliverable => Status is DeliveryStatuses.Succeeded or DeliveryStatuses.Failed;
}

/// <summary>Bir teslimat denemesinin kaydı: başlangıç, süre, HTTP durumu, temizlenmiş neden ve yanıt özeti.</summary>
public sealed class WebhookDeliveryAttempt : TenantEntity<Guid>
{
    private WebhookDeliveryAttempt()
    {
    }

    private WebhookDeliveryAttempt(Guid id, Guid tenantId) : base(id, tenantId)
    {
    }

    public Guid DeliveryId { get; private set; }

    public int AttemptNo { get; private set; }

    public DateTime StartedAt { get; private set; }

    public int? DurationMs { get; private set; }

    public int? ResponseStatus { get; private set; }

    public string? FailureReason { get; private set; }

    public string? ErrorDetail { get; private set; }

    public string? ResponseSnippet { get; private set; }

    public static WebhookDeliveryAttempt Create(
        Guid tenantId,
        Guid deliveryId,
        int attemptNo,
        DateTime startedAt,
        int? durationMs,
        int? responseStatus,
        string? failureReason,
        string? errorDetail,
        string? responseSnippet) =>
        new(Guid.CreateVersion7(), tenantId)
        {
            DeliveryId = deliveryId,
            AttemptNo = attemptNo,
            StartedAt = startedAt,
            DurationMs = durationMs,
            ResponseStatus = responseStatus,
            FailureReason = failureReason,
            ErrorDetail = errorDetail is null ? null : Truncate(errorDetail, IntegrationsLimits.ErrorDetailMaxLength),
            ResponseSnippet = responseSnippet is null ? null : Truncate(responseSnippet, IntegrationsLimits.ResponseSnippetMaxLength),
        };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Küresel teknik kuyruk satırı (outbox gibi kiracı filtresiz; <c>ITenantEntity</c> DEĞİLDİR): dispatcher tüm kiracıların vadesi gelmiş işlerini tek taramayla alır,
/// sonra kiracı kapsamına girer. Terminal durumda satır silinir; yalnız dispatcher/fan-out/redeliver yazar. İmha <c>IntegrationsQueueEraser</c> ile yapılır.
/// </summary>
public sealed class DeliveryQueueItem
{
    public Guid DeliveryId { get; set; }

    public Guid TenantId { get; set; }

    public DateTime DueAt { get; set; }

    public DateTime? LockedUntil { get; set; }

    public int Attempt { get; set; }
}
