using Crm.Modules.Sales.Contracts;
using Crm.Modules.Sales.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Sales.Infrastructure;

/// <summary>
/// Sales'in başka modüllere sunduğu Contracts arayüzlerinin uygulamaları. Hem API host'u (<c>SalesModule</c>) hem de Worker
/// (Conductor görev işleyicileri, olay tüketicileri) aynı kaydı kullanır; Worker Sales.Application'ı taramadığı için burada toplanır.
/// </summary>
public static class SalesContractServices
{
    public static IServiceCollection AddSalesContractServices(this IServiceCollection services)
    {
        services.AddScoped<IRecordLookup, RecordLookup>();
        services.AddScoped<ILeadOwnerService, LeadOwnerService>();
        services.AddScoped<IContactAccountLookup, ContactAccountLookup>();
        return services;
    }
}
