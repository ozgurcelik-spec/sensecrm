using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Application.Provisioning;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Infrastructure.Persistence;
using Crm.Modules.Identity.Infrastructure.Security;
using Crm.Shared.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Identity.Infrastructure;

/// <summary>
/// Organizasyon/kullanıcı açılışı için gereken ortak kayıtlar (repository'ler, parola/gizli değer servisleri, provisioner). API host'u
/// (<c>IdentityModule</c>) ve Migrator (<c>create-platform-admin</c>) aynı kaydı kullanır. <c>IPermissionCatalog</c> çağıranın sorumluluğundadır
/// (API: modül kataloğundan; Migrator: tüm modüllerin <c>Contracts</c> izin listelerinden).
/// </summary>
public static class IdentityProvisioningServices
{
    public static IServiceCollection AddIdentityProvisioning(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IdentityOptions>().Bind(configuration.GetSection(ConfigurationSections.Identity)).ValidateOnStart();

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IIdentityUnitOfWork>(sp => sp.GetRequiredService<IdentityDbContext>());

        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<ISecretGenerator, SecretGenerator>();

        services.AddScoped<OrganizationProvisioner>();
        services.AddScoped<PlatformAdminBootstrapper>();
        return services;
    }
}
