using Crm.Api;
using Crm.Api.HealthChecks;
using Crm.Api.Middleware;
using Crm.Shared.Infrastructure.Observability;
using Crm.Shared.Web.DependencyInjection;
using Crm.Shared.Web.Middleware;
using Scalar.AspNetCore;
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

    // "Server: Kestrel" başlığı sunucu teknolojisini ifşa etmesin.
    builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

    builder.Services.AddCrmWeb(builder.Configuration, ModuleCatalog.Modules);
    builder.Services.AddCrmAuthentication(builder.Configuration, builder.Environment);
    builder.Services.AddCrmHealthChecks(builder.Configuration);

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

    // Anonim kimlik uçları için IP bazlı hız sınırlama; yalnız [EnableRateLimiting] ile işaretli uçlarda devreye girer.
    app.UseRateLimiter();

    app.UseAuthentication();
    app.UseRequestContext();
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
