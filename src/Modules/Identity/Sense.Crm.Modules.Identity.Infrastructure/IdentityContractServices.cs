using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Identity.Infrastructure;

/// <summary>
/// Identity'nin başka modüllere sunduğu Contracts arayüzlerinin uygulamaları. Hem API host'u (<c>IdentityModule</c>) hem de
/// Worker (Conductor görev işleyicileri) aynı kaydı kullanır.
/// </summary>
public static class IdentityContractServices
{
    public static IServiceCollection AddIdentityContractServices(this IServiceCollection services)
    {
        services.AddScoped<IMemberLookup, MemberLookup>();
        services.AddScoped<IRoleMemberLookup, RoleMemberLookup>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();

        // M7: kullanım ölçümü (Platform anlık görüntü işi/limit denetimi) ve KVKK imha adımları (Worker/Migrator).
        services.AddScoped<IUsageReporter, IdentityUsageReporter>();
        services.AddScoped<ITenantDataEraser, IdentityAccountEraser>();
        services.AddScoped<ITenantDataEraser, IdentityTenantEraser>();

        // C-SEC2 (H1/M6): platform yöneticisi dizini (kiracı koruma kuralı) ve yaşam döngüsü (geri alma); API, Worker ve Migrator kullanır.
        services.AddScoped<IPlatformAdminDirectory, PlatformAdminDirectory>();
        services.AddScoped<IPlatformAdminManager, PlatformAdminManager>();
        return services;
    }
}
