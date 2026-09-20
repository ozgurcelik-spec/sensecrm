using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Application.Console;
using Sense.Crm.Modules.Platform.Contracts;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Infrastructure;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Modules;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;

namespace Sense.Crm.Modules.Platform.Api;

/// <summary>
/// Platform modülü kompozisyon kökü (M7, docs/plan/m7-saas-hazirlik.md): plan kataloğu, kiracı yaşam döngüsü (askı, deneme, silme), plan/limit
/// zorlamasının gerçek uygulamaları (<c>ITenantEntitlements</c>, <c>ILimitGuard</c>), kullanım ölçümü ve platform konsolu (<c>/platform/**</c>).
/// <b>Hiçbir modül Platform'a bağlanmaz</b> (yalnız host'lar); Platform yalnız <c>Identity.Contracts</c> + <c>Shared.*</c> kullanır. Yeni kiracı izni yoktur.
/// </summary>
public sealed class PlatformModule : IModule
{
    public string Name => PlatformDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(PlatformModule).Assembly,
        typeof(PlatformOptions).Assembly,
        typeof(TenantSuspended).Assembly,
        typeof(PlatformDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => [];

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<PlatformDbContext>(configuration, PlatformDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox) için.
        services.AddModuleHandlers(
            PlatformDbContext.SchemaName,
            typeof(PlatformOptions).Assembly,
            typeof(PlatformDbContext).Assembly,
            typeof(TenantAccount).Assembly,
            typeof(TenantSuspended).Assembly);

        services.AddPlatformContractServices(configuration);

        // C-SEC2 H2: yıkıcı komutlar için step-up (çağıranın parolası); yalnız API host'unda (Identity'nin IStepUpAuthenticator uygulaması burada kayıtlıdır).
        services.AddScoped<IStepUpGuard, StepUpGuard>();
    }
}
