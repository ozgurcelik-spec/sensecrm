using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.ApiKeys;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Persistence;

public sealed class WebhookSubscriptionRepository(IntegrationsDbContext db) : IWebhookSubscriptionRepository
{
    public Task<WebhookSubscription?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.WebhookSubscriptions.FirstOrDefaultAsync(x => x.Id == id, ct);

    public void Add(WebhookSubscription subscription) => db.WebhookSubscriptions.Add(subscription);

    public async Task RemoveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        // Kuyruk satırları (FK'sız küresel tablo) açıkça silinir; teslimat/deneme satırları veritabanı kaskadıyla silinir.
        var subscriptionId = subscription.Id;
        var deliveryIds = db.WebhookDeliveries.Where(d => d.SubscriptionId == subscriptionId).Select(d => d.Id);
        await db.DeliveryQueue.Where(q => deliveryIds.Contains(q.DeliveryId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.WebhookSubscriptions.Remove(subscription);
    }

    public Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var lower = name.Trim().ToLowerInvariant();
        return db.WebhookSubscriptions.AnyAsync(x => x.Name.ToLower() == lower && (excludeId == null || x.Id != excludeId), ct);
    }
}

public sealed class WebhookDeliveryRepository(IntegrationsDbContext db) : IWebhookDeliveryRepository
{
    public Task<WebhookDelivery?> GetByIdAsync(Guid id, CancellationToken ct) => db.WebhookDeliveries.FirstOrDefaultAsync(x => x.Id == id, ct);

    public void Add(WebhookDelivery delivery) => db.WebhookDeliveries.Add(delivery);

    public void AddQueueItem(DeliveryQueueItem item) => db.DeliveryQueue.Add(item);
}

public sealed class ApiKeyRepository(IntegrationsDbContext db) : IApiKeyRepository
{
    public Task<ApiKey?> GetByIdAsync(Guid id, CancellationToken ct) => db.ApiKeys.FirstOrDefaultAsync(x => x.Id == id, ct);

    public void Add(ApiKey key) => db.ApiKeys.Add(key);

    public void Remove(ApiKey key) => db.ApiKeys.Remove(key);

    public Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var lower = name.Trim().ToLowerInvariant();
        return db.ApiKeys.AnyAsync(x => x.RevokedAt == null && x.Name.ToLower() == lower && (excludeId == null || x.Id != excludeId), ct);
    }

    public Task<bool> PrefixExistsAsync(string prefix, CancellationToken ct) => db.ApiKeys.AnyAsync(x => x.Prefix == prefix, ct);
}
