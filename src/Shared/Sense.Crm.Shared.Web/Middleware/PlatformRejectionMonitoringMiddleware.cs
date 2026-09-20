using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Shared.Web.Middleware;

/// <summary>
/// <c>/api/v1/platform/**</c> üzerindeki <b>reddedilen</b> istekleri kaba düzeyde izler (C-SEC2 L4): kimliği doğrulanmış ama platform yöneticisi olmayan (403) bir kullanıcının
/// platform konsolunu yoklaması bir saldırı/sızma belirtisi olabilir. <b>Kişisel veri yazılmaz</b>: yalnız sayaç (<c>crm.platform.rejected_requests</c>, etiket: HTTP yöntemi) ve
/// kullanıcı/kiracı kimliği içermeyen bir uyarı günlüğü (rate sınırlı değil; günlük hattı zaten toplu). Geçici parola zorlamasının 403'ü sayılmaz. Yanıtı değiştirmez.
/// Sıra: UseAuthentication → bu middleware → UsePasswordChangeRequired → UseAuthorization (kararı veren middleware'lerin ÜSTÜNDE olmalı ki durum kodunu görsün).
/// </summary>
public sealed partial class PlatformRejectionMonitoringMiddleware(RequestDelegate next, ILogger<PlatformRejectionMonitoringMiddleware> logger)
{
    public const string MeterName = "Sense.Crm.Security";
    public const string CounterName = "crm.platform.rejected_requests";
    private const string PlatformPrefix = "/api/v1/platform";

    private static readonly Meter SecurityMeter = new(MeterName);
    private static readonly Counter<long> Rejected = SecurityMeter.CreateCounter<long>(CounterName, unit: "{request}", description: "Authenticated non-platform-admin requests rejected on /platform/**.");

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context).ConfigureAwait(false);

        if (context.Response.StatusCode == StatusCodes.Status403Forbidden
            && context.Request.Path.StartsWithSegments(PlatformPrefix, StringComparison.OrdinalIgnoreCase)
            && context.User.Identity?.IsAuthenticated == true
            && !context.User.HasClaim(c => c.Type == ClaimNames.PlatformAdmin && c.Value == ClaimNames.TrueValue)
            && !context.User.HasClaim(c => c.Type == ClaimNames.PasswordChangeRequired && c.Value == ClaimNames.TrueValue))
        {
            Rejected.Add(1, new KeyValuePair<string, object?>("method", context.Request.Method));
            LogRejected(logger, context.Request.Method);
        }
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Warning, Message = "Rejected a {Method} request to the platform console from an authenticated non-platform-admin user")]
    private static partial void LogRejected(ILogger logger, string method);
}

public static class PlatformRejectionMonitoringMiddlewareExtensions
{
    public static IApplicationBuilder UsePlatformRejectionMonitoring(this IApplicationBuilder app) => app.UseMiddleware<PlatformRejectionMonitoringMiddleware>();
}
