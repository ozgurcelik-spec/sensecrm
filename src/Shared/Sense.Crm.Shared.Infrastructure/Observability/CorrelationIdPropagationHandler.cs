using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Shared.Infrastructure.Observability;

/// <summary>
/// Giden her HttpClient isteğine, kurulmuşsa mevcut correlation id'yi <c>X-Correlation-Id</c> başlığı olarak ekler.
/// W3C <c>traceparent</c> başlığını .NET zaten kendisi taşır; bu handler, iz örneklenmese bile aşağı akış servislerinin
/// loglarını aynı correlation id ile ilişkilendirebilmesini sağlar. Çağıran başlığı açıkça koymuşsa dokunmaz.
/// </summary>
public sealed class CorrelationIdPropagationHandler : DelegatingHandler
{
    private static readonly CorrelationIdContext Context = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = Context.CorrelationId;
        if (!string.IsNullOrEmpty(correlationId) && !request.Headers.Contains(CrmHeaderNames.CorrelationId))
        {
            request.Headers.TryAddWithoutValidation(CrmHeaderNames.CorrelationId, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
