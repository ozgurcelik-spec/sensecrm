using System.Diagnostics;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.Observability;

namespace Sense.Crm.Api.Middleware;

/// <summary>
/// Pipeline'ın en başında çalışır (kimlik doğrulamadan bile önce) ve isteğin izleme kimliklerini kurar:
/// <list type="bullet">
/// <item><c>X-Correlation-Id</c>: istekten okunur; yoksa (veya geçersizse) proxy'nin <c>X-Request-Id</c>'si, o da yoksa yeni GUID.
/// HttpContext.Items ve AsyncLocal tabanlı <see cref="ICorrelationIdContextSetter"/> içine yazılır, span'e etiketlenir.</item>
/// <item><c>X-Request-Id</c>: bu tek isteğin kimliği; gelen değer korunur, yoksa <see cref="HttpContext.TraceIdentifier"/>.</item>
/// <item><c>X-Trace-Id</c>: aktif W3C trace id; istemci/destek ekibi izi bulabilsin diye yanıtta döner.</item>
/// </list>
/// Böylece hem sonraki middleware'ler (RequestContextMiddleware, GlobalExceptionHandler) hem de Serilog
/// <see cref="CorrelationIdEnricher"/> aynı kimliği görür. Yanıt başlıkları <c>OnStarting</c>'de yeniden yazılır:
/// UseExceptionHandler hata durumunda yanıtı (başlıklar dahil) temizlediğinden 500 yanıtları da kimlikleri taşır.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string CorrelationIdTag = "crm.correlation_id";

    public async Task InvokeAsync(HttpContext context, ICorrelationIdContextSetter correlationIdContextSetter)
    {
        var headers = context.Request.Headers;
        var incomingRequestId = HeaderValues.Read(headers, CrmHeaderNames.RequestId, HeaderValues.MaxIdLength);
        var correlationId = HeaderValues.Read(headers, CrmHeaderNames.CorrelationId, HeaderValues.MaxIdLength)
            ?? incomingRequestId
            ?? Guid.NewGuid().ToString();
        var requestId = incomingRequestId ?? context.TraceIdentifier;

        var activity = Activity.Current;
        activity?.SetTag(CorrelationIdTag, correlationId);
        var traceId = activity?.TraceId.ToHexString();

        context.Items[CorrelationIdContext.HttpContextItemKey] = correlationId;

        WriteResponseHeaders(context.Response, correlationId, requestId, traceId);
        context.Response.OnStarting(() =>
        {
            WriteResponseHeaders(context.Response, correlationId, requestId, traceId);
            return Task.CompletedTask;
        });

        using (correlationIdContextSetter.BeginScope(correlationId))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    private static void WriteResponseHeaders(HttpResponse response, string correlationId, string requestId, string? traceId)
    {
        response.Headers[CrmHeaderNames.CorrelationId] = correlationId;
        response.Headers[CrmHeaderNames.RequestId] = requestId;
        if (traceId is not null)
        {
            response.Headers[CrmHeaderNames.TraceId] = traceId;
        }
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>Pipeline'a olabildiğince erken eklenmelidir (UseExceptionHandler'dan önce).</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
