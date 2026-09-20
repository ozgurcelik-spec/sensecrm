using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Service.Infrastructure;

/// <summary>
/// Service'in başka bileşenlere sunduğu kayıtlar (M7): <c>IUsageReporter</c> (Platform kullanım anlık görüntüsü ve limit denetimi). Hem API host'u hem de Worker aynı kaydı kullanır.
/// </summary>
public static class ServiceContractServices
{
    public static IServiceCollection AddServiceContractServices(this IServiceCollection services)
    {
        services.AddScoped<IUsageReporter, ServiceUsageReporter>();
        return services;
    }
}
