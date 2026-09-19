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
        services.AddOptions<IdentityOptions>().Bind(configuration.GetSection(ConfigurationSections.Identity)).ValidateOnStart();
        services.AddOptions<JwtIssuerOptions>().Bind(configuration.GetSection(ConfigurationSections.Auth));

        services.AddModuleDbContext<IdentityDbContext>(configuration, IdentityDbContext.SchemaName);
        services.AddModuleHandlers(
            IdentityDbContext.SchemaName,
            typeof(IdentityOptions).Assembly,
            typeof(IdentityDbContext).Assembly,
            typeof(IUserRepository).Assembly,
            typeof(OrgPermissions).Assembly);
        services.AddScoped<IIdentityUnitOfWork>(sp => sp.GetRequiredService<IdentityDbContext>());

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
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

        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<ISecretGenerator, SecretGenerator>();
        services.AddScoped<ITokenService, JwtTokenService>();
    }
}
