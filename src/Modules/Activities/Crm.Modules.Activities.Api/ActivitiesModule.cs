using System.Reflection;
using Crm.Modules.Activities.Application;
using Crm.Modules.Activities.Contracts;
using Crm.Modules.Activities.Domain;
using Crm.Modules.Activities.Infrastructure;
using Crm.Modules.Activities.Infrastructure.Persistence;
using Crm.Modules.Identity.Contracts;
using Crm.Shared.Contracts.Modules;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Activities.Api;

/// <summary>
/// Activities modülü kompozisyon kökü: görev, arama, toplantı ve not; firma/kişi/potansiyel/fırsata yumuşak bağ
/// (<c>Sales.Contracts.IRecordLookup</c> ile doğrulanır), kişisel iş özeti ve aktivite raporu.
/// <c>crm.activities.*</c> izinlerini kataloğa katar (Milestone 1'de Identity.Contracts'taydı).
/// </summary>
public sealed class ActivitiesModule : IModule
{
    public string Name => ActivitiesDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(ActivitiesModule).Assembly,
        typeof(AssigneeResolver).Assembly,
        typeof(ActivitiesPermissions).Assembly,
        typeof(ActivitiesDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => ActivitiesPermissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ActivitiesDbContext>(configuration, ActivitiesDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox) için.
        services.AddModuleHandlers(
            ActivitiesDbContext.SchemaName,
            typeof(AssigneeResolver).Assembly,
            typeof(ActivitiesDbContext).Assembly,
            typeof(IActivityRepository).Assembly,
            typeof(ActivitiesPermissions).Assembly);

        services.AddScoped<IActivityRepository, ActivityRepository>();
        services.AddScoped<IActivityReadStore, ActivityReadStore>();

        services.AddActivitiesContractServices();

        services.AddScoped<AssigneeResolver>();
        services.AddScoped<RelatedRecordVerifier>();
        services.AddSingleton<IAuditEntityPermissions, ActivitiesAuditEntityPermissions>();
    }
}

/// <summary>Activities varlık türlerinin kayıt bazlı denetim okuma izinleri (<c>GET /audit?entityType&amp;entityId</c>).</summary>
public sealed class ActivitiesAuditEntityPermissions : IAuditEntityPermissions
{
    public IReadOnlyDictionary<string, string> ReadPermissionsByEntityType => ActivitiesAuditEntities.ReadPermissions;
}
