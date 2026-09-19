using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Modules;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Web.Authorization;
using Crm.Shared.Web.Controllers;
using Crm.Shared.Web.ErrorHandling;
using Crm.Shared.Web.Localization;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProblemDetailsDefaults = Crm.Shared.Contracts.Configuration.ProblemDetailsDefaults;

namespace Crm.Shared.Web.DependencyInjection;

public static class WebServiceCollectionExtensions
{
    private const string OpenApiDocumentName = "v1";
    private const string ApiVersionGroupFormat = "'v'VVV";

    /// <summary>Name of the CORS policy registered in <see cref="AddCrmWeb"/> and applied via <see cref="UseCrmCors"/>.</summary>
    public const string CorsPolicyName = "CrmFrontend";

    /// <summary>
    /// API host'u için ortak kayıt: controller'lar (her modülün Api assembly'si application part olarak eklenir),
    /// URL sürümleme (/api/v1), yerelleştirme, ProblemDetails, izin policy'leri, OpenAPI, CORS, hız sınırlama, health.
    /// </summary>
    public static IServiceCollection AddCrmWeb(this IServiceCollection services, IConfiguration configuration, IReadOnlyList<IModule> modules)
    {
        services.AddCrmCore(configuration);

        var localization = configuration.GetSection(ConfigurationSections.Localization).Get<LocalizationOptions>() ?? new LocalizationOptions();
        services.AddLocalization(o => o.ResourcesPath = localization.ResourcesPath);
        services.TryAddSingleton<IErrorLocalizer, ErrorLocalizer>();
        services.Configure<RequestLocalizationOptions>(o =>
        {
            var cultures = localization.SupportedCultures.Select(c => new CultureInfo(c)).ToList();
            o.DefaultRequestCulture = new RequestCulture(localization.DefaultCulture);
            o.SupportedCultures = cultures;
            o.SupportedUICultures = cultures;
            o.ApplyCurrentCultureToResponseHeaders = true;
        });
        ValidatorOptions.Global.LanguageManager.Enabled = true;

        var mvc = services.AddControllers(options =>
            {
                options.ModelBinderProviders.Insert(0, new Binding.PagedQueryBinderProvider());

                // Null/eksik alanlar FluentValidation'a kadar ulaşsın: tüm doğrulama hataları aynı biçimde ("validation" + errors) döner.
                options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
                options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), StatusCodes.Status400BadRequest));
                options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), StatusCodes.Status401Unauthorized));
                options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), StatusCodes.Status403Forbidden));
                options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ProblemDetails), StatusCodes.Status404NotFound));
            })
            .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = InvalidModelStateProblem)
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            });

        foreach (var module in modules)
        {
            foreach (var asm in module.Assemblies.Distinct())
            {
                mvc.PartManager.ApplicationParts.Add(new AssemblyPart(asm));
            }

            module.AddModule(services, configuration);
        }

        services.AddSingleton(modules);

        services.AddApiVersioning(o =>
            {
                o.DefaultApiVersion = ApiVersionParser.Default.Parse(ApiRoutes.DefaultVersion);
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.ReportApiVersions = true;
                o.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddMvc()
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = ApiVersionGroupFormat;
                o.SubstituteApiVersionInUrl = true;
            });

        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        // SPA (web/) kendi kökeninden (Vite: http://localhost:5173) çağırır; izinli kökenler "Cors:AllowedOrigins".
        var cors = configuration.GetSection(ConfigurationSections.Cors).Get<CorsOptions>() ?? new CorsOptions();
        services.AddCors(options =>
        {
            options.AddPolicy(CorsPolicyName, policy =>
            {
                if (cors.AllowedOrigins.Count > 0)
                {
                    // Her kimlik doğrulamalı çağrı Authorization başlığı taşıdığından preflight'lıdır; max-age tekrarları azaltır.
                    policy.WithOrigins([.. cors.AllowedOrigins])
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .SetPreflightMaxAge(TimeSpan.FromHours(2))
                        .WithExposedHeaders(CrmHeaderNames.ExposedToClients);
                }
            });
        });

        services.TryAddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, PermissionHandler>());
        services.AddAuthorization();

        services.AddCrmRateLimiting(configuration);

        services.AddOpenApi(OpenApiDocumentName);
        services.AddHealthChecks();
        services.AddHttpContextAccessor();

        return services;
    }

    /// <summary>
    /// Kültür seçimi: Accept-Language (tr/en). Hata metinleri sunucuda yerelleştirilir ama istemci sabit `code` anahtarını
    /// kullanır (K8). FluentValidation alan adları "field.{snake_case}" kaynak anahtarlarından çevrilir; yoksa varsayılan ad.
    /// </summary>
    public static IApplicationBuilder UseCrmLocalization(this IApplicationBuilder app)
    {
        var localizer = app.ApplicationServices.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>();
        ValidatorOptions.Global.DisplayNameResolver = (_, member, _) =>
        {
            if (member is null)
            {
                return null;
            }

            var value = localizer[ValidationFieldKeys.For(member.Name)];
            return value.ResourceNotFound ? null : value.Value;
        };

        var options = app.ApplicationServices.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;
        return app.UseRequestLocalization(options);
    }

    /// <summary>Applies the CORS policy registered by <see cref="AddCrmWeb"/>. Must run before UseAuthentication/UseAuthorization.</summary>
    public static IApplicationBuilder UseCrmCors(this IApplicationBuilder app) => app.UseCors(CorsPolicyName);

    /// <summary>
    /// Anonim kimlik uçları (kayıt/giriş/yenileme) için IP bazlı sabit pencere limiter (<see cref="RateLimitPolicyNames.Auth"/>).
    /// Yalnız <c>[EnableRateLimiting]</c> ile işaretli uçlarda devreye girer; global varsayılan limiter yoktur.
    /// </summary>
    private static IServiceCollection AddCrmRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ConfigurationSections.RateLimiting).Get<RateLimitingOptions>() ?? new RateLimitingOptions();

        services.AddOptions<RateLimitingOptions>().Bind(configuration.GetSection(ConfigurationSections.RateLimiting));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitPolicyNames.Auth, httpContext => CreatePartition(httpContext, options.Auth));

            // M2: kimliği doğrulanmış her istek için kullanıcı başına VE kiracı başına genel sınır (zincirlenmiş; ikisi de geçmeli).
            // Anonim istekler bu sınırlayıcıya takılmaz (auth uçları yukarıdaki IP politikasıyla, health uçları sınırsız).
            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => AuthenticatedPartition(ctx, ClaimNames.Subject, "user", options.User)),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => AuthenticatedPartition(ctx, ClaimNames.Tenant, "tenant", options.Tenant)));
            limiter.OnRejected = WriteRateLimitProblemAsync;
        });

        return services;
    }

    /// <summary>JWT claim'i (sub/tid) ile bölümlenmiş sabit pencere; claim yoksa (anonim) sınırsız.</summary>
    private static RateLimitPartition<string> AuthenticatedPartition(HttpContext httpContext, string claimName, string scope, RateLimitPolicyOptions policyOptions)
    {
        var id = httpContext.User.Identity?.IsAuthenticated == true ? httpContext.User.FindFirst(claimName)?.Value : null;
        if (string.IsNullOrEmpty(id))
        {
            return RateLimitPartition.GetNoLimiter(string.Concat(scope, ":anonymous"));
        }

        return RateLimitPartition.GetFixedWindowLimiter(string.Concat(scope, ":", id), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = policyOptions.PermitLimit,
            Window = TimeSpan.FromSeconds(policyOptions.WindowSeconds),
            QueueLimit = policyOptions.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>IP adresine göre bölümlenmiş sabit pencere; ters proxy arkasında Forwarded Headers middleware'i gerekir.</summary>
    private static RateLimitPartition<string> CreatePartition(HttpContext httpContext, RateLimitPolicyOptions policyOptions)
    {
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = policyOptions.PermitLimit,
            Window = TimeSpan.FromSeconds(policyOptions.WindowSeconds),
            QueueLimit = policyOptions.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    private static async ValueTask WriteRateLimitProblemAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        await ProblemResponses.WriteAsync(context.HttpContext, new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>MVC model bağlama hataları (ör. bozuk JSON) da aynı sözleşmeyle döner: code = "validation", errors = { alan: [mesajlar] }.</summary>
    private static IActionResult InvalidModelStateProblem(ActionContext context)
    {
        var localizer = context.HttpContext.RequestServices.GetRequiredService<IErrorLocalizer>();
        var problemOptions = context.HttpContext.RequestServices.GetRequiredService<IOptions<ProblemMappingOptions>>().Value;
        var errors = context.ModelState
            .Where(e => e.Value is { Errors.Count: > 0 })
            .GroupBy(e => PropertyNames.ToCamelCase(e.Key.TrimStart('$', '.')))
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(e => e.Value!.Errors).Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? localizer.Message(Error.Validation(ErrorCodes.ValidationError)) : e.ErrorMessage).Distinct().ToArray(),
                StringComparer.Ordinal);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = localizer.Title(ErrorType.Validation),
            Detail = localizer.Message(Error.Validation(ErrorCodes.ValidationError)),
            Type = problemOptions.TypeBaseUrl + ErrorCodes.ValidationError,
            Instance = context.HttpContext.Request.Path,
        };
        problem.Extensions[ProblemDetailsDefaults.CodeExtension] = ErrorCodes.ValidationError;
        problem.Extensions[ProblemDetailsDefaults.TraceIdExtension] = context.HttpContext.TraceIdentifier;
        problem.Extensions[ProblemDetailsDefaults.ErrorsExtension] = errors;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { MediaTypes.ProblemJson },
        };
    }
}

