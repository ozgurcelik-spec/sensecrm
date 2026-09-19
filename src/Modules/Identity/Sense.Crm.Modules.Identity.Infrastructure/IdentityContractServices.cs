using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;

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
        return services;
    }
}
