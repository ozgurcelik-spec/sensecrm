using Scalar.AspNetCore;
using Sense.Crm.Api;
using Sense.Crm.Api.HealthChecks;
using Sense.Crm.Api.Middleware;
using Sense.Crm.Shared.Infrastructure.Observability;
using Sense.Crm.Shared.Web.DependencyInjection;
using Sense.Crm.Shared.Web.Middleware;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Dosya tabanlı gizli değerler (Docker/Kubernetes secret): /run/secrets/<Ad> dosyası yapılandırma anahtarı olur ("__" = ":"),
    // örn. /run/secrets/Auth__SigningKeyPem -> Auth:SigningKeyPem. Çok satırlı PEM anahtarı ortam değişkeninde taşımak yerine kullanılır.
    // Dizin yoksa (geliştirme) sessizce yok sayılır.
    builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

    // Gözlemlenebilirlik MVP'de yalnız Serilog konsol + istek logu (OTel/Sentry/Seq/Loki sonraki aşama).
    builder.Host.UseSerilog((context, services, cfg) => cfg
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.With<TenantEnricher>()
        .Enrich.With<CorrelationIdEnricher>()
        .Enrich.With(new UserEnricher(services.GetRequiredService<IHttpContextAccessor>()))
        .Enrich.WithProperty(HostConstants.ApplicationProperty, context.Configuration[HostConstants.ApplicationNameKey] ?? HostConstants.DefaultApplicationName));

    // "Server: Kestrel" başlığı sunucu teknolojisini ifşa etmesin. İstek gövdesi üst sınırı (M2; RequestLimits:MaxRequestBodyBytes,
    // varsayılan 1 MB): bu API yalnız küçük JSON alır; aşan istek Kestrel'de 413 ile kesilir.
    var maxBodyBytes = builder.Configuration.GetSection(Sense.Crm.Shared.Contracts.Configuration.ConfigurationSections.RequestLimits).Get<Sense.Crm.Shared.Contracts.Configuration.RequestLimitsOptions>()?.MaxRequestBodyBytes
        ?? Sense.Crm.Shared.Contracts.Configuration.RequestLimitsDefaults.MaxRequestBodyBytes;
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.AddServerHeader = false;
        o.Limits.MaxRequestBodySize = maxBodyBytes;
    });

    builder.Services.AddCrmWeb(builder.Configuration, ModuleCatalog.Modules);
    builder.Services.AddCrmAuthentication(builder.Configuration, builder.Environment);
    builder.Services.AddCrmHealthChecks(builder.Configuration);

    // C-OPS1 (K20): Observability:Metrics:Enabled=true ise ayrı portta (varsayılan 9464) Prometheus /metrics; ana porttan ve nginx'ten erişilemez.
    builder.Services.AddCrmObservability(builder.Configuration, "crm-api");

    builder.Services.AddCrmForwardedHeaders(builder.Configuration);

    var app = builder.Build();

    if (ForwardedHeadersSetup.IsEnabled(builder.Configuration))
    {
        app.UseForwardedHeaders();
    }

    // En erken middleware: sonraki her şey (exception handler dahil) aynı correlation id'yi görsün.
    app.UseCorrelationId();
    app.UseSecurityHeaders();
    app.UseNoStoreForAuth();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging(o =>
    {
        // Health check polling'i log gürültüsü yaratmasın.
        o.GetLevel = (httpContext, elapsedMs, ex) => ex is not null
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments(HostConstants.HealthPath)
                ? LogEventLevel.Verbose
                : httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : elapsedMs > 1000
                        ? LogEventLevel.Warning
                        : LogEventLevel.Information;
    });
    app.UseCrmLocalization();
    app.UseCrmCors();

    app.UseAuthentication();

    // M8B: API anahtarı kullanım sayacı — hız sınırlayıcıdan ÖNCE (429 yanıtları da sayılır); yalnız ApiKey kimliğiyle gelen istekleri sayar.
    app.UseApiKeyUsage();

    // Hız sınırlama kimlik doğrulamadan SONRA: anonim auth uçları IP başına ([EnableRateLimiting]), kimliği doğrulanmış tüm istekler
    // ayrıca kullanıcı ve kiracı başına genel sınırdan geçer (M2). Kullanıcı/kiracı anahtarı doğrulanmış JWT claim'lerinden okunur.
    app.UseRateLimiter();

    app.UseRequestContext();

    // Geçici parolalı hesap: parola değişene kadar yalnız [AllowWhenPasswordChangeRequired] uçlar çalışır (H4).
    app.UsePasswordChangeRequired();
    app.UseAuthorization();

    app.MapControllers();
    app.MapCrmHealthChecks();
    // API dokümantasyonu (OpenAPI + Scalar) yalnız geliştirme/test ortamında veya açıkça `Docs:Enabled=true` ile açılır (Production varsayılanı: kapalı).
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment(AuthDefaults.TestingEnvironment) || app.Configuration.GetValue<bool>(HostConstants.DocsEnabledKey))
    {
        app.MapOpenApi(HostConstants.OpenApiRoutePattern);
        app.MapScalarApiReference(HostConstants.ScalarPath, o => o.WithTitle(HostConstants.ApiTitle));
    }

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, HostConstants.StartupFailedMessage);
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>WebApplicationFactory için görünür giriş noktası.</summary>
public partial class Program;
