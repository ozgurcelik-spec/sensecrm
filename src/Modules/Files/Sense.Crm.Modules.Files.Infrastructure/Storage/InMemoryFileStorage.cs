using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;

namespace Sense.Crm.Modules.Files.Infrastructure.Storage;

/// <summary>
/// Bellek içi nesne deposu durumu (tek örnek; süreç boyunca yaşar). <see cref="InMemoryFileStorage"/> bunun kiracı korumalı, istek-kapsamlı yüzüdür.
/// Yalnız birim/entegrasyon testleri ve Testing ortamı içindir. Test yardımcıları (<see cref="SeedRaw"/>, <see cref="Snapshot"/>) ham anahtarlarla çalışır
/// (yetim/yabancı nesne senaryoları için).
/// </summary>
public sealed class InMemoryObjectStore
{
    private readonly ConcurrentDictionary<string, StoredEntry> _objects = new(StringComparer.Ordinal);

    internal sealed record StoredEntry(byte[] Data, DateTimeOffset LastModified);

    public int Count => _objects.Count;

    public IReadOnlyCollection<string> Keys => _objects.Keys.ToArray();

    internal void Put(string key, byte[] data, DateTimeOffset now) => _objects[key] = new StoredEntry(data, now);

    internal StoredEntry? Get(string key) => _objects.TryGetValue(key, out var entry) ? entry : null;

    internal bool Remove(string key) => _objects.TryRemove(key, out _);

    internal IEnumerable<KeyValuePair<string, StoredEntry>> Enumerate(string prefix) =>
        _objects.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal);

    /// <summary>Test yardımcısı: ham anahtarla (ObjectKey olmayabilir) nesne koyar.</summary>
    public void SeedRaw(string rawKey, byte[] data, DateTimeOffset? lastModified = null) =>
        Put(rawKey, data, lastModified ?? DateTimeOffset.UtcNow);

    /// <summary>Test yardımcısı: ham anahtarlı nesneyi siler (nesnesiz satır/kayıp nesne senaryoları).</summary>
    public bool RemoveRaw(string rawKey) => Remove(rawKey);

    /// <summary>Test yardımcısı: bir nesnenin baytları.</summary>
    public byte[]? Snapshot(string rawKey) => Get(rawKey)?.Data;

    /// <summary>Test yardımcısı: bir nesnenin son değişiklik anını değiştirir (grace penceresi testleri).</summary>
    public void Touch(string rawKey, DateTimeOffset lastModified)
    {
        if (Get(rawKey) is { } entry)
        {
            _objects[rawKey] = entry with { LastModified = lastModified };
        }
    }
}

/// <summary>Bellek içi <see cref="IFileStorage"/> (kiracı korumalı; <see cref="InMemoryObjectStore"/> üzerinde çalışır).</summary>
public sealed class InMemoryFileStorage(InMemoryObjectStore store, ITenantContext tenant, TimeProvider clock, ILogger<InMemoryFileStorage> logger)
    : FileStorageBase(tenant, logger), IFileStorage
{
    public async Task PutAsync(ObjectKey key, Stream content, long length, CancellationToken ct)
    {
        EnsureTenant(key);
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream(capacity: (int)Math.Min(length, int.MaxValue));
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (buffer.Length != length)
        {
            throw new InvalidOperationException("Declared length does not match the stream length.");
        }

        store.Put(key.ToString(), buffer.ToArray(), clock.GetUtcNow());
    }

    public Task<StoredObject?> GetAsync(ObjectKey key, ByteRange? range, CancellationToken ct)
    {
        EnsureTenant(key);
        if (store.Get(key.ToString()) is not { } entry)
        {
            return Task.FromResult<StoredObject?>(null);
        }

        var total = entry.Data.LongLength;
        var start = range?.Start ?? 0;
        var end = range?.End is { } e ? Math.Min(e, total - 1) : total - 1;
        if (start < 0 || start >= total || end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "Range is outside the object.");
        }

        var slice = new MemoryStream(entry.Data, (int)start, (int)(end - start + 1), writable: false);
        return Task.FromResult<StoredObject?>(new StoredObject(slice, total, start, end));
    }

    public Task<ObjectStat?> StatAsync(ObjectKey key, CancellationToken ct)
    {
        EnsureTenant(key);
        return Task.FromResult(store.Get(key.ToString()) is { } entry ? new ObjectStat(entry.Data.LongLength, entry.LastModified) : null);
    }

    public Task<bool> DeleteAsync(ObjectKey key, CancellationToken ct)
    {
        EnsureTenant(key);
        return Task.FromResult(store.Remove(key.ToString()));
    }

    public Task<long> DeletePrefixAsync(Guid tenantId, CancellationToken ct)
    {
        EnsureTenant(tenantId);
        long count = 0;
        foreach (var pair in store.Enumerate(ObjectKey.TenantPrefix(tenantId)).ToList())
        {
            if (store.Remove(pair.Key))
            {
                count++;
            }
        }

        return Task.FromResult(count);
    }

    public async IAsyncEnumerable<ObjectInfo> ListAsync(Guid tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureTenant(tenantId);
        foreach (var pair in store.Enumerate(ObjectKey.TenantPrefix(tenantId)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            yield return new ObjectInfo(pair.Key, ObjectKey.TryParse(pair.Key, out var parsed) ? parsed : null, pair.Value.Data.LongLength, pair.Value.LastModified);
            await Task.Yield();
        }
    }

    public Task<StorageHealth> CheckAsync(CancellationToken ct) => Task.FromResult(new StorageHealth(true, "memory"));
}
