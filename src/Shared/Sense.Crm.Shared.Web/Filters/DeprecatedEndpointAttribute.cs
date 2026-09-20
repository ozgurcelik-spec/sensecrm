using System.Globalization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Sense.Crm.Shared.Web.Filters;

/// <summary>
/// Kullanımdan kaldırılan uç (M8B, kararlı v1 sözleşme politikası): yanıtlara <c>Deprecation: true</c>, <c>Sunset: &lt;HTTP-tarihi&gt;</c> (RFC 8594) ve <c>Link: &lt;…&gt;; rel="successor-version"</c> ekler.
/// OpenAPI'de <c>deprecated: true</c> için uçta ayrıca <c>[Obsolete]</c> işaretlenir. Süreç: (1) uç işaretlenir, (2) başlıklar, (3) belgede <c>deprecated</c>, (4) uç için en az 6 ay / sürüm için 12 ay paralel destek,
/// (5) değişiklik günlüğü. <paramref name="sunset"/> ISO tarihi (<c>2027-06-30</c>), <paramref name="successor"/> ardıl uç adresi (isteğe bağlı).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class DeprecatedEndpointAttribute(string sunset, string? successor = null) : ActionFilterAttribute
{
    public string Sunset { get; } = sunset;

    public string? Successor { get; } = successor;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.HttpContext.Response.Headers;
        headers["Deprecation"] = "true";
        if (DateTimeOffset.TryParse(Sunset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sunsetAt))
        {
            headers["Sunset"] = sunsetAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(Successor))
        {
            headers.Append("Link", $"<{Successor}>; rel=\"successor-version\"");
        }

        base.OnActionExecuting(context);
    }
}
