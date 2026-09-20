using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Application.Provisioning;
using Sense.Crm.Modules.Platform.Infrastructure.Entitlements;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Platform.Infrastructure;

/// <summary>
/// Platform'un çalışma zamanı kayıtları (handler taraması hariç): seçenekler, depolar, <c>ITenantEntitlements</c>/<c>ILimitGuard</c>/<c>IPlanCatalog</c> gerçek uygulamaları
/// (<c>AddCrmCore</c>'un "her şey açık" varsayılanlarını <b>Replace</b> eder), kullanım sayacı, işler ve imha adımları. API host'u (<c>PlatformModule</c>), Worker ve
/// Migrator aynı kaydı kullanır; <c>AddModuleDbContext&lt;PlatformDbContext&gt;</c> çağıranındır.
/// </summary>
public static class PlatformContractServices
{
    public static IServiceCollection AddPlatformContractServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PlatformOptions>()
            .Bind(configuration.GetSection(PlatformOptions.SectionName))
            .Validate(o => PlanCatalogValidator.ValidateRanges(o).Count == 0, "Platform:* ranges are inconsistent (see PlanCatalogValidator.ValidateRanges).")
            .ValidateOnStart();

        services.AddScoped<IPlatformUnitOfWork>(sp => sp.GetRequiredService<PlatformDbContext>());
        services.AddScoped<IPlanRepository, PlanRepository>();
        services.AddScoped<ITenantAccountRepository, TenantAccountRepository>();
        services.AddScoped<IDeletionRequestRepository, DeletionRequestRepository>();
        services.AddScoped<IPlatformAudit, PlatformAudit>();
        services.AddScoped<IPlatformReadStore, PlatformReadStore>();
        services.AddScoped<IUsageExportWriter, UsageExportWriter>();
        services.AddScoped<IUsageMeter, UsageMeter>();
        services.AddScoped<IUsageSnapshotWriter, UsageSnapshotWriter>();
        services.AddScoped<IEntitlementCache, EntitlementCache>();
        services.AddScoped<AccountProvisioner>();
        services.AddScoped<PlanSynchronizer>();
        services.AddScoped<AccountBackfill>();
        services.AddScoped<DeletedTenantsReplay>();

        // Gerçek zorlama uygulamaları: AddCrmCore'un TryAdd varsayılanlarının yerine.
        services.Replace(ServiceDescriptor.Scoped<ITenantEntitlements, TenantEntitlementsService>());
        services.Replace(ServiceDescriptor.Scoped<ILimitGuard, LimitGuard>());
        services.Replace(ServiceDescriptor.Scoped<IPlanCatalog, PlanCatalog>());
        services.Replace(ServiceDescriptor.Scoped<IPlatformAuditSink, PlatformAuditSink>());

        // Platform'un kendi işleri (Worker BackgroundService sarmalayıcılarıyla, testler doğrudan çağırır).
        services.AddSingleton<UsageSnapshotJob>();
        services.AddSingleton<TenantErasureJob>();
        services.AddScoped<ITenantDataEraser, AuditTenantDataEraser>();
        return services;
    }
}
