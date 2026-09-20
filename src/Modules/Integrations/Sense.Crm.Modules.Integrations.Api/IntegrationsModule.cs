using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.OpenApi;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Infrastructure;
using Sense.Crm.Modules.Integrations.Infrastructure.Persistence;
using Sense.Crm.Modules.Integrations.Infrastructure.Security;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Modules;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;

namespace Sense.Crm.Modules.Integrations.Api;

/// <summary>
/// Integrations modülü kompozisyon kökü (M8B): giden webhook'lar (imzalı, yeniden denemeli, SSRF korumalı), API anahtarları (makine kimliği), teslimat günlüğü ve kiracıya süzülmüş OpenAPI belgesi.
/// Yalnız <c>*.Contracts</c> (Identity, Sales, Commerce, Service) + outbox integration event'leriyle konuşur; <b>hiçbir modül Integrations'a bağlanmaz</b>. <c>org.integrations.manage</c> izni kataloğa katılır.
/// <c>integrations</c> M7 kapı modülüdür (plan bayrağı); dış çağrıyı yalnız Worker yapar. Identity'den <b>sonra</b> kaydedilmelidir (izin servisi dekoratörü).
/// </summary>
public sealed class IntegrationsModule : IModule
{
    public string Name => IntegrationsDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(IntegrationsModule).Assembly,
        typeof(IIntegrationsUnitOfWork).Assembly,
        typeof(IntegrationsPermissions).Assembly,
        typeof(IntegrationsDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => IntegrationsPermissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<IntegrationsDbContext>(configuration, IntegrationsDbContext.SchemaName);

        // Application + Domain + Contracts assembly'leri: handler/validator taraması ve olay tipi kaydı; fan-out işleyicileri (IIntegrationEventHandler<>) Application'dadır.
        services.AddModuleHandlers(
            IntegrationsDbContext.SchemaName,
            typeof(IIntegrationsUnitOfWork).Assembly,
            typeof(IntegrationsDbContext).Assembly,
            typeof(IWebhookSubscriptionRepository).Assembly,
            typeof(IntegrationsPermissions).Assembly);

        services.AddIntegrationsContractServices(configuration);
        services.AddIntegrationsApiKeyServices();

        services.AddSingleton<IOpenApiBaseDocumentSource, OpenApiBaseDocumentSource>();
        services.AddScoped<IOpenApiDocumentComposer, OpenApiDocumentComposer>();
        services.AddHostedService<ApiKeyUsageFlushService>();
    }
}

/// <summary>Kiracı+plana göre süzülmüş belgeyi üretir: temel belge süreç başına bir kez; süzülmüş sonuç <c>Integrations:OpenApi:CacheMinutes</c> (10 dk) kiracı + açık modül kümesi bazında önbelleklenir.</summary>
public sealed class OpenApiDocumentComposer(IOpenApiBaseDocumentSource source, IMemoryCache cache, IOptions<IntegrationsOptions> options) : IOpenApiDocumentComposer
{
    public async Task<string> ComposeAsync(Guid tenantId, EntitlementSnapshot snapshot, CancellationToken ct)
    {
        var modules = string.Join(',', new[] { GatedModules.Commerce, GatedModules.Service, GatedModules.Marketing }.Where(snapshot.IsModuleEnabled));
        var key = $"openapi:{tenantId:N}:{modules}";
        if (cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            return cached;
        }

        var filtered = OpenApiDocumentFilter.Filter(await source.GetJsonAsync(ct).ConfigureAwait(false), snapshot.IsModuleEnabled);
        cache.Set(key, filtered, TimeSpan.FromMinutes(Math.Max(options.Value.OpenApi.CacheMinutes, 0)));
        return filtered;
    }
}

/// <summary>API anahtarı kullanım sayaçlarını periyodik (<c>UsageFlushSeconds</c>) olarak <c>upsert</c> eder; kapanışta son kez boşaltır. Yazılamayan sayaçlar geri konur (kayıp ≤ bir aralık).</summary>
public sealed partial class ApiKeyUsageFlushService(IServiceScopeFactory scopes, ApiKeyUsageBuffer buffer, IOptions<IntegrationsOptions> options, ILogger<ApiKeyUsageFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.ApiKeys.UsageFlushSeconds);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Kapanış.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var entries = buffer.Drain();
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ApiKeyUsageStore>().UpsertAsync(entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            buffer.Restore(entries);
            LogFlushFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 8500, Level = LogLevel.Warning, Message = "API key usage flush failed; counters restored for the next attempt")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);
}
