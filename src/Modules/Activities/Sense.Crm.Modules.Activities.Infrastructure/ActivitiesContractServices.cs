using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Activities.Contracts;
using Sense.Crm.Modules.Activities.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Activities.Infrastructure;

/// <summary>
/// Activities'in başka modüllere sunduğu Contracts arayüzlerinin uygulamaları. Hem API host'u (<c>ActivitiesModule</c>) hem de
/// Worker (Conductor görev işleyicileri) aynı kaydı kullanır.
/// </summary>
public static class ActivitiesContractServices
{
    public static IServiceCollection AddActivitiesContractServices(this IServiceCollection services)
    {
        services.AddScoped<IActivityCreator, ActivityCreator>();
        services.AddScoped<Sense.Crm.Shared.Contracts.Usage.IUsageReporter, ActivitiesUsageReporter>();

        // M8C: dosya eki hedefi (aktivite).
        services.AddScoped<Sense.Crm.Shared.Contracts.Files.IAttachmentTarget, ActivityAttachmentTarget>();
        return services;
    }
}
