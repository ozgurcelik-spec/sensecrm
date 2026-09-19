namespace Crm.Api.Middleware;

/// <summary>
/// Kimlik uçlarının (<c>/api/v1/auth/*</c>: giriş, kayıt, token yenileme…) yanıtları token içerir; ara katmanlar/tarayıcı önbelleğe
/// almasın diye <c>Cache-Control: no-store</c> eklenir. Uç kendi başlığını koymuşsa (ör. <c>auth/config</c>: 60 sn genel önbellek) dokunulmaz.
/// </summary>
public sealed class NoStoreAuthMiddleware(RequestDelegate next)
{
    private const string AuthPrefix = "/api/v1/auth";

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(AuthPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey("Cache-Control"))
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}

public static class NoStoreAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseNoStoreForAuth(this IApplicationBuilder app) => app.UseMiddleware<NoStoreAuthMiddleware>();
}
