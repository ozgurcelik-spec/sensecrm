using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.Security;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.Caching;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Security;

/// <summary>Önbelleklenen anahtar meta verisi: yalnız <b>özet ve meta</b> (ham sır asla). İptal/güncelleme aynı süreçte anında geçersiz kılar; çok kopyada ≤ <c>CacheSeconds</c> bayat.</summary>
public sealed record ApiKeyMeta(
    Guid KeyId,
    Guid CreatorUserId,
    string CreatorName,
    string Name,
    string Prefix,
    byte[] SecretHash,
    string[] Scopes,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    string[] AllowedCidrs,
    string? TenantSlug);

/// <summary>Kimlik doğrulama başarısızlık azaltması (bellek içi, kopya başına): <c>IP + kiracı</c> başına kayan pencerede <c>MaxFailures</c> aşılırsa DB'siz <c>429</c>.</summary>
public sealed class ApiKeyFailureThrottle(TimeProvider clock, IOptions<IntegrationsOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    public bool IsBlocked(string ip, string tenantN)
    {
        if (!_failures.TryGetValue(Key(ip, tenantN), out var queue))
        {
            return false;
        }

        var (max, window) = Settings();
        lock (queue)
        {
            Trim(queue, window);
            return queue.Count >= max;
        }
    }

    public void RecordFailure(string ip, string tenantN)
    {
        var (_, window) = Settings();
        var queue = _failures.GetOrAdd(Key(ip, tenantN), _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            Trim(queue, window);
            queue.Enqueue(clock.GetUtcNow());
        }

        if (_failures.Count > 50_000)
        {
            Cleanup(window);
        }
    }

    private static string Key(string ip, string tenantN) => string.Concat(ip, "|", tenantN);

    private (int Max, TimeSpan Window) Settings()
    {
        var t = options.Value.ApiKeys.FailureThrottle;
        return (t.MaxFailures, TimeSpan.FromMinutes(t.WindowMinutes));
    }

    private void Trim(Queue<DateTimeOffset> queue, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        while (queue.Count > 0 && now - queue.Peek() >= window)
        {
            queue.Dequeue();
        }
    }

    private void Cleanup(TimeSpan window)
    {
        foreach (var (key, queue) in _failures)
        {
            lock (queue)
            {
                Trim(queue, window);
                if (queue.Count == 0)
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }
}

/// <summary>Anahtar başına son kullanım yazımını sınırlar (<c>LastUsedWriteMinutes</c>) ve IP değişimini izler (bellek içi).</summary>
public sealed class ApiKeyLastUsedTracker
{
    private readonly ConcurrentDictionary<Guid, (DateTime WrittenAt, string? Ip)> _last = new();

    /// <summary>Yazım gerekiyorsa true; <paramref name="previousIp"/> önceki bilinen IP (değişim günlüğü için).</summary>
    public bool ShouldWrite(Guid keyId, DateTime now, TimeSpan every, string? ip, out string? previousIp)
    {
        previousIp = null;
        if (_last.TryGetValue(keyId, out var state))
        {
            previousIp = state.Ip;
            if (now - state.WrittenAt < every)
            {
                return false;
            }
        }

        _last[keyId] = (now, ip);
        return true;
    }
}

/// <summary>
/// <see cref="IApiKeyAuthenticator"/> (D9): <c>Authorization: Bearer crmk_…</c>. Sıra: tek başlık/biçim (DB'siz) → IP+kiracı azaltması (DB'siz) → kiracı kapsamında filtreli anahtar arama (önbellekli meta) →
/// <b>sahte özet karşılaştırması</b> (yok/yanlış aynı maliyet: zamanlamayla önek keşfi yok) → yalnız sır DOĞRUYSA belirli kodlar (iptal, süre, IP listesi, oluşturan pasif, plan kapalı). Kiracı
/// <c>ITenantEntitlements</c> doğrulama SONRASI sorgulanır (bilinmeyen kiracı için tembel plan satırı açılmaz). Günlükte yalnız <c>crmk_&lt;önek&gt;</c> görünür.
/// </summary>
public sealed partial class ApiKeyAuthenticator(
    IntegrationsDbContext db,
    HybridCache cache,
    ITenantContextSetter tenantSetter,
    IPermissionService permissions,
    IMemberLookup members,
    ITenantDirectory directory,
    ITenantEntitlements entitlements,
    ApiKeyFailureThrottle throttle,
    ApiKeyLastUsedTracker lastUsed,
    IOptions<IntegrationsOptions> options,
    TimeProvider clock,
    ILogger<ApiKeyAuthenticator> logger) : IApiKeyAuthenticator
{
    public const string MeterName = "Sense.Crm.Integrations";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> AuthFailed = Meter.CreateCounter<long>("crm.api_keys.auth_failed", description: "Failed API key authentications.");
    private static readonly Counter<long> AuthSucceeded = Meter.CreateCounter<long>("crm.api_keys.auth_succeeded", description: "Successful API key authentications.");

    /// <summary>Bilinmeyen anahtarda da aynı maliyetli karşılaştırma yapılsın diye sabit sahte özet.</summary>
    private static readonly byte[] DummyHash = SHA256.HashData("crm-api-key-dummy-hash"u8);

    private const string BearerPrefix = "Bearer ";

    public async Task<ApiKeyAuthResult> AuthenticateAsync(IReadOnlyList<string> authorizationHeaders, string? remoteIp, CancellationToken ct = default)
    {
        // 1) Biçim: tek başlık, Bearer crmk_…, uzunluk/karakter sınıfı. Sapma → 401 (DB'ye gidilmez).
        if (authorizationHeaders.Count != 1 || authorizationHeaders[0] is not { } header
            || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            || !ApiKeyToken.TryParse(header[BearerPrefix.Length..].Trim(), out var parts))
        {
            AuthFailed.Add(1, new KeyValuePair<string, object?>("reason", "malformed"));
            return Unauthenticated();
        }

        var tenantN = parts.TenantId.ToString("N");
        var ip = remoteIp ?? "unknown";

        // 2) IP + kiracı başarısızlık azaltması (DB'siz).
        if (throttle.IsBlocked(ip, tenantN))
        {
            AuthFailed.Add(1, new KeyValuePair<string, object?>("reason", "throttled"));
            return ApiKeyAuthResult.Fail(new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests));
        }

        using var scope = tenantSetter.BeginScope(parts.TenantId);

        // 3) Filtreli arama (kiracı anahtar biçiminde gömülü; başka kiracının anahtarı bu kapsamda bulunmaz).
        var meta = await LoadAsync(tenantN, parts, ct).ConfigureAwait(false);

        // 4) Sahte özet karşılaştırması: yok/yanlış aynı yol.
        var presented = ApiKeyToken.HashSecret(parts.Secret);
        var secretOk = CryptographicOperations.FixedTimeEquals(presented, meta?.SecretHash ?? DummyHash) && meta is not null;
        if (!secretOk)
        {
            throttle.RecordFailure(ip, tenantN);
            AuthFailed.Add(1, new KeyValuePair<string, object?>("reason", "invalid"));
            LogFailed(logger, parts.Display);
            return Unauthenticated();
        }

        // 5) Sır doğru (artık saldırgan değil sahip): belirli kodlar.
        var now = clock.GetUtcNow().UtcDateTime;
        if (meta!.RevokedAt is not null)
        {
            return Reject(parts.Display, Error.Unauthorized(ApiKeyErrorCodes.Revoked));
        }

        if (now >= meta.ExpiresAt)
        {
            return Reject(parts.Display, Error.Unauthorized(ApiKeyErrorCodes.Expired));
        }

        if (meta.AllowedCidrs.Length > 0 && !IpAllowed(remoteIp, meta.AllowedCidrs))
        {
            return Reject(parts.Display, Error.Forbidden(ApiKeyErrorCodes.IpNotAllowed));
        }

        var held = await permissions.GetPermissionsAsync(meta.CreatorUserId, ct).ConfigureAwait(false);
        if (held.Count == 0)
        {
            return Reject(parts.Display, Error.Unauthorized(ApiKeyErrorCodes.OwnerInactive));
        }

        var snapshot = await entitlements.GetAsync(parts.TenantId, ct).ConfigureAwait(false);
        if (!snapshot.IsModuleEnabled(GatedModules.Integrations))
        {
            return Reject(parts.Display, EntitlementErrors.DisabledModule(GatedModules.Integrations));
        }

        await TouchAsync(meta, remoteIp, now, ct).ConfigureAwait(false);
        AuthSucceeded.Add(1);
        return ApiKeyAuthResult.Success(new ApiKeyIdentity(
            parts.TenantId,
            meta.TenantSlug,
            meta.KeyId,
            meta.Prefix,
            meta.Name,
            meta.CreatorUserId,
            meta.CreatorName + " (API: " + meta.Name + ")",
            meta.Scopes));
    }

    private ApiKeyAuthResult Reject(string display, Error error)
    {
        AuthFailed.Add(1, new KeyValuePair<string, object?>("reason", error.Code));
        LogRejected(logger, display, error.Code);
        return ApiKeyAuthResult.Fail(error);
    }

    private static ApiKeyAuthResult Unauthenticated() => ApiKeyAuthResult.Fail(Error.Unauthorized(ErrorCodes.Unauthenticated));

    private async Task<ApiKeyMeta?> LoadAsync(string tenantN, ApiKeyParts parts, CancellationToken ct)
    {
        var cacheKey = ApiKeyCacheKeys.For(tenantN, parts.Prefix);
        var seconds = options.Value.ApiKeys.CacheSeconds;
        var ttl = TimeSpan.FromSeconds(Math.Max(seconds, 1));
        var meta = await cache.GetOrCreateInContextAsync<ApiKeyMeta?>(
            cacheKey,
            async token => await ReadAsync(parts, token).ConfigureAwait(false),
            new HybridCacheEntryOptions { Expiration = ttl, LocalCacheExpiration = ttl },
            cancellationToken: ct).ConfigureAwait(false);

        // Olumsuz sonuç (bilinmeyen önek) önbelleklenmez; süre 0 ise hiç önbellek yok.
        if (meta is null || seconds <= 0)
        {
            await cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
        }

        return meta;
    }

    private async ValueTask<ApiKeyMeta?> ReadAsync(ApiKeyParts parts, CancellationToken ct)
    {
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Prefix == parts.Prefix, ct).ConfigureAwait(false);
        if (key is null)
        {
            return null;
        }

        var names = await members.GetDisplayNamesAsync([key.CreatedByUserId], ct).ConfigureAwait(false);
        var tenant = await directory.FindAsync(parts.TenantId, ct).ConfigureAwait(false);
        return new ApiKeyMeta(
            key.Id,
            key.CreatedByUserId,
            names.GetValueOrDefault(key.CreatedByUserId, "API"),
            key.Name,
            key.Prefix,
            key.SecretHash,
            [.. key.Scopes],
            key.ExpiresAt,
            key.RevokedAt,
            [.. key.AllowedCidrs],
            tenant?.Slug);
    }

    private async Task TouchAsync(ApiKeyMeta meta, string? ip, DateTime now, CancellationToken ct)
    {
        var every = TimeSpan.FromMinutes(options.Value.ApiKeys.LastUsedWriteMinutes);
        if (!lastUsed.ShouldWrite(meta.KeyId, now, every, ip, out var previousIp))
        {
            return;
        }

        if (previousIp is not null && ip is not null && !string.Equals(previousIp, ip, StringComparison.Ordinal))
        {
            LogIpChanged(logger, ApiKeyToken.Display(meta.Prefix), previousIp, ip);
        }

        try
        {
            var threshold = now - every;
            var keyId = meta.KeyId;
            var truncatedIp = ip is null ? null : ip.Length > 64 ? ip[..64] : ip;
            await db.ApiKeys
                .Where(k => k.Id == keyId && (k.LastUsedAt == null || k.LastUsedAt < threshold))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now).SetProperty(k => k.LastUsedIp, truncatedIp), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTouchFailed(logger, ex, ApiKeyToken.Display(meta.Prefix));
        }
    }

    /// <summary>İstemci IP'si (ForwardedHeaders sonrası) hiçbir CIDR'a girmiyorsa veya IP yoksa false (IP yoksa <b>reddet</b>).</summary>
    public static bool IpAllowed(string? remoteIp, IReadOnlyList<string> cidrs)
    {
        if (!IPAddress.TryParse(remoteIp, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var text in cidrs)
        {
            if (Cidr.TryParse(text, out var cidr) && cidr.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(EventId = 8100, Level = LogLevel.Warning, Message = "API key authentication failed for {KeyPrefix}")]
    private static partial void LogFailed(ILogger logger, string keyPrefix);

    [LoggerMessage(EventId = 8101, Level = LogLevel.Information, Message = "API key {KeyPrefix} rejected: {Code}")]
    private static partial void LogRejected(ILogger logger, string keyPrefix, string code);

    [LoggerMessage(EventId = 8102, Level = LogLevel.Information, Message = "API key {KeyPrefix} used from a new IP (was {OldIp}, now {NewIp})")]
    private static partial void LogIpChanged(ILogger logger, string keyPrefix, string oldIp, string newIp);

    [LoggerMessage(EventId = 8103, Level = LogLevel.Warning, Message = "Could not record last use for API key {KeyPrefix}")]
    private static partial void LogTouchFailed(ILogger logger, Exception exception, string keyPrefix);
}

internal static class ApiKeyCacheKeys
{
    public static string For(string tenantN, string prefix) => $"apikey:{tenantN}:{prefix}";
}

/// <summary>Önbellek geçersiz kılma (<c>apikey:{tenantN}:{prefix}</c>): aynı süreçte anında; çok kopyada ≤ <c>CacheSeconds</c>.</summary>
public sealed class ApiKeyCacheInvalidator(HybridCache cache) : IApiKeyCacheInvalidator
{
    public async Task InvalidateAsync(Guid tenantId, string prefix, CancellationToken ct) =>
        await cache.RemoveAsync(ApiKeyCacheKeys.For(tenantId.ToString("N"), prefix), ct).ConfigureAwait(false);
}
