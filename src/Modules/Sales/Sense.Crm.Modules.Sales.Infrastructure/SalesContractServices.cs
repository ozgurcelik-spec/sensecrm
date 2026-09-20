using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Sales.Infrastructure;

/// <summary>
/// Sales'in başka modüllere sunduğu Contracts arayüzlerinin uygulamaları. Hem API host'u (<c>SalesModule</c>) hem de Worker
/// (Conductor görev işleyicileri, olay tüketicileri) aynı kaydı kullanır; Worker Sales.Application'ı taramadığı için burada toplanır.
/// </summary>
public static class SalesContractServices
{
    public static IServiceCollection AddSalesContractServices(this IServiceCollection services)
    {
        services.AddScoped<IRecordLookup, RecordLookup>();
        services.AddScoped<IRecordRelationLookup, RecordRelationLookup>();
        services.AddScoped<ILeadOwnerService, LeadOwnerService>();
        services.AddScoped<ILeadStatusLookup, LeadStatusLookup>();
        services.AddScoped<IContactAccountLookup, ContactAccountLookup>();
        services.AddScoped<Sense.Crm.Shared.Contracts.Usage.IUsageReporter, SalesUsageReporter>();
        return services;
    }
}
