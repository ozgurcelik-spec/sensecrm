using System.Reflection;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Application;
using Crm.Modules.Sales.Contracts;
using Crm.Modules.Sales.Domain;
using Crm.Modules.Sales.Infrastructure.Persistence;
using Crm.Modules.Sales.Infrastructure.Provisioning;
using Crm.Shared.Contracts.Modules;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Sales.Api;

/// <summary>
/// Sales modülü kompozisyon kökü: firma, kişi, potansiyel müşteri (lead) + dönüştürme, satış hunisi (pipeline) ve fırsat (deal).
/// <c>crm.accounts/contacts/leads/deals.*</c> izinlerini kataloğa katar (Milestone 1'de Identity.Contracts'taydı).
/// </summary>
public sealed class SalesModule : IModule
{
    public string Name => SalesDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(SalesModule).Assembly,
        typeof(OwnerResolver).Assembly,
        typeof(SalesPermissions).Assembly,
        typeof(SalesDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => SalesPermissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SalesDbContext>(configuration, SalesDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox) için; Application handler'lar ve OrganizationCreated tüketicisi için.
        services.AddModuleHandlers(
            SalesDbContext.SchemaName,
            typeof(OwnerResolver).Assembly,
            typeof(SalesDbContext).Assembly,
            typeof(IAccountRepository).Assembly,
            typeof(SalesPermissions).Assembly);

        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IContactRepository, ContactRepository>();
        services.AddScoped<ILeadRepository, LeadRepository>();
        services.AddScoped<IPipelineRepository, PipelineRepository>();
        services.AddScoped<IDealRepository, DealRepository>();
        services.AddScoped<ISalesReadStore, SalesReadStore>();

        services.AddScoped<OwnerResolver>();
        services.AddScoped<DefaultPipelineResolver>();
        services.AddScoped<IDefaultPipelineSeeder, DefaultPipelineSeeder>();
        services.AddSingleton<IAuditEntityPermissions, SalesAuditEntityPermissions>();

        // Mevcut (M1'de açılmış) organizasyonlara varsayılan huniyi tohumlar (API başlangıcı, idempotent).
        services.AddHostedService<DefaultPipelineSyncHostedService>();
    }
}

/// <summary>Sales varlık türlerinin kayıt bazlı denetim okuma izinleri (<c>GET /audit?entityType&amp;entityId</c>).</summary>
public sealed class SalesAuditEntityPermissions : IAuditEntityPermissions
{
    public IReadOnlyDictionary<string, string> ReadPermissionsByEntityType => SalesAuditEntities.ReadPermissions;
}
