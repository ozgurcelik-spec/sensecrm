using Sense.Crm.Modules.Integrations.Domain.ApiKeys;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;

namespace Sense.Crm.Modules.Integrations.Domain;

/// <summary>Abonelik deposu (kiracı filtresi altında; başka kiracının kaydı bulunmaz → <c>not_found</c>).</summary>
public interface IWebhookSubscriptionRepository
{
    Task<WebhookSubscription?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(WebhookSubscription subscription);

    /// <summary>Aboneliği ve kuyruk satırlarını siler; teslimat/deneme satırları veritabanı kaskadıyla silinir.</summary>
    Task RemoveAsync(WebhookSubscription subscription, CancellationToken ct);

    /// <summary>Ad kiracıda kullanımda mı (büyük/küçük harf duyarsız).</summary>
    Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct);
}

public interface IWebhookDeliveryRepository
{
    Task<WebhookDelivery?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(WebhookDelivery delivery);

    void AddQueueItem(DeliveryQueueItem item);
}

public interface IApiKeyRepository
{
    Task<ApiKey?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(ApiKey key);

    void Remove(ApiKey key);

    /// <summary>İptal edilmemiş anahtarlar arasında ad kullanımda mı.</summary>
    Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct);

    Task<bool> PrefixExistsAsync(string prefix, CancellationToken ct);
}
