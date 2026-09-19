namespace Crm.Shared.Infrastructure.Observability;

/// <summary>Mevcut mantıksal akışın correlation id'sini okur (HTTP isteği veya background job).</summary>
public interface ICorrelationIdContext
{
    /// <summary>Kurulmuşsa correlation id; aksi halde null.</summary>
    string? CorrelationId { get; }
}

/// <summary>Correlation id bağlamını kurar (middleware, job executor).</summary>
public interface ICorrelationIdContextSetter
{
    IDisposable BeginScope(string correlationId);
}

/// <summary>
/// Correlation id bağlamı. TenantContext ile aynı desen: AsyncLocal sayesinde HTTP isteğinde middleware,
/// arka plan işlerinde job executor tarafından kurulur ve aynı mantıksal akıştaki tüm loglar/enricher'lar görür.
/// </summary>
public sealed class CorrelationIdContext : ICorrelationIdContext, ICorrelationIdContextSetter
{
    /// <summary>CorrelationIdMiddleware'in HttpContext.Items'a yazdığı, pipeline'ın geri kalanının okuduğu anahtar.</summary>
    public const string HttpContextItemKey = "Crm.CorrelationId";

    private static readonly AsyncLocal<string?> Current = new();

    public string? CorrelationId => Current.Value;

    public IDisposable BeginScope(string correlationId)
    {
        var previous = Current.Value;
        Current.Value = correlationId;
        return new ScopeRestorer(previous);
    }

    private sealed class ScopeRestorer(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
