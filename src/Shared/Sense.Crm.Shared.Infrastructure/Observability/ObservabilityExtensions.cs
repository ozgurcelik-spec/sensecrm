using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Sense.Crm.Shared.Contracts.Observability;
using Sense.Crm.Shared.Infrastructure.Events;

namespace Sense.Crm.Shared.Infrastructure.Observability;

/// <summary>
/// Prometheus metrik uç noktası ayarları (<c>Observability:Metrics</c>, C-OPS1/K20). Uç nokta varsayılan olarak <b>kapalıdır</b> ve açıldığında
/// ana uygulama portundan <b>ayrı</b> bir dinleyicide (varsayılan 9464) yalnız <see cref="Path"/> yolunu sunar: nginx/web tarafından vekillenmez,
/// hiçbir portu yayınlanmaz; koruma ağ yalıtımı (compose <c>internal</c> ağı) + isteğe bağlı bearer belirteçtir.
/// </summary>
public sealed class MetricsEndpointOptions
{
    public const string SectionName = "Observability:Metrics";

    /// <summary>Açık değilse ne ihraççı ne dinleyici kurulur (sıfır maliyet).</summary>
    public bool Enabled { get; set; }

    /// <summary>Dinleme adresi. Varsayılan yalnız döngü (<c>127.0.0.1</c>): yanlışlıkla ağa açılmaz. Konteynerde <c>0.0.0.0</c> (overlay ayarlar).</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9464;

    public string Path { get; set; } = "/metrics";

    /// <summary>Doluysa <c>Authorization: Bearer &lt;değer&gt;</c> zorunlu olur (sabit zamanlı karşılaştırma). Sırlar dosyada tutulmalı: <see cref="BearerTokenFile"/>.</summary>
    public string? BearerToken { get; set; }

    /// <summary>Belirteci içeren dosya (Docker secret, örn. <c>/run/secrets/metrics_bearer_token</c>); <see cref="BearerToken"/> boşsa okunur.</summary>
    public string? BearerTokenFile { get; set; }

    /// <summary>Kazıma yanıtının önbellek süresi (ms); art arda kazımalar aynı yanıtı görür. Testlerde 0.</summary>
    public int ScrapeCacheMilliseconds { get; set; } = 1000;
}

public static class ObservabilityExtensions
{
    /// <summary>Framework'ün yerleşik Meter'ları (ek enstrümantasyon paketi gerekmez) + uygulama Meter'ları.</summary>
    internal static readonly string[] Meters =
    [
        CrmMetrics.MeterName,
        InProcessEventBus.MeterName,
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.RateLimiting",
        "System.Net.Http",
        "System.Net.NameResolution",
        "System.Runtime",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
    ];

    /// <summary>
    /// OpenTelemetry <b>metrikleri</b> (iz yok, dış ihracat yok) + ayrı bir dinleyicide Prometheus <c>/metrics</c>. <c>Observability:Metrics:Enabled=false</c>
    /// (varsayılan) iken hiçbir şey kaydedilmez. Api ve Worker aynı çağrıyı kullanır; <paramref name="serviceName"/> yalnız <c>target_info</c>'ya girer.
    /// </summary>
    public static IServiceCollection AddCrmObservability(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var options = new MetricsEndpointOptions();
        configuration.GetSection(MetricsEndpointOptions.SectionName).Bind(options);
        if (!options.Enabled)
        {
            return services;
        }

        services.AddSingleton(options);
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName,
                serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString(),
                serviceInstanceId: Environment.MachineName)) // konteyner adı; rastgele GUID yerine (yeniden başlatmada seri patlaması yok)
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(Meters);

                // HTTP sunucu süresi: yalnız düşük kardinaliteli etiketler (yol şablonu http.route; ham URL/sorgu asla). Yönlendirilemeyen istekte route yoktur.
                metrics.AddView(
                    "http.server.request.duration",
                    new ExplicitBucketHistogramConfiguration
                    {
                        TagKeys = ["http.request.method", "http.route", "http.response.status_code", "error.type", "url.scheme"],
                        Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
                    });

                // Npgsql: havuz adı bağlantı dizesinin (sunucu/veritabanı/kullanıcı, parolasız) kopyasıdır; ayrıntı sızdırmamak ve seri sayısını sabit tutmak için düşürülür.
                metrics.AddView(instrument => instrument.Meter.Name == "Npgsql"
                    ? new MetricStreamConfiguration { TagKeys = ["db.client.connection.state", "db.system.name", "error.type"] }
                    : null);

                metrics.AddPrometheusExporter(o =>
                {
                    o.ScopeInfoEnabled = false;
                    o.ScrapeResponseCacheDurationMilliseconds = options.ScrapeCacheMilliseconds;
                });
            });
        services.AddHostedService<MetricsListenerService>();
        return services;
    }

    /// <summary>Test/işletim yardımcısı: ayarlardan belirteci çözer (dosya başındaki/sonundaki boşluk atılır); yoksa <c>null</c> (kimlik doğrulama yok).</summary>
    public static string? ResolveBearerToken(this MetricsEndpointOptions options)
    {
        var token = options.BearerToken;
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(options.BearerTokenFile) && File.Exists(options.BearerTokenFile))
        {
            token = File.ReadAllText(options.BearerTokenFile);
        }

        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}

