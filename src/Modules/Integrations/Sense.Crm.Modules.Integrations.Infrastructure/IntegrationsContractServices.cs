using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Infrastructure.Delivery;
using Sense.Crm.Modules.Integrations.Infrastructure.Persistence;
using Sense.Crm.Modules.Integrations.Infrastructure.Security;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Contracts.Usage;
using Sense.Crm.Shared.Infrastructure.Context;

namespace Sense.Crm.Modules.Integrations.Infrastructure;

/// <summary>
/// Integrations'ın host'lara (API, Worker, Migrator) sunduğu ortak kayıtlar: seçenekler (doğrulamalı), gizli koruması, kullanım ölçümü (M7 <c>IUsageReporter</c>), KVKK imha adımı, repolar ve fan-out.
/// Worker'a ek olarak <see cref="AddIntegrationsWorkerServices"/> (dispatcher, taşıyıcı, çözümleyici, saklama) kaydedilir; <b>API asla hedefe bağlanmaz</b> (taşıyıcı yalnız Worker'da kayıtlıdır).
/// </summary>
public static class IntegrationsContractServices
{
    public static IServiceCollection AddIntegrationsContractServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IntegrationsOptions>()
            .Bind(configuration.GetSection(IntegrationsOptions.SectionName))
            .PostConfigure<IHostEnvironment>((o, env) => o.DevelopmentLike = env.IsDevelopment() || env.IsEnvironment("Testing"))
            .Validate(o => IntegrationsOptionsValidator.Validate(o).Count == 0, "Integrations configuration is invalid (see IntegrationsOptionsValidator).")
            .ValidateOnStart();
        services.AddOptions<IntegrationsOptions>().Validate(EncryptionKeyIsUsable, "Integrations:Encryption:Keys:<CurrentKeyId> must be a base64 32-byte key (required outside Development/Testing).");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWebhookSecretProtector, WebhookSecretProtector>();
        services.TryAddSingleton<IActionThrottle, ActionThrottle>();
        services.TryAddSingleton<IWebhookRuntime, WebhookRuntime>();
        services.AddMemoryCache();

        services.AddScoped<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();
        services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IIntegrationsUnitOfWork>(sp => sp.GetRequiredService<IntegrationsDbContext>());
        services.AddScoped<IWebhookFanOut, WebhookFanOut>();
        services.AddScoped<IntegrationsSecretReencryptor>();

