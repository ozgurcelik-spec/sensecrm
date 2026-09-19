namespace Crm.Api.Middleware;

/// <summary>
/// Her yanıta temel güvenlik başlıklarını ekler (OWASP Secure Headers önerileri, JSON API'ye uygun alt küme).
/// Content-Security-Policy bilinçli olarak eklenmez: Scalar API referansı inline script kullanır;
/// API yanıtları zaten HTML değildir ve çerçeveleme <c>X-Frame-Options</c> ile engellenir. HSTS, Program.cs'te
/// Development dışı ortamlarda <c>UseHsts()</c> ile eklenir. Uygulama bir başlığı açıkça koyduysa üzerine yazılmaz.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private static readonly (string Name, string Value)[] Headers =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "DENY"),
        ("Referrer-Policy", "no-referrer"),
        ("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()"),
        ("Cross-Origin-Opener-Policy", "same-origin"),
    ];

    public Task InvokeAsync(HttpContext context)
    {
        // Doğrudan ve OnStarting'de: UseExceptionHandler hata yanıtında başlıkları temizler.
        Apply(context.Response);
        context.Response.OnStarting(() =>
        {
            Apply(context.Response);
            return Task.CompletedTask;
        });

        return next(context);
    }

    private static void Apply(HttpResponse response)
    {
        foreach (var (name, value) in Headers)
        {
            response.Headers.TryAdd(name, value);
        }
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
