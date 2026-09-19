using Crm.Modules.Marketing.Application;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Modules.Marketing.Infrastructure;

/// <summary>
/// Marketing'in çalışma zamanı kayıtları (handler taraması hariç): UnitOfWork ve depolar. Hem API host'u (<c>MarketingModule</c>) hem de
/// Worker (<c>LeadConverted</c> tüketicisi) aynı kaydı kullanır; Worker Application assembly'sini taramadığı için burada toplanır.
/// Marketing başka modüllere Contracts arayüzü sunmaz.
/// </summary>
public static class MarketingContractServices
{
    public static IServiceCollection AddMarketingContractServices(this IServiceCollection services)
    {
        services.AddScoped<IMarketingUnitOfWork>(sp => sp.GetRequiredService<MarketingDbContext>());
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<ICampaignMemberRepository, CampaignMemberRepository>();
        return services;
    }
}
