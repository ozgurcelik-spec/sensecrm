using Microsoft.AspNetCore.Authorization;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Web.DependencyInjection;

namespace Sense.Crm.Shared.Web.Middleware;

/// <summary>
/// Endpoint işareti: parolası geçici olan (<c>MustChangePassword</c>) kullanıcı bu ucu yine de çağırabilir
/// (<c>POST /me/password</c>, <c>GET /me</c>, davet uçları). İşaretsiz her kimlik doğrulamalı uç 403
/// <c>auth.password_change_required</c> döner.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhenPasswordChangeRequiredAttribute : Attribute;

/// <summary>
/// Geçici parola zorlaması (H4): access token'da <see cref="ClaimNames.PasswordChangeRequired"/> claim'i varsa yalnız
/// <see cref="AllowWhenPasswordChangeRequiredAttribute"/> taşıyan uçlar çalışır; diğerleri 403 <c>auth.password_change_required</c>.
/// Bayrak token'dadır (istek başına veritabanı okuması yok); parola değişince yeni token bayrağı taşımaz. Anonim uçlar
/// (<c>[AllowAnonymous]</c>: giriş, yenileme, çıkış…) kullanıcı adına iş yapmadığından etkilenmez.
/// Sıra: UseAuthentication → bu middleware → UseAuthorization.
/// </summary>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(c => c.Type == ClaimNames.PasswordChangeRequired && c.Value == ClaimNames.TrueValue)
            && context.GetEndpoint() is { } endpoint
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
            && endpoint.Metadata.GetMetadata<AllowWhenPasswordChangeRequiredAttribute>() is null)
        {
            await ProblemResponses.WriteAsync(context, Error.Forbidden(PasswordChangeRequiredCode), context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Kararlı hata kodu (Identity.Domain.IdentityErrors.PasswordChangeRequired ile aynı dize).</summary>
    public const string PasswordChangeRequiredCode = "auth.password_change_required";
}

public static class PasswordChangeRequiredMiddlewareExtensions
{
    public static IApplicationBuilder UsePasswordChangeRequired(this IApplicationBuilder app) => app.UseMiddleware<PasswordChangeRequiredMiddleware>();
}