        // M7: kullanım ölçümü (Platform anlık görüntü işi/limit denetimi) ve KVKK imha adımları.
        services.AddScoped<IUsageReporter, IntegrationsUsageReporter>();
        services.AddScoped<ITenantDataEraser, IntegrationsQueueEraser>();
        return services;
    }

    /// <summary>Worker: dispatcher çekirdeği, güvenli HTTP taşıyıcı, çözümleyici, kuyruk deposu ve saklama.</summary>
    public static IServiceCollection AddIntegrationsWorkerServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IJitter, RandomJitter>();
        services.TryAddSingleton<IDnsResolver, WebhookDnsResolver>();
        services.TryAddSingleton<IWebhookTransport, SafeWebhookHttpTransport>();
        services.TryAddSingleton<DispatchLimiter>();
        services.AddScoped<DeliveryQueueStore>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddScoped<IntegrationsRetention>();
        return services;
    }

    /// <summary>
    /// Worker: fan-out olay işleyicileri (<c>IIntegrationEventHandler&lt;&gt;</c>, Application assembly'sinden yalnız bunlar). Worker Application assembly'lerini tam taramadığı için (komut işleyicilerinin bağımlılıkları
    /// yoktur) olay işleyicileri hedefli kaydedilir. <c>[EntitlementExempt]</c> yoktur → askıdaki/kapalı kiracıda <c>InProcessEventBus</c> işleyiciyi atlar.
    /// </summary>
    public static IServiceCollection AddIntegrationsEventHandlers(this IServiceCollection services)
    {
        var handlerInterface = typeof(Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<>);
        foreach (var type in typeof(IIntegrationsUnitOfWork).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface))
            {
                services.AddScoped(contract, type);
            }
        }

        return services;
    }

    /// <summary>API host'u: API anahtarı doğrulama, kullanım tamponu, önbellek geçersiz kılma ve izin dekoratörü.</summary>
    public static IServiceCollection AddIntegrationsApiKeyServices(this IServiceCollection services)
    {
        services.AddSingleton<ApiKeyFailureThrottle>();
        services.AddSingleton<ApiKeyLastUsedTracker>();
        services.AddSingleton<ApiKeyUsageBuffer>();
        services.Replace(ServiceDescriptor.Singleton<IApiKeyUsageSink>(sp => sp.GetRequiredService<ApiKeyUsageBuffer>()));
        services.AddScoped<ApiKeyUsageStore>();
        services.AddScoped<IApiKeyCacheInvalidator, ApiKeyCacheInvalidator>();
        services.Replace(ServiceDescriptor.Scoped<IApiKeyAuthenticator, ApiKeyAuthenticator>());
        services.AddSingleton<IApiKeyScopeCatalog>(sp => new ApiKeyScopeCatalog(sp.GetRequiredService<IReadOnlyList<Sense.Crm.Shared.Contracts.Modules.IModule>>()));
        services.AddScoped<IApiKeyReadStore, ApiKeyReadStore>();
        services.AddScoped<IWebhookReadStore, WebhookReadStore>();
        services.AddScoped<IIntegrationsStats, IntegrationsStats>();
        DecoratePermissionService(services);
        return services;
    }

    private static bool EncryptionKeyIsUsable(IntegrationsOptions options)
    {
        var current = string.IsNullOrWhiteSpace(options.Encryption.CurrentKeyId) ? EncryptionOptions.DefaultKeyId : options.Encryption.CurrentKeyId;
        var configured = options.Encryption.Keys.TryGetValue(current, out var value) && !string.IsNullOrWhiteSpace(value);
        if (!configured)
        {
            return options.DevelopmentLike;
        }

        return IntegrationsEncryptionKeys.TryDecode(value!, out _);
    }

    /// <summary>
    /// <c>IPermissionService</c>'i (Identity kaydı) <see cref="ApiKeyScopedPermissionService"/> ile sarar: anahtar isteklerinde kapsam ∩ oluşturanın izinleri. JWT isteklerinde iç servisi döner.
    /// Identity modülü <b>önce</b> kaydedilmiş olmalıdır (ModuleCatalog sırası).
    /// </summary>
    private static void DecoratePermissionService(IServiceCollection services)
    {
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IPermissionService))
            ?? throw new InvalidOperationException("Integrations must be registered after Identity: no IPermissionService to decorate.");
        services.Remove(inner);
        services.Add(ServiceDescriptor.Describe(
            typeof(IPermissionService),
            sp => new ApiKeyScopedPermissionService(Resolve(sp, inner), sp.GetRequiredService<ICurrentUser>()),
            inner.Lifetime));

        static IPermissionService Resolve(IServiceProvider sp, ServiceDescriptor descriptor) =>
            descriptor switch
            {
                { ImplementationInstance: IPermissionService instance } => instance,
                { ImplementationFactory: { } factory } => (IPermissionService)factory(sp),
                { ImplementationType: { } type } => (IPermissionService)ActivatorUtilities.CreateInstance(sp, type),
                _ => throw new InvalidOperationException("Cannot resolve the IPermissionService to decorate."),
            };
    }
}

/// <summary>
/// M7 kullanım ölçümü: yalnız <c>COUNT</c>, kişisel veri yok, <b>kiracı kapsamında</b>. Anahtarlar: <c>integrations.webhooks</c> (tüm abonelikler, pasifler dahil), <c>integrations.api_keys</c> (<b>aktif</b>: iptal edilmemiş ve
/// süresi dolmamış), <c>integrations.records</c> (toplam: abonelikler + tüm anahtar satırları).
/// </summary>
public sealed class IntegrationsUsageReporter(IntegrationsDbContext db, TimeProvider clock) : IUsageReporter
{
    public const string WebhooksKey = "integrations.webhooks";
    public const string ApiKeysKey = "integrations.api_keys";

    public string Module => IntegrationsDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var webhooks = await db.WebhookSubscriptions.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var allKeys = await db.ApiKeys.AsNoTracking().LongCountAsync(ct).ConfigureAwait(false);
        var activeKeys = await db.ApiKeys.AsNoTracking().LongCountAsync(k => k.RevokedAt == null && k.ExpiresAt > now, ct).ConfigureAwait(false);
        return
        [
            new UsageMetric(WebhooksKey, webhooks),
            new UsageMetric(ApiKeysKey, activeKeys),
            new UsageMetric(UsageKeys.Records(Module), webhooks + allKeys),
        ];
    }
}

/// <summary>
/// KVKK imhası (M8B): küresel <c>delivery_queue</c> genel adıma girmez (<c>ITenantEntity</c> değil) → bu adım kiracının bekleyen teslimat satırlarını siler (<b>Order 90</b>: teslimat/abonelik tablolarından önce, uçuştaki
/// teslimat için kuyruk satırı kalmaz). Ham SQL envanterinde listeli; kiracı kimliği daima parametre; idempotent.
/// </summary>
public sealed class IntegrationsQueueEraser(IntegrationsDbContext db) : ITenantDataEraser
{
    public string Name => "module:integrations:delivery_queue";

    public int Order => 90;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        var deleted = await db.Database.ExecuteSqlAsync($"DELETE FROM integrations.delivery_queue WHERE tenant_id = {tenantId}", ct).ConfigureAwait(false);
        return new EraseReport(new Dictionary<string, long> { ["integrations.delivery_queue"] = deleted });
    }
}

