using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Infrastructure.Caching;
using Crm.Shared.Kernel.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Crm.Shared.Infrastructure.Messaging.Behaviours;

/// <summary>
/// ICachedQuery sonuçlarını HybridCache'te (L1 bellek + L2 Redis) kiracı ön ekiyle saklar.
/// Result nesnesi değil yalnız başarılı sonucun değeri serileştirilir; hata sonuçları önbelleğe yazılmaz.
/// Handler, çağıranın kiracı/kullanıcı bağlamında çalışır (bkz. <see cref="HybridCacheContextExtensions"/>).
/// </summary>
public sealed class CachingBehaviour<TRequest, TResponse>(HybridCache cache, ITenantContext tenant, IOptions<CachingOptions> options)
    : IPipelineBehaviour<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICachedRequest cached || CachedResultStrategy<TResponse>.Instance is not { } strategy)
        {
            return next();
        }

        var prefix = CacheKeys.TenantPrefix(tenant, options.Value);
        var entry = new CacheEntryRequest(
            CacheKeys.Join(prefix, cached.CacheKey),
            cached.Tags.Select(t => CacheKeys.Join(prefix, t)).ToArray(),
            new HybridCacheEntryOptions
            {
                Expiration = cached.Expiration ?? TimeSpan.FromMinutes(options.Value.DefaultExpirationMinutes),
                LocalCacheExpiration = TimeSpan.FromSeconds(options.Value.LocalExpirationSeconds),
            });

        return strategy.GetOrCreateAsync(cache, entry, next, cancellationToken);
    }
}

public sealed record CacheEntryRequest(string Key, IReadOnlyCollection<string> Tags, HybridCacheEntryOptions Options);

/// <summary>Önbellekte saklanan değer zarfı. HasValue=false ise kayıt hemen silinir.</summary>
public sealed record CachedValue<TValue>(bool HasValue, TValue? Value);

/// <summary>TResponse = Result&lt;TValue&gt; için değer tabanlı önbellek stratejisi; kapalı tip başına bir kez oluşturulur.</summary>
public abstract class CachedResultStrategy<TResponse>
    where TResponse : Result
{
    public static CachedResultStrategy<TResponse>? Instance { get; } = Create();

    public abstract Task<TResponse> GetOrCreateAsync(
        HybridCache cache, CacheEntryRequest entry, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);

    private static CachedResultStrategy<TResponse>? Create()
    {
        var type = typeof(TResponse);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Result<>))
        {
            return null;
        }

        var strategyType = typeof(ValueResultStrategy<>).MakeGenericType(type.GetGenericArguments()[0]);
        return (CachedResultStrategy<TResponse>)Activator.CreateInstance(strategyType)!;
    }
}

public sealed class ValueResultStrategy<TValue> : CachedResultStrategy<Result<TValue>>
{
    public override async Task<Result<TValue>> GetOrCreateAsync(
        HybridCache cache, CacheEntryRequest entry, RequestHandlerDelegate<Result<TValue>> next, CancellationToken cancellationToken)
    {
        Result<TValue>? produced = null;

        var cached = await cache.GetOrCreateInContextAsync(
            entry.Key,
            async _ =>
            {
                produced = await next().ConfigureAwait(false);
                return produced.IsSuccess
                    ? new CachedValue<TValue>(true, produced.Value)
                    : new CachedValue<TValue>(false, default);
            },
            entry.Options,
            entry.Tags,
            cancellationToken).ConfigureAwait(false);

        if (cached.HasValue)
        {
            return produced ?? Result.Success(cached.Value!);
        }

        await cache.RemoveAsync(entry.Key, cancellationToken).ConfigureAwait(false);

        // Başka bir eşzamanlı çağrının ürettiği hata sonucu paylaşılmaz; bu çağrı kendi sonucunu üretir.
        return produced ?? await next().ConfigureAwait(false);
    }
}

/// <summary>Önbellek anahtarı kuralları: {tenant}:{anahtar}.</summary>
public static class CacheKeys
{
    public static string TenantPrefix(ITenantContext tenant, CachingOptions options) =>
        tenant.IsResolved ? tenant.TenantId.ToString("N") : options.KeyPrefixGlobal;

    public static string Join(params string[] parts) => string.Join(CachingDefaults.KeySeparator, parts);
}
