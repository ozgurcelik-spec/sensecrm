using Crm.Modules.Identity.Contracts;
using Crm.Modules.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Identity.Infrastructure;

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
        return services;
    }
}
