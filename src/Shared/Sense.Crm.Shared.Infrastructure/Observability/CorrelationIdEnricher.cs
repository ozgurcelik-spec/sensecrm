using Serilog.Core;
using Serilog.Events;

namespace Sense.Crm.Shared.Infrastructure.Observability;

/// <summary>
/// Her log olayına, kurulmuşsa mevcut correlation id'yi ekler. HTTP isteğinde <c>CorrelationIdMiddleware</c>,
/// arka plan işlerinde <c>BackgroundJobExecutor</c> bağlamı kurar; bu enricher yalnızca okur.
/// </summary>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public const string PropertyName = "CorrelationId";

    private static readonly CorrelationIdContext Context = new();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var correlationId = Context.CorrelationId;
        if (string.IsNullOrEmpty(correlationId))
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, correlationId));
    }
}
