using System.Reflection;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Marketing.Application;
using Crm.Modules.Marketing.Contracts;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Infrastructure;
using Crm.Modules.Marketing.Infrastructure.Persistence;
using Crm.Shared.Contracts.Modules;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Marketing.Api;

/// <summary>
/// Marketing modülü kompozisyon kökü: kampanyalar, kampanya üyeleri (lead/kişi; Sales'e yumuşak bağ,
/// <c>Sales.Contracts</c> ile doğrulanır), metrikler, pazarlama raporu ve <c>LeadConverted</c> tüketicisi.
/// <c>crm.campaigns.*</c> izinlerini kataloğa katar.
/// </summary>
public sealed class MarketingModule : IModule
{
    public string Name => MarketingDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(MarketingModule).Assembly,
        typeof(OwnerResolver).Assembly,
        typeof(MarketingPermissions).Assembly,
        typeof(MarketingDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => MarketingPermissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<MarketingDbContext>(configuration, MarketingDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox) için.
        services.AddModuleHandlers(
            MarketingDbContext.SchemaName,
            typeof(OwnerResolver).Assembly,
            typeof(MarketingDbContext).Assembly,
            typeof(ICampaignRepository).Assembly,
            typeof(MarketingPermissions).Assembly);

        services.AddMarketingContractServices();
        services.AddScoped<IMarketingReadStore, MarketingReadStore>();
        services.AddScoped<IMarketingReportStore, MarketingReportStore>();

        services.AddScoped<OwnerResolver>();
        services.AddSingleton<IAuditEntityPermissions, MarketingAuditEntityPermissions>();
    }
}

/// <summary>Marketing varlık türlerinin kayıt bazlı denetim okuma izinleri (<c>GET /audit?entityType&amp;entityId</c>).</summary>
public sealed class MarketingAuditEntityPermissions : IAuditEntityPermissions
{
    public IReadOnlyDictionary<string, string> ReadPermissionsByEntityType => MarketingAuditEntities.ReadPermissions;
}