/// <summary>İç dinleyicinin Meter'larını <c>Sense.Crm.Uncollected.*</c> adıyla açar; hiçbir <c>AddMeter</c> bunları dinlemediğinden dışa aktarılmazlar.</summary>
internal sealed class UncollectedMeterFactory : System.Diagnostics.Metrics.IMeterFactory
{
    private readonly List<System.Diagnostics.Metrics.Meter> meters = [];

    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options)
    {
        var meter = new System.Diagnostics.Metrics.Meter(new System.Diagnostics.Metrics.MeterOptions("Sense.Crm.Uncollected." + options.Name)
        {
            Version = options.Version,
            Tags = options.Tags,
            Scope = this,
        });
        lock (meters)
        {
            meters.Add(meter);
        }

        return meter;
    }

    public void Dispose()
    {
        lock (meters)
        {
            foreach (var meter in meters)
            {
                meter.Dispose();
            }

            meters.Clear();
        }
    }
}

/// <summary>
/// Ana istek hattından <b>bağımsız</b> küçük bir Kestrel ana bilgisayarı: yalnız metrik yolunu sunar (AllowedHosts, hız sınırı, kimlik doğrulama,
/// yönlendirme başlıkları ana uygulamaya aittir ve burayı etkilemez), başka her yol 404 döner. Ağ yalıtımı birincil koruma, bearer belirteç ikincildir.
/// Başlatılamazsa (port dolu) uygulama çalışmaya devam eder; hata günlüğe yazılır (metrikler isteğe bağlıdır).
/// </summary>
internal sealed partial class MetricsListenerService(MetricsEndpointOptions options, MeterProvider meterProvider, ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private readonly ILogger logger = loggerFactory.CreateLogger<MetricsListenerService>();
    private WebApplication? app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // MeterProvider bu noktada kurulmuştur (yapıcı bağımlılığı): sıfır değerli serileri şimdi oluştur (bkz. CrmMetrics.Prime).
        CrmMetrics.Prime();
        try
        {
            var token = options.ResolveBearerToken();
            var tokenBytes = token is null ? null : Encoding.UTF8.GetBytes(token);
            var path = options.Path.StartsWith('/') ? options.Path : "/" + options.Path;

            var builder = WebApplication.CreateSlimBuilder();

            // İç dinleyicinin kendi günlüğü kapalıdır: her kazıma için "Request starting/finished" satırı (Worker'da 15 sn'de bir; gerçek yığında yakalandı) gürültü olurdu.
            // Başlatma başarısı/hatası yukarıdaki kendi logger'ımızla yazılır.
            builder.Logging.ClearProviders();

            // Ana uygulamanın AllowedHosts ortam değişkeni bu ana bilgisayara da sızar; Prometheus IP/hizmet adıyla kazır (Host: 10.x.x.x:9464), bu yüzden burada
            // konak süzgeci kapalıdır. Koruma: ağ yalıtımı + bearer belirteç (bu dinleyici yalnız /metrics sunar).
            builder.Configuration["AllowedHosts"] = "*";

            // Bu dinleyicinin kendi Kestrel/ASP.NET Core ölçümleri (kazıma istekleri) ana uygulamanın RED metriklerine karışmasın: Meter'lar farklı adla açılır (toplanmaz).
            builder.Services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory>(new UncollectedMeterFactory());
            builder.WebHost.UseUrls($"http://{options.BindAddress}:{options.Port}");
            builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

            var web = builder.Build();
            web.UseOpenTelemetryPrometheusScrapingEndpoint(
                meterProvider,
                predicate: context => string.Equals(context.Request.Path.Value, path, StringComparison.Ordinal),
                path: null,
                configureBranchedPipeline: branch => branch.Use(async (context, next) =>
                {
                    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                    {
                        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                        return;
                    }

                    if (tokenBytes is not null && !IsAuthorized(context.Request, tokenBytes))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        return;
                    }

                    await next().ConfigureAwait(false);
                }),
                optionsName: null);
            web.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });

            await web.StartAsync(cancellationToken).ConfigureAwait(false);
            app = web;
            LogStarted(logger, options.BindAddress, options.Port, path, tokenBytes is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex, options.BindAddress, options.Port);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (app is { } running)
        {
            await running.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (app is { } running)
        {
            await running.DisposeAsync().ConfigureAwait(false);
            app = null;
        }
    }

    private static bool IsAuthorized(HttpRequest request, byte[] expected)
    {
        const string prefix = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented), SHA256.HashData(expected));
    }

    [LoggerMessage(EventId = 1300, Level = LogLevel.Information, Message = "Metrics endpoint listening on {Address}:{Port}{Path} (bearer token required: {TokenRequired})")]
    private static partial void LogStarted(ILogger logger, string address, int port, string path, bool tokenRequired);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Error, Message = "Metrics endpoint could not start on {Address}:{Port}; the application continues without it")]
    private static partial void LogFailed(ILogger logger, Exception exception, string address, int port);
}
