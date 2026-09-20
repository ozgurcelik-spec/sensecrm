using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Service.Application;
using Sense.Crm.Modules.Service.Contracts;
using Sense.Crm.Modules.Service.Domain;
using Sense.Crm.Modules.Service.Infrastructure;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Modules.Service.Infrastructure.Provisioning;
using Sense.Crm.Shared.Contracts.Modules;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;

namespace Sense.Crm.Modules.Service.Api;

/// <summary>
/// Service (servis/destek) modülü kompozisyon kökü: numaralı talepler (durum makinesi, atama, yorumlar, zaman çizelgesi), öncelik başına SLA
/// süreleri/politikaları, servis raporları. Sales'e yalnız <c>Sales.Contracts</c> (<c>IRecordLookup</c>, <c>IContactAccountLookup</c>), Identity'ye
/// <c>Identity.Contracts</c> üzerinden bağlıdır. <c>crm.cases.*</c> izinlerini kataloğa katar.
/// </summary>
public sealed class ServiceModule : IModule
{
    public string Name => ServiceDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(ServiceModule).Assembly,
        typeof(CaseAssigneeVerifier).Assembly,
        typeof(ServicePermissions).Assembly,
        typeof(ServiceDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => ServicePermissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ServiceDbContext>(configuration, ServiceDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox: CaseResolved) için; Application handler'lar ve OrganizationCreated tüketicisi için.
        services.AddModuleHandlers(
            ServiceDbContext.SchemaName,
            typeof(CaseAssigneeVerifier).Assembly,
            typeof(ServiceDbContext).Assembly,
            typeof(ICaseRepository).Assembly,
            typeof(ServicePermissions).Assembly);

        var settings = configuration.GetSection(ServiceSettings.SectionName).Get<ServiceSettings>() ?? new ServiceSettings();
        if (settings.ReopenWindowDays < 1)
        {
            settings.ReopenWindowDays = Domain.Cases.CaseRules.ReopenWindowDays;
        }

        services.AddSingleton(settings);

        services.AddServiceContractServices();
        services.AddScoped<ICaseRepository, CaseRepository>();
        services.AddScoped<ISlaPolicyRepository, SlaPolicyRepository>();
        services.AddScoped<ICaseReadStore, CaseReadStore>();
        services.AddScoped<ICaseNumberGenerator, CaseNumberGenerator>();

        services.AddScoped<CaseAssigneeVerifier>();
        services.AddScoped<CaseLinkResolver>();
        services.AddScoped<SlaPolicyProvider>();
        services.AddScoped<IDefaultSlaPolicySeeder, DefaultSlaPolicySeeder>();
        services.AddSingleton<IAuditEntityPermissions, ServiceAuditEntityPermissions>();

        // Mevcut (M1–M6A'da açılmış) organizasyonlara varsayılan SLA politikalarını tohumlar (API başlangıcı, idempotent).
        services.AddHostedService<DefaultSlaPolicySyncHostedService>();
    }
}

/// <summary>Service varlık türlerinin kayıt bazlı denetim okuma izinleri (<c>GET /audit?entityType&amp;entityId</c>).</summary>
public sealed class ServiceAuditEntityPermissions : IAuditEntityPermissions
{
    public IReadOnlyDictionary<string, string> ReadPermissionsByEntityType => ServiceAuditEntities.ReadPermissions;
}
