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

        // M8C: dosya eki hedefleri (teklif, satış siparişi).
        services.AddScoped<Sense.Crm.Shared.Contracts.Files.IAttachmentTarget, QuoteAttachmentTarget>();
        services.AddScoped<Sense.Crm.Shared.Contracts.Files.IAttachmentTarget, OrderAttachmentTarget>();
        return services;
    }
}
