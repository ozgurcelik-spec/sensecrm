using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.Webhooks;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Modules.Integrations.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Delivery;

/// <summary>
/// Fan-out (D4): kiracının <b>etkin</b> aboneliklerinden olay türünü içerenleri seçer, zarfı <b>bir kez</b> serileştirir (tam bayt dizisi <c>payload text</c>), her abonelik için
/// <c>webhook_deliveries</c> + <c>delivery_queue</c> satırını tek <c>SaveChanges</c>'te yazar. <c>(subscription_id, event_id)</c> tekildir: outbox yeniden denemesi çift teslimat üretmez (önce var mı denetimi +
/// benzersiz indeks yarışta korur). <c>Integrations:Webhooks:Enabled=false</c> ise hiçbir şey yazmaz (kapalıyken üretilen olaylar sonradan teslim edilmez). Boyut aşan zarf üretilmez: denenmeden
/// <c>failed</c> (<c>payload_too_large</c>).
/// </summary>
public sealed partial class WebhookFanOut(
    IntegrationsDbContext db,
    IOptions<IntegrationsOptions> options,
    TimeProvider clock,
    ILogger<WebhookFanOut> logger) : IWebhookFanOut
{
    public async Task PublishAsync(WebhookOccurrence occurrence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var settings = options.Value.Webhooks;
        if (!settings.Enabled)
        {
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var type = occurrence.Type;
            var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
                .Where(s => s.Enabled && s.EventTypes.Contains(type))
                .Select(s => new { s.Id, s.Host })
                .ToListAsync(ct).ConfigureAwait(false);
            if (subscriptions.Count == 0)
            {
                return;
            }

            var eventId = occurrence.EventId;
            var ids = subscriptions.Select(s => s.Id).ToArray();
            var existing = (await db.WebhookDeliveries.AsNoTracking()
                .Where(d => d.EventId == eventId && d.Kind == DeliveryKinds.Event && ids.Contains(d.SubscriptionId))
                .Select(d => d.SubscriptionId)
                .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

            var pending = subscriptions.Where(s => !existing.Contains(s.Id)).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            var payload = WebhookEnvelope.Serialize(occurrence.EventId, occurrence.Type, WebhookEnvelope.CurrentVersion, occurrence.OccurredAt, occurrence.TenantId, occurrence.ActorUserId, occurrence.Data);
            var tooLarge = Encoding.UTF8.GetByteCount(payload) > settings.MaxPayloadBytes;
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var subscription in pending)
            {
                var delivery = WebhookDelivery.Create(
                    occurrence.TenantId, subscription.Id, occurrence.EventId, occurrence.Type, DeliveryKinds.Event, redeliveryOf: null, tooLarge ? string.Empty : payload, subscription.Host, now);
                if (tooLarge)
                {
                    delivery.FailWithoutAttempt(DeliveryFailureReasons.PayloadTooLarge, now);
                    LogTooLarge(logger, occurrence.Type, subscription.Id);
                }
                else
                {
                    db.DeliveryQueue.Add(new DeliveryQueueItem { DeliveryId = delivery.Id, TenantId = occurrence.TenantId, DueAt = now, Attempt = 0 });
                }

                db.WebhookDeliveries.Add(delivery);
            }

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && attempt == 0)
            {
                // Eşzamanlı ikinci işleyici aynı (abonelik, olay) satırını yazdı: izleyiciyi temizle, kalanları yeniden hesapla.
                db.ChangeTracker.Clear();
            }
        }
    }

    [LoggerMessage(EventId = 8200, Level = LogLevel.Error, Message = "Webhook envelope for {EventType} exceeded the payload limit; delivery to subscription {SubscriptionId} marked failed")]
    private static partial void LogTooLarge(ILogger logger, string eventType, Guid subscriptionId);
}

/// <summary>Bu kurulumda webhook gönderimi etkin mi (D6): <c>Enabled</c> ∧ egress yapılandırması geçerli (API yalnız yapılandırmayı okur, dış çağrı yapmaz).</summary>
public sealed class WebhookRuntime(IOptions<IntegrationsOptions> options) : IWebhookRuntime
{
    public bool DeliveryAvailable
    {
        get
        {
            var o = options.Value;
            var w = o.Webhooks;
            var proxy = !string.IsNullOrWhiteSpace(w.EgressProxy) && !string.IsNullOrWhiteSpace(w.DnsServer);
            return w.Enabled && (proxy || (o.DevelopmentLike && w.AllowDirectEgress));
        }
    }
}
