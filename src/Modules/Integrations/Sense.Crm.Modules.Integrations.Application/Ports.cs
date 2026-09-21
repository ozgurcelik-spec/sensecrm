using System.Text.Json.Nodes;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Persistence;

namespace Sense.Crm.Modules.Integrations.Application;

/// <summary>Integrations'ın SaveChanges portu (komutlar UnitOfWorkBehaviour ile; olay işleyicileri ve teslimat hattı doğrudan).</summary>
public interface IIntegrationsUnitOfWork : IUnitOfWork;

/// <summary>
/// Webhook sırrı koruması (D7): <b>AES-256-GCM</b>, AAD = <c>kiracı‖abonelik‖sürüm</c>; <c>keyId</c> hangi ana anahtarla şifrelendiğini belirtir (döndürme:
/// <c>reencrypt-integration-secrets</c>). HMAC için ham değer gerektiğinden hash'lenemez. Sırlar günlüğe/denetime/hata iletisine yazılmaz.
/// </summary>
public interface IWebhookSecretProtector
{
    /// <summary>Yeni ham sır üretir (<c>whsec_</c> + 32 bayt base64url).</summary>
    string GenerateSecret();

    SealedSecret Seal(string secret, Guid tenantId, Guid subscriptionId, int version);

    /// <summary>AAD uyuşmazlığı (başka satıra kopyalanmış blob) veya bilinmeyen anahtar → <see cref="System.Security.Cryptography.CryptographicException"/>.</summary>
    string Open(byte[] cipher, string keyId, Guid tenantId, Guid subscriptionId, int version);

    /// <summary>Şifrelemede kullanılan etkin anahtar kimliği.</summary>
    string CurrentKeyId { get; }
}

/// <summary>Kısıtlı eylem denetimi (test ping'i, yeniden gönderme; bellek içi, kopya başına): pencere içinde <c>limit</c> aşılırsa false.</summary>
public interface IActionThrottle
{
    bool TryAcquire(string key, int limit, TimeSpan window);
}

/// <summary>API anahtarı önbellek geçersiz kılma (iptal/güncelleme aynı süreçte anında; çok kopyada ≤ <c>CacheSeconds</c>).</summary>
public interface IApiKeyCacheInvalidator
{
    Task InvalidateAsync(Guid tenantId, string prefix, CancellationToken ct);
}

/// <summary>Anahtara verilebilecek/bilinen kapsam kataloğu (izin kataloğundan; Identity/Application'a bağlanmadan).</summary>
public interface IApiKeyScopeCatalog
{
    /// <summary>Kataloğun tüm izin anahtarları (bilinen kapsam denetimi).</summary>
    IReadOnlySet<string> AllKnown { get; }

    /// <summary>Anahtara verilebilir kapsamlar: <c>crm.*</c> eksi <c>crm.approvals.decide</c>.</summary>
    IReadOnlyList<string> Allowed { get; }
}

/// <summary>
/// Fan-out (olay → abonelik başına teslimat + kuyruk satırı, tek <c>SaveChanges</c>): <c>Integrations:Webhooks:Enabled=false</c> ise hiçbir şey yazmaz. Zarf <b>bir kez</b>
/// serileştirilir (tam bayt dizisi saklanır); <c>(subscription_id, event_id)</c> tekildir (outbox yeniden denemesi çift teslimat üretmez).
/// </summary>
public interface IWebhookFanOut
{
    Task PublishAsync(WebhookOccurrence occurrence, CancellationToken ct);
}

/// <summary>Bir olayın zarfa dönüştürülecek anlık görüntüsü (<c>Data</c> açık izin listesiyle üretilmiş, PII'siz).</summary>
public sealed record WebhookOccurrence(Guid EventId, string Type, Guid TenantId, Guid? ActorUserId, DateTime OccurredAt, JsonObject Data);

/// <summary>Kaynak kod → uygulama durumu: webhook gönderimi bu kurulumda etkin mi (yapılandırma; API dış çağrı yapmaz).</summary>
public interface IWebhookRuntime
{
    /// <summary><c>Enabled</c> ∧ egress yapılandırması geçerli.</summary>
    bool DeliveryAvailable { get; }
}

/// <summary>Okuma tarafı: projeksiyonlar Infrastructure'da EF ile, kiracı filtresi altında; sıralama beyaz listeli, her zaman kimlikle kararlı; arama <c>ILIKE</c> + kaçışlı parametre.</summary>
public interface IWebhookReadStore
{
    Task<PagedResult<WebhookSubscriptionDto>> ListSubscriptionsAsync(PagedQuery paging, string? eventType, bool? enabled, DateTime nowUtc, CancellationToken ct);

    Task<WebhookSubscriptionDto?> GetSubscriptionAsync(Guid id, DateTime nowUtc, CancellationToken ct);

    Task<PagedResult<DeliveryListItemDto>> ListDeliveriesAsync(PagedQuery paging, DeliveryFilter filter, int maxAttempts, CancellationToken ct);

    Task<DeliveryDetailDto?> GetDeliveryAsync(Guid id, int maxAttempts, CancellationToken ct);
}

public sealed record DeliveryFilter(Guid? SubscriptionId, IReadOnlyList<string> Statuses, string? EventType, Guid? EventId, string? Kind, DateOnly? From, DateOnly? To);

public interface IApiKeyReadStore
{
    Task<PagedResult<ApiKeyDto>> ListAsync(PagedQuery paging, string? status, DateTime nowUtc, CancellationToken ct);

    Task<ApiKeyDto?> GetAsync(Guid id, DateTime nowUtc, CancellationToken ct);

    Task<IReadOnlyList<ApiKeyUsageDayDto>> GetUsageAsync(Guid keyId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Anahtarın bitişi (<c>GET /api-keys/current</c>).</summary>
    Task<DateTime?> GetExpiryAsync(Guid keyId, CancellationToken ct);
}

/// <summary>Kiracı sayaçları (durum ucu): abonelik sayısı ve etkin anahtar sayısı (canlı, önbelleksiz).</summary>
public interface IIntegrationsStats
{
    Task<(int Webhooks, int ApiKeys)> CountAsync(DateTime nowUtc, CancellationToken ct);
}

/// <summary>Temel OpenAPI belgesi kaynağı (Api uygular; süreç başına bir kez üretilip önbelleklenir). Kiracı süzgeci Application'dadır.</summary>
public interface IOpenApiBaseDocumentSource
{
    Task<string> GetJsonAsync(CancellationToken ct);
}
