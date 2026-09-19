using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.Observability;

namespace Sense.Crm.Shared.Web.Middleware;

/// <summary>
/// Kimlik doğrulamasından sonra çalışır: JWT claim'lerinden ICurrentUser'ı doldurur ve `tid` claim'i ile kiracı
/// (aktif organizasyon) bağlamını kurar. Sıra: UseCorrelationId → UseAuthentication → UseRequestContext → UseAuthorization.
/// URL'de kiracı asla taşınmaz; organizasyon değiştirmek yeni token almak demektir (POST /auth/switch-organization).
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUserAccessor userAccessor, ITenantContextSetter tenantSetter)
    {
        var correlationId = context.Items[CorrelationIdContext.HttpContextItemKey] as string ?? context.TraceIdentifier;

        var principal = context.User;
        IDisposable? tenantScope = null;

        if (principal.Identity?.IsAuthenticated == true)
        {
            var roles = principal.FindAll(ClaimNames.Roles).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

            var user = new AuthenticatedUser(
                UserId: ParseGuid(principal.FindFirst(ClaimNames.Subject)?.Value) ?? Guid.Empty,
                Email: principal.FindFirst(ClaimNames.Email)?.Value,
                DisplayName: principal.FindFirst(ClaimNames.Name)?.Value,
                Roles: roles,
                IsPlatformAdmin: principal.HasClaim(c => c.Type == ClaimNames.PlatformAdmin && c.Value == ClaimNames.TrueValue),
                CorrelationId: correlationId,
                IpAddress: context.Connection.RemoteIpAddress?.ToString());

            userAccessor.Set(user);

            if (ParseGuid(principal.FindFirst(ClaimNames.Tenant)?.Value) is { } tid)
            {
                tenantScope = tenantSetter.BeginScope(tid, principal.FindFirst(ClaimNames.TenantSlug)?.Value);
            }
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            tenantScope?.Dispose();
        }
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;
}

public static class RequestContextMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestContext(this IApplicationBuilder app) => app.UseMiddleware<RequestContextMiddleware>();
}
