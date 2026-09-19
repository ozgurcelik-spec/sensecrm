using System.Net;
using Crm.Shared.Contracts.Configuration;
using Microsoft.AspNetCore.HttpOverrides;

namespace Crm.Api.Middleware;

/// <summary>
/// Ters vekil (ör. web konteynerindeki nginx) arkasında istemci IP'si ve şema <c>X-Forwarded-*</c> başlıklarından okunur; aksi hâlde
/// IP bazlı hız sınırı tüm kullanıcıları tek vekil adresine toplar, oturum/denetim kayıtlarında IP yanlış olur. Güvenlik gereği
/// <b>yapılandırılmadıkça kapalıdır</b> ve yalnız listelenen vekillerden gelen başlıklara güvenilir (istemci başlığı taklit edemez):
/// <c>ForwardedHeaders:Enabled=true</c> + <c>ForwardedHeaders:KnownProxies</c> (IP dizisi) ve/veya <c>ForwardedHeaders:KnownNetworks</c> (CIDR dizisi).
/// Etkinken ikisi de boşsa yalnız loopback'e güvenilir (ASP.NET varsayılanı).
/// </summary>
public static class ForwardedHeadersSetup
{
    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>($"{ConfigurationSections.ForwardedHeaders}:Enabled");

    public static IServiceCollection AddCrmForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return services;
        }

        var section = configuration.GetSection(ConfigurationSections.ForwardedHeaders);
        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;

            if (proxies.Length == 0 && networks.Length == 0)
            {
                return; // yalnız loopback (varsayılan)
            }

            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (var proxy in proxies.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                o.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
            }

            foreach (var network in networks.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network.Trim()));
            }
        });

        return services;
    }
}