/// <summary>Middleware/olay işleyicilerinden (401 challenge, 429) controller dışı ProblemDetails yazımı - aynı sözleşme.</summary>
public static class ProblemResponses
{
    public static Task WriteAsync(HttpContext httpContext, Error error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(error);

        var localizer = httpContext.RequestServices.GetRequiredService<IErrorLocalizer>();
        var problemOptions = httpContext.RequestServices.GetRequiredService<IOptions<ProblemMappingOptions>>().Value;
        var status = HttpStatusMap.For(error.Type);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = localizer.Title(error.Type),
            Detail = localizer.Message(error),
            Type = problemOptions.TypeBaseUrl + error.Code,
            Instance = httpContext.Request.Path,
        };
        problem.Extensions[ProblemDetailsDefaults.CodeExtension] = error.Code;
        problem.Extensions[ProblemDetailsDefaults.TraceIdExtension] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = status;
        return httpContext.Response.WriteAsJsonAsync(problem, (JsonSerializerOptions?)null, MediaTypes.ProblemJson, cancellationToken);
    }
}

/// <summary>Doğrulama mesajlarındaki alan adlarının kaynak anahtarları: field.{snake_case}.</summary>
public static class ValidationFieldKeys
{
    public const string Prefix = "field.";

    public static string For(string propertyName) => Prefix + PropertyNames.ToSnakeCase(propertyName);
}
