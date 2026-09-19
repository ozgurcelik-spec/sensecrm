using System.Reflection;
using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Application.Auth;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Infrastructure;
using Crm.Modules.Identity.Infrastructure.Persistence;
using Crm.Modules.Identity.Infrastructure.Security;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Modules;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Crm.Modules.Identity.Api;

/// <summary>
/// Identity modülü kompozisyon kökü: organizasyon (kiracı) kaydı ve üyelik, kullanıcı hesapları, RBAC, JWT oturumları,
/// denetim kaydı okuma. İzin kataloğuna <c>org.*</c> ve modüller arası ortak <c>crm.reports.read</c> anahtarını katar; <c>crm.accounts/contacts/leads/deals</c> Sales, <c>crm.activities</c> Activities modülündedir.
/// </summary>
public sealed class IdentityModule : IModule
{
    public string Name => IdentityDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(IdentityModule).Assembly,
        typeof(IdentityOptions).Assembly,
        typeof(IdentityDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => OrgPermissions.All.Concat(CrmPermissions.All);

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtIssuerOptions>().Bind(configuration.GetSection(ConfigurationSections.Auth));
        services.AddIdentityProvisioning(configuration);

        // Herkese açık kayıt: ayar boşsa Development/Testing = açık, diğer ortamlar (Production) = kapalı. Geçersiz değer açılışı durdurur.
        services.AddOptions<RegistrationOptions>()
            .Bind(configuration.GetSection(ConfigurationSections.Registration))
            .Validate(o => RegistrationModes.IsValid(o.Mode), "Registration:Mode must be 'open' or 'disabled'.")
            .ValidateOnStart();
        services.AddSingleton<IRegistrationPolicy>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            return RegistrationPolicy.Resolve(sp.GetRequiredService<IOptions<RegistrationOptions>>().Value, env.IsDevelopment() || env.IsEnvironment("Testing"));
        });

        services.AddModuleDbContext<IdentityDbContext>(configuration, IdentityDbContext.SchemaName);
        services.AddModuleHandlers(
            IdentityDbContext.SchemaName,
            typeof(IdentityOptions).Assembly,
            typeof(IdentityDbContext).Assembly,
            typeof(IUserRepository).Assembly,
            typeof(OrgPermissions).Assembly);

        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IIdentityReadStore, IdentityReadStore>();
        services.AddIdentityContractServices();
        services.AddScoped<TenantCalendarService>();
        services.AddScoped<SessionIssuer>();

        services.AddScoped<PermissionService>();
        services.AddScoped<IPermissionService>(sp => sp.GetRequiredService<PermissionService>());
        services.AddScoped<IPermissionCacheInvalidator>(sp => sp.GetRequiredService<PermissionService>());
        services.AddSingleton<IPermissionCatalog>(sp => new PermissionCatalog(sp.GetRequiredService<IReadOnlyList<IModule>>()));

        // Mevcut organizasyonların sistem rollerine yeni izin anahtarlarını taşır (API başlangıcı).
        services.AddScoped<SystemRolePermissionSynchronizer>();
        services.AddHostedService<SystemRolePermissionSyncHostedService>();

        services.AddScoped<ITokenService, JwtTokenService>();
    }
}
