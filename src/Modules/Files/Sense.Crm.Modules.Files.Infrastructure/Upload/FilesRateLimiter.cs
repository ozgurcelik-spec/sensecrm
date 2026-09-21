using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Application;

namespace Sense.Crm.Modules.Files.Infrastructure.Upload;

/// <summary>
/// Bellek içi (süreç başına) sınırlayıcılar: kullanıcı başına dakikada <c>Upload</c> parça (<c>files-upload</c>; her parça bir jeton) ve <c>Download</c> indirme
/// (<c>files-download</c>); kiracı başına <c>ConcurrentUploadsPerTenant</c> ve genel <c>MaxConcurrentUploads</c> eşzamanlı yükleme. Genel kullanıcı/kiracı sınırlayıcıları
/// (600/3000) ayrıca geçerlidir. Tek örnek olarak kaydedilir.
/// </summary>
public sealed class FilesRateLimiter : IFilesRateLimiter, IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _upload;
    private readonly PartitionedRateLimiter<Guid> _download;
    private readonly int _perTenant;
    private readonly int _global;
    private readonly ConcurrentDictionary<Guid, int> _tenantCounts = new();
    private int _globalCount;

    public FilesRateLimiter(IOptions<FilesOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limits = options.Value.RateLimiting;
        _upload = Create(limits.Upload);
        _download = Create(limits.Download);
        _perTenant = limits.ConcurrentUploadsPerTenant;
        _global = limits.MaxConcurrentUploads;
    }

    public bool TryAcquireUploadPart(Guid userId) => Acquire(_upload, userId);

    public bool TryAcquireDownload(Guid userId) => Acquire(_download, userId);

    public IDisposable? TryEnterUpload(Guid tenantId)
    {
        if (Interlocked.Increment(ref _globalCount) > _global)
        {
            Interlocked.Decrement(ref _globalCount);
            return null;
        }

        var tenantCount = _tenantCounts.AddOrUpdate(tenantId, 1, (_, current) => current + 1);
        if (tenantCount > _perTenant)
        {
            Release(tenantId);
            return null;
        }

        return new Slot(this, tenantId);
    }

    public void Dispose()
    {
        _upload.Dispose();
        _download.Dispose();
    }

    private static PartitionedRateLimiter<Guid> Create(int permitPerMinute) =>
        PartitionedRateLimiter.Create<Guid, Guid>(userId => RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    private static bool Acquire(PartitionedRateLimiter<Guid> limiter, Guid userId)
    {
        using var lease = limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    private void Release(Guid tenantId)
    {
        Interlocked.Decrement(ref _globalCount);
        _tenantCounts.AddOrUpdate(tenantId, 0, (_, current) => Math.Max(current - 1, 0));
    }

    private sealed class Slot(FilesRateLimiter owner, Guid tenantId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(tenantId);
            }
        }
    }
}
