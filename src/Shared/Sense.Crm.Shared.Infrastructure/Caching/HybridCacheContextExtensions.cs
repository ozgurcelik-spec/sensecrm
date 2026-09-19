using Microsoft.Extensions.Caching.Hybrid;

namespace Sense.Crm.Shared.Infrastructure.Caching;

/// <summary>
/// HybridCache, önbellek ıskasında factory'yi thread-pool'a ExecutionContext akıtmadan kuyruklar
/// (stampede koruması için UnsafeQueueUserWorkItem). Bu yüzden AsyncLocal tabanlı kiracı/kullanıcı bağlamı,
/// Activity (trace) ve log scope'ları factory içinde kaybolur; kiracı filtresi boş kiracıyla çalışır.
/// Bu uzantı çağıranın ExecutionContext'ini yakalar ve factory'yi onun içinde çalıştırır.
/// Kodda HybridCache.GetOrCreateAsync doğrudan çağrılmaz; her zaman bu uzantı kullanılır.
/// </summary>
public static class HybridCacheContextExtensions
{
    public static ValueTask<T> GetOrCreateInContextAsync<T>(
        this HybridCache cache,
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(factory);

        var state = new FactoryState<T>(ExecutionContext.Capture(), factory);
        return cache.GetOrCreateAsync(key, state, static (s, ct) => s.Run(ct), options, tags, cancellationToken);
    }

    private sealed class FactoryState<T>(ExecutionContext? context, Func<CancellationToken, ValueTask<T>> factory)
    {
        public ValueTask<T> Run(CancellationToken cancellationToken)
        {
            if (context is null)
            {
                return factory(cancellationToken);
            }

            var invocation = new Invocation(factory, cancellationToken);
            ExecutionContext.Run(context, static s => ((Invocation)s!).Start(), invocation);
            return new ValueTask<T>(invocation.Result!);
        }

        private sealed class Invocation(Func<CancellationToken, ValueTask<T>> factory, CancellationToken cancellationToken)
        {
            /// <summary>ValueTask yalnız bir kez beklenebildiği için Task'a çevrilerek saklanır (yalnız önbellek ıskasında).</summary>
            public Task<T>? Result { get; private set; }

            public void Start() => Result = factory(cancellationToken).AsTask();
        }
    }
}
