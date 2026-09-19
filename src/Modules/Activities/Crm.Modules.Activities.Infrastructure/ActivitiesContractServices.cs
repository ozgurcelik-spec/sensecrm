using Crm.Modules.Activities.Contracts;
using Crm.Modules.Activities.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Activities.Infrastructure;

/// <summary>
/// Activities'in başka modüllere sunduğu Contracts arayüzlerinin uygulamaları. Hem API host'u (<c>ActivitiesModule</c>) hem de
/// Worker (Conductor görev işleyicileri) aynı kaydı kullanır.
/// </summary>
public static class ActivitiesContractServices
{
    public static IServiceCollection AddActivitiesContractServices(this IServiceCollection services)
    {
        services.AddScoped<IActivityCreator, ActivityCreator>();
        return services;
    }
}