/// <summary>
/// Saklama (günlük, Worker): tüm kiracılarda (kiracı kapsamında, filtre atlamadan) süresi dolmuş <c>previous_secret_*</c> temizlenir; teslimat + deneme satırları <c>DeliveryRetentionDays</c> (30) sonra,
/// <c>api_key_usage_daily</c> <c>UsageRetentionDays</c> (180) sonra silinir.
/// </summary>
public sealed partial class IntegrationsRetention(
    IntegrationsDbContext db,
    ITenantDirectory tenants,
    ITenantContextSetter tenantSetter,
    IOptions<IntegrationsOptions> options,
    TimeProvider clock,
    ILogger<IntegrationsRetention> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var deliveryCutoff = now.AddDays(-options.Value.Webhooks.DeliveryRetentionDays);
        var usageCutoff = DateOnly.FromDateTime(now.AddDays(-options.Value.ApiKeys.UsageRetentionDays));
        var total = 0;
        foreach (var tenant in await tenants.ListAllAsync(ct).ConfigureAwait(false))
        {
            using var scope = tenantSetter.BeginScope(tenant.Id);
            db.ChangeTracker.Clear();
            var expired = await db.WebhookSubscriptions
                .Where(s => s.PreviousSecretEnc != null && s.PreviousSecretExpiresAt != null && s.PreviousSecretExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.PreviousSecretEnc, (byte[]?)null).SetProperty(x => x.PreviousSecretKeyId, (string?)null).SetProperty(x => x.PreviousSecretExpiresAt, (DateTime?)null), ct)
                .ConfigureAwait(false);
            var deliveries = await db.WebhookDeliveries.Where(d => d.CreatedAt < deliveryCutoff && d.Status != "pending" && d.Status != "delivering").ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var usage = await db.ApiKeyUsageDays.Where(u => u.Day < usageCutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            total += expired + deliveries + usage;
        }

        LogRetention(logger, total);
        return total;
    }

    [LoggerMessage(EventId = 8400, Level = LogLevel.Information, Message = "Integrations retention pass changed {Rows} rows")]
    private static partial void LogRetention(ILogger logger, int rows);
}

/// <summary>Anahtar döndürme sonucu.</summary>
public sealed record ReencryptResult(int Scanned, int Rewritten, string KeyId);

/// <summary>
/// Webhook sırlarını mevcut şifreleme anahtarıyla yeniden şifreler (Migrator <c>reencrypt-integration-secrets</c>): her kiracıda (kiracı kapsamında, filtre atlamadan) eski anahtarla çözer, AYNI sürüm/AAD ile
/// mevcut anahtarla yazar. İdempotent (mevcut anahtarlı sırlara dokunmaz); çözülemeyen sır <see cref="System.Security.Cryptography.CryptographicException"/> ile işlemi durdurur.
/// </summary>
public sealed class IntegrationsSecretReencryptor(IntegrationsDbContext db, ITenantDirectory tenants, ITenantContextSetter tenantSetter, IWebhookSecretProtector protector)
{
    public async Task<ReencryptResult> RunAsync(CancellationToken ct)
    {
        var current = protector.CurrentKeyId;
        int scanned = 0, rewritten = 0;
        foreach (var tenant in await tenants.ListAllAsync(ct).ConfigureAwait(false))
        {
            using var scope = tenantSetter.BeginScope(tenant.Id);
            db.ChangeTracker.Clear();
            var subscriptions = await db.WebhookSubscriptions
                .Where(s => s.SecretKeyId != current || (s.PreviousSecretKeyId != null && s.PreviousSecretKeyId != current))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var s in subscriptions)
            {
                scanned++;
                var newCurrent = s.SecretKeyId == current
                    ? new Sense.Crm.Modules.Integrations.Domain.Webhooks.SealedSecret(s.SecretEnc, s.SecretKeyId, s.SecretLast4)
                    : protector.Seal(protector.Open(s.SecretEnc, s.SecretKeyId, s.TenantId, s.Id, s.SecretVersion), s.TenantId, s.Id, s.SecretVersion);
                Sense.Crm.Modules.Integrations.Domain.Webhooks.SealedSecret? newPrevious = null;
                if (s.PreviousSecretEnc is not null && s.PreviousSecretKeyId is not null)
                {
                    newPrevious = s.PreviousSecretKeyId == current
                        ? new Sense.Crm.Modules.Integrations.Domain.Webhooks.SealedSecret(s.PreviousSecretEnc, s.PreviousSecretKeyId, string.Empty)
                        : protector.Seal(protector.Open(s.PreviousSecretEnc, s.PreviousSecretKeyId, s.TenantId, s.Id, s.PreviousSecretVersion), s.TenantId, s.Id, s.PreviousSecretVersion);
                }

                s.ReplaceSealedSecrets(newCurrent, newPrevious);
                rewritten++;
            }

            if (subscriptions.Count > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        return new ReencryptResult(scanned, rewritten, current);
    }
}
