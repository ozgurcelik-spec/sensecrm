using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Shared.Web.Middleware;

/// <summary>
/// API anahtarı kullanım sayacı (M8B, D12): kimlik doğrulamasından SONRA ve hız sınırlayıcıdan ÖNCE çalışır → <c>429</c> yanıtları da sayılır. Okuma dahil <b>her kullanım</b> bellek içi sayaca yazılır
/// (istek/hata/kısıtlama); veritabanına istek başına yazma yoktur (periyodik toplu <c>upsert</c>). Yalnız sunucunun kurduğu ApiKey kimliği sayılır; başarısız kimlik doğrulama satır yazmaz (DoS).
/// </summary>
public sealed class ApiKeyUsageMiddleware(RequestDelegate next, IApiKeyUsageSink sink)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true
            || !principal.Identities.Any(i => i.AuthenticationType == ApiKeyClaimNames.Scheme)
            || !Guid.TryParse(principal.FindFirst(ApiKeyClaimNames.ApiKeyId)?.Value, out var keyId)
            || !Guid.TryParse(principal.FindFirst(ClaimNames.Tenant)?.Value, out var tenantId))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            sink.Record(tenantId, keyId, StatusCodes.Status500InternalServerError);
            throw;
        }

        sink.Record(tenantId, keyId, context.Response.StatusCode);
    }
}

public static class ApiKeyUsageMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyUsage(this IApplicationBuilder app) => app.UseMiddleware<ApiKeyUsageMiddleware>();
}
