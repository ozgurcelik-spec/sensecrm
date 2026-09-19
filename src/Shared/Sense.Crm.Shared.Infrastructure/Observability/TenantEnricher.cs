using Sense.Crm.Shared.Infrastructure.Context;
using Serilog.Core;
using Serilog.Events;

namespace Sense.Crm.Shared.Infrastructure.Observability;

/// <summary>
/// Her log olayına, çözümlenmişse mevcut kiracının TenantId'sini ekler. Parametresiz kurucu:
/// Serilog <c>.Enrich.With&lt;TenantEnricher&gt;()</c> ile örneklenir; TenantContext'in statik AsyncLocal'i
/// sayesinde DI kapsamı olmadan da (ör. logger bir kez kurulduğunda) doğru mantıksal akışı okur.
/// </summary>
public sealed class TenantEnricher : ILogEventEnricher
{
    public const string PropertyName = "TenantId";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (TenantContext.CurrentTenantIdOrNull is not { } tenantId)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, tenantId));
    }
}
