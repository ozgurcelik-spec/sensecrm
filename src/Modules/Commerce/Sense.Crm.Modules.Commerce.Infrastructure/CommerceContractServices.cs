using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Commerce.Infrastructure;

/// <summary>
/// Commerce'in başka bileşenlere sunduğu kayıtlar (M7): <c>IUsageReporter</c> (Platform kullanım anlık görüntüsü ve limit denetimi). Hem API host'u hem de Worker aynı kaydı kullanır.
/// </summary>
public static class CommerceContractServices
{
    public static IServiceCollection AddCommerceContractServices(this IServiceCollection services)
    {
        services.AddScoped<IUsageReporter, CommerceUsageReporter>();
        return services;
    }
}
