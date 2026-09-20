using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Files.Tests.Storage;

/// <summary>
/// <see cref="IFileStorage"/> sözleşme testleri: <b>aynı paket her adaptörde</b> (InMemory, FileSystem ve gerçek MinIO'ya karşı S3). Kiracı bağlamı ≠ anahtar kiracısı → reddedilir.
/// </summary>
public abstract class FileStorageContractTests
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Verilen kiracı bağlamıyla çalışan depo (yeni istek kapsamı gibi).</summary>
    protected abstract IFileStorage Create(ITenantContext tenant);

    /// <summary>Sabit kiracı bağlamı (üretimdeki <c>TenantContext</c> ortam-AsyncLocal'dır; testte bağımsız bağlamlar gerekir).</summary>
    protected sealed class FixedTenantContext(Guid? tenantId) : ITenantContext
    {
        public Guid TenantId => tenantId ?? throw new InvalidOperationException("Tenant context is not resolved.");

        public bool IsResolved => tenantId is not null;

        public string? TenantSlug => null;
    }

    protected static (ITenantContext Context, IDisposable Scope) TenantScope(Guid tenantId) => (new FixedTenantContext(tenantId), NoopScope.Instance);

    protected sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
        }
    }

    private IFileStorage For(Guid tenantId, out IDisposable scope)
    {
        var (context, disposable) = TenantScope(tenantId);
        scope = disposable;
        return Create(context);
    }


    private static ObjectKey NewKey(Guid tenantId) => ObjectKey.For(tenantId, 2026, Guid.CreateVersion7());

    private static async Task PutAsync(IFileStorage storage, ObjectKey key, byte[] data)
    {
        await using var stream = new MemoryStream(data);
        await storage.PutAsync(key, stream, data.Length, Ct);
    }

    private static async Task<byte[]> ReadAllAsync(StoredObject stored)
    {
        await using (stored)
        {
            using var buffer = new MemoryStream();
            await stored.Content.CopyToAsync(buffer, Ct);
            return buffer.ToArray();
        }
    }

    [Fact]
    public async Task PutGet_RoundTripsTheBytes()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        var data = SampleData(10_000);

        await PutAsync(storage, key, data);

        var stored = await storage.GetAsync(key, null, Ct);
        stored.ShouldNotBeNull();
        stored.TotalLength.ShouldBe(10_000);
        stored.RangeStart.ShouldBe(0);
        stored.RangeEnd.ShouldBe(9_999);
        stored.IsPartial.ShouldBeFalse();
        (await ReadAllAsync(stored)).ShouldBe(data);
    }

    [Fact]
    public async Task GetMissing_ReturnsNull()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        (await storage.GetAsync(NewKey(tenant), null, Ct)).ShouldBeNull();
        (await storage.StatAsync(NewKey(tenant), Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(0L, 99L)]
    [InlineData(10L, 19L)]
    [InlineData(999L, 999L)]
    [InlineData(990L, 5000L)]
    public async Task Get_WithRange_ReturnsExactlyTheRequestedBytes(long start, long end)
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        var data = SampleData(1000);
        await PutAsync(storage, key, data);

        var stored = await storage.GetAsync(key, new ByteRange(start, end), Ct);
        stored.ShouldNotBeNull();
        var lastByte = Math.Min(end, 999);
        stored.TotalLength.ShouldBe(1000);
        stored.RangeStart.ShouldBe(start);
        stored.RangeEnd.ShouldBe(lastByte);
        stored.ContentLength.ShouldBe(lastByte - start + 1);
        (await ReadAllAsync(stored)).ShouldBe(data[(int)start..(int)(lastByte + 1)]);
    }

    [Fact]
    public async Task Get_OpenEndedRange_ReturnsToTheEnd()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        var data = SampleData(500);
        await PutAsync(storage, key, data);

        var stored = await storage.GetAsync(key, new ByteRange(400, null), Ct);
        stored.ShouldNotBeNull();
        stored.RangeStart.ShouldBe(400);
        stored.RangeEnd.ShouldBe(499);
        (await ReadAllAsync(stored)).ShouldBe(data[400..]);
    }

    [Fact]
    public async Task Get_RangeBeyondTheObject_IsRejected()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        await PutAsync(storage, key, SampleData(100));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => storage.GetAsync(key, new ByteRange(100, 200), Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => storage.GetAsync(key, new ByteRange(5000, null), Ct));
    }

    [Fact]
    public async Task Stat_ReturnsSizeAndLastModified()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        await PutAsync(storage, key, SampleData(321));

        var stat = await storage.StatAsync(key, Ct);
        stat.ShouldNotBeNull();
        stat.Size.ShouldBe(321);
        stat.LastModified.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        stat.LastModified.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task Delete_IsIdempotent()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        await PutAsync(storage, key, SampleData(10));

        (await storage.DeleteAsync(key, Ct)).ShouldBeTrue();
        (await storage.GetAsync(key, null, Ct)).ShouldBeNull();
        (await storage.DeleteAsync(key, Ct)).ShouldBeFalse();
        await storage.DeleteAsync(NewKey(tenant), Ct);
    }

    [Fact]
    public async Task Put_Overwrite_ReplacesTheContent()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        await PutAsync(storage, key, SampleData(10));
        await PutAsync(storage, key, SampleData(20, seed: 7));

        (await ReadAllAsync((await storage.GetAsync(key, null, Ct))!)).ShouldBe(SampleData(20, seed: 7));
    }

    [Fact]
    public async Task List_PagesPastOneThousandObjects_AndDeletePrefixRemovesThemAll()
    {
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 1_150; i++)
        {
            var key = NewKey(tenant);
            expected.Add(key.ToString());
            await PutAsync(storage, key, [1, 2, 3]);
        }

        // Başka kiracının nesnesi listeye girmez ve silinmez.
        var otherStorage = For(other, out var otherScope);
        using var __ = otherScope;
        var otherKey = NewKey(other);
        await PutAsync(otherStorage, otherKey, [9]);

        var listed = new List<ObjectInfo>();
        await foreach (var item in storage.ListAsync(tenant, Ct))
        {
            listed.Add(item);
        }

        listed.Count.ShouldBe(1_150);
        listed.Select(i => i.RawKey).ToHashSet(StringComparer.Ordinal).SetEquals(expected).ShouldBeTrue();
        listed.ShouldAllBe(i => i.Key != null && i.Key.Value.TenantId == tenant && i.Size == 3);

        (await storage.DeletePrefixAsync(tenant, Ct)).ShouldBe(1_150);
        await foreach (var leftover in storage.ListAsync(tenant, Ct))
        {
            throw new InvalidOperationException("Önek altında nesne kalmamalı: " + leftover.Size);
        }

        (await storage.DeletePrefixAsync(tenant, Ct)).ShouldBe(0);
        var stillThere = await otherStorage.GetAsync(otherKey, null, Ct);
        stillThere.ShouldNotBeNull();
        await stillThere.DisposeAsync();
    }

    [Fact]
    public async Task TenantContextThatDiffersFromTheKeyTenant_IsRejectedOnEveryOperation()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var storageOfA = For(a, out var scope);
        using var _ = scope;
        var foreignKey = NewKey(b);

        await Should.ThrowAsync<InvalidOperationException>(() => PutAsync(storageOfA, foreignKey, [1]));
        await Should.ThrowAsync<InvalidOperationException>(() => storageOfA.GetAsync(foreignKey, null, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => storageOfA.StatAsync(foreignKey, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => storageOfA.DeleteAsync(foreignKey, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => storageOfA.DeletePrefixAsync(b, Ct));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _1 in storageOfA.ListAsync(b, Ct))
            {
            }
        });
    }

    [Fact]
    public async Task UnresolvedTenantContext_IsRejected()
    {
        var storage = Create(new FixedTenantContext(null));
        await Should.ThrowAsync<InvalidOperationException>(() => storage.GetAsync(NewKey(Guid.NewGuid()), null, Ct));
    }

    [Fact]
    public async Task LargeObject_StreamsThroughWithoutCorruption()
    {
        var tenant = Guid.NewGuid();
        var storage = For(tenant, out var scope);
        using var _ = scope;
        var key = NewKey(tenant);
        const int size = 25 * 1024 * 1024;

        var path = Path.Combine(Path.GetTempPath(), "crm-files-test-" + Guid.NewGuid().ToString("N"));
        string expectedHash;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunk = SampleData(1024 * 1024);
            for (var i = 0; i < size / chunk.Length; i++)
            {
                await file.WriteAsync(chunk, Ct);
                hash.AppendData(chunk);
            }

            expectedHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            await file.FlushAsync(Ct);
            file.Position = 0;
            await storage.PutAsync(key, file, size, Ct);
        }

        var stored = await storage.GetAsync(key, null, Ct);
        stored.ShouldNotBeNull();
        stored.TotalLength.ShouldBe(size);
        await using (stored)
        {
            using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await stored.Content.ReadAsync(buffer, Ct)) > 0)
            {
                actual.AppendData(buffer, 0, read);
                total += read;
            }

            total.ShouldBe(size);
            Convert.ToHexStringLower(actual.GetHashAndReset()).ShouldBe(expectedHash);
        }
    }

    [Fact]
    public async Task Check_ReportsHealthy()
    {
        var storage = Create(new FixedTenantContext(null));
        (await storage.CheckAsync(Ct)).Healthy.ShouldBeTrue();
    }

    protected static byte[] SampleData(int size, int seed = 3)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)((i * 131 + seed) % 251);
        }

        return bytes;
    }

    protected static NullLogger<T> Log<T>() => NullLogger<T>.Instance;
}
