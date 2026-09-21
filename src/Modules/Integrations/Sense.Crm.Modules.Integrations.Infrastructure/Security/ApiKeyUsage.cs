using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Integrations.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Security;

/// <summary>Bir (kiracı, anahtar, UTC günü) için toplanmış sayaçlar.</summary>
public sealed record ApiKeyUsageEntry(Guid TenantId, Guid KeyId, DateOnly Day, int Requests, int Errors, int Throttled);

/// <summary>
/// Anahtar kullanımı: her istek bellek içi sayaca yazılır (DB'ye istek başına yazma yok), <c>UsageFlushSeconds</c> aralıkla toplu <c>upsert</c> edilir (çökmede kayıp ≤ bir aralık, kabul).
/// <see cref="Drain"/> sayaçları sıfırlar → çift flush çift saymaz (idempotent).
/// </summary>
public sealed class ApiKeyUsageBuffer(TimeProvider clock) : IApiKeyUsageSink
{
    private sealed class Counters
    {
        public int Requests;
        public int Errors;
        public int Throttled;
    }

    private readonly ConcurrentDictionary<(Guid Tenant, Guid Key, DateOnly Day), Counters> _counters = new();

    public void Record(Guid tenantId, Guid keyId, int statusCode)
    {
        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var counters = _counters.GetOrAdd((tenantId, keyId, day), _ => new Counters());
        Interlocked.Increment(ref counters.Requests);
        if (statusCode >= 400)
        {
            Interlocked.Increment(ref counters.Errors);
        }

        if (statusCode == 429)
        {
            Interlocked.Increment(ref counters.Throttled);
        }
    }

    /// <summary>Birikmiş sayaçları döner ve sıfırlar.</summary>
    public IReadOnlyList<ApiKeyUsageEntry> Drain()
    {
        var result = new List<ApiKeyUsageEntry>();
        foreach (var ((tenant, key, day), counters) in _counters)
        {
            var requests = Interlocked.Exchange(ref counters.Requests, 0);
            var errors = Interlocked.Exchange(ref counters.Errors, 0);
            var throttled = Interlocked.Exchange(ref counters.Throttled, 0);
            if (requests > 0 || errors > 0 || throttled > 0)
            {
                result.Add(new ApiKeyUsageEntry(tenant, key, day, requests, errors, throttled));
            }
            else if (day != DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            {
                _counters.TryRemove((tenant, key, day), out _);
            }
        }

        return result;
    }

    /// <summary>Yazılamayan sayaçları geri koyar (en iyi çaba).</summary>
    public void Restore(IEnumerable<ApiKeyUsageEntry> entries)
    {
        foreach (var e in entries)
        {
            var counters = _counters.GetOrAdd((e.TenantId, e.KeyId, e.Day), _ => new Counters());
            Interlocked.Add(ref counters.Requests, e.Requests);
            Interlocked.Add(ref counters.Errors, e.Errors);
            Interlocked.Add(ref counters.Throttled, e.Throttled);
        }
    }
}

/// <summary>
/// Günlük sayaç kalıcılaştırma: yalnız <c>INSERT … ON CONFLICT DO UPDATE SET n = n + @n</c> (ham SQL; kiracı kimliği daima parametre). Kiracı filtresini atlayan tek yazma yolu bu upsert'tir
/// (değerler kiracı bağlamsız süreçten gelir).
/// </summary>
public sealed class ApiKeyUsageStore(IntegrationsDbContext db)
{
    public async Task UpsertAsync(IReadOnlyList<ApiKeyUsageEntry> entries, CancellationToken ct)
    {
        foreach (var e in entries)
        {
            var tenantId = e.TenantId;
            var keyId = e.KeyId;
            var day = e.Day;
            var requests = e.Requests;
            var errors = e.Errors;
            var throttled = e.Throttled;
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO integrations.api_key_usage_daily (tenant_id, api_key_id, day, requests, errors, throttled)
                VALUES ({tenantId}, {keyId}, {day}, {requests}, {errors}, {throttled})
                ON CONFLICT (tenant_id, api_key_id, day) DO UPDATE SET
                    requests = integrations.api_key_usage_daily.requests + EXCLUDED.requests,
                    errors = integrations.api_key_usage_daily.errors + EXCLUDED.errors,
                    throttled = integrations.api_key_usage_daily.throttled + EXCLUDED.throttled
                """,
                ct).ConfigureAwait(false);
        }
    }
}
