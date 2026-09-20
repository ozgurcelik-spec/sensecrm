using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Application.Files;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;
using AppByteRange = Sense.Crm.Modules.Files.Application.ByteRange;

namespace Sense.Crm.Modules.Files.Infrastructure.Storage;

/// <summary>
/// S3 istemcisi ve doğrulama önbelleği (tek örnek; süreç boyunca yaşar). <b>Sunucudan bağımsız</b> (<c>AWSSDK.S3</c>): MinIO/SeaweedFS/Garage/Ceph RGW kod değişmeden çalışır.
/// SDK v4 varsayılan <c>aws-chunked</c>/CRC32 sağlama toplamı bazı S3 uyumlu sunucularla sorun çıkarabildiği için sağlama toplamı yalnız <b>gerektiğinde</b>
/// hesaplanır (<c>WHEN_REQUIRED</c>); yol biçimli adresleme (<c>ForcePathStyle</c>) varsayılandır. İç ağ düz HTTP olduğundan SDK yükün SHA-256'sını hesaplar →
/// yükleme <b>aranabilir</b> akıştan (geçici dosya) yapılır.
/// </summary>
public sealed class S3StorageState : IDisposable
{
    private static readonly TimeSpan HealthTtl = TimeSpan.FromSeconds(30);
    private long _healthyUntilTicks;

    public S3StorageState(IOptions<FilesOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var storage = options.Value.Storage;
        Bucket = storage.Bucket;
        RequireEncryption = string.Equals(storage.Encryption, StorageEncryptionModes.Required, StringComparison.OrdinalIgnoreCase);
        var config = new AmazonS3Config
        {
            ServiceURL = storage.Endpoint,
            ForcePathStyle = storage.ForcePathStyle,
            AuthenticationRegion = storage.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromMinutes(5),
        };
        Client = new AmazonS3Client(new BasicAWSCredentials(storage.AccessKey, storage.SecretKey), config);
    }

    public IAmazonS3 Client { get; }

    public string Bucket { get; }

    public bool RequireEncryption { get; }

    internal bool IsRecentlyHealthy(TimeProvider clock) => clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _healthyUntilTicks);

    internal void MarkHealthy(TimeProvider clock) => Interlocked.Exchange(ref _healthyUntilTicks, (clock.GetUtcNow() + HealthTtl).UtcTicks);

    internal void MarkUnhealthy() => Interlocked.Exchange(ref _healthyUntilTicks, 0);

    public void Dispose() => Client.Dispose();
}

/// <summary>S3 uyumlu (<c>AWSSDK.S3</c>) <see cref="IFileStorage"/>: üretim adaptörü (D4).</summary>
public sealed class S3FileStorage(S3StorageState state, ITenantContext tenant, TimeProvider clock, ILogger<S3FileStorage> logger) : FileStorageBase(tenant, logger), IFileStorage
{
    private const string OctetStream = "application/octet-stream";
    private const int PageSize = 1000;

    public async Task PutAsync(ObjectKey key, Stream content, long length, CancellationToken ct)
    {
        EnsureTenant(key);
        ArgumentNullException.ThrowIfNull(content);

        // Şifreleme doğrulanamıyorsa sessizce şifresiz yazılmaz.
        var health = await CheckCachedAsync(ct).ConfigureAwait(false);
        if (!health.Healthy)
        {
            throw new StorageUnavailableException(health.Detail);
        }

        var request = new PutObjectRequest
        {
            BucketName = state.Bucket,
            Key = key.ToString(),
            InputStream = content,
            ContentType = OctetStream,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
        };
        request.Headers.ContentLength = length;
        await state.Client.PutObjectAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<StoredObject?> GetAsync(ObjectKey key, AppByteRange? range, CancellationToken ct)
    {
        EnsureTenant(key);
        var request = new GetObjectRequest { BucketName = state.Bucket, Key = key.ToString() };
        if (range is { } r)
        {
            request.ByteRange = r.End is { } last ? new Amazon.S3.Model.ByteRange(r.Start, last) : new Amazon.S3.Model.ByteRange(r.Start, long.MaxValue);
        }

        GetObjectResponse response;
        try
        {
            response = await state.Client.GetObjectAsync(request, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "Range is outside the object.");
        }

        var (start, end, total) = ParseContentRange(response.ContentRange, response.ContentLength);
        return new StoredObject(response.ResponseStream, total, start, end, new ResponseOwner(response));
    }

    public async Task<ObjectStat?> StatAsync(ObjectKey key, CancellationToken ct)
    {
        EnsureTenant(key);
        try
        {
            var meta = await state.Client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = state.Bucket, Key = key.ToString() }, ct).ConfigureAwait(false);
            return new ObjectStat(meta.ContentLength, new DateTimeOffset(DateTime.SpecifyKind(meta.LastModified ?? DateTime.UtcNow, DateTimeKind.Utc)));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(ObjectKey key, CancellationToken ct)
    {
        EnsureTenant(key);
        var existed = await StatAsync(key, ct).ConfigureAwait(false) is not null;
        await state.Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = state.Bucket, Key = key.ToString() }, ct).ConfigureAwait(false);
        return existed;
    }

    public async Task<long> DeletePrefixAsync(Guid tenantId, CancellationToken ct)
    {
        EnsureTenant(tenantId);
        var prefix = ObjectKey.TenantPrefix(tenantId);
        long deleted = 0;
        string? token = null;
        do
        {
            var page = await state.Client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = state.Bucket, Prefix = prefix, MaxKeys = PageSize, ContinuationToken = token }, ct).ConfigureAwait(false);
            var keys = (page.S3Objects ?? []).Select(o => new KeyVersion { Key = o.Key }).ToList();
            if (keys.Count > 0)
            {
                var result = await state.Client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = state.Bucket, Objects = keys, Quiet = true }, ct).ConfigureAwait(false);
                if (result.DeleteErrors is { Count: > 0 })
                {
                    throw new IOException(string.Create(CultureInfo.InvariantCulture, $"{result.DeleteErrors.Count} objects under the tenant prefix could not be deleted."));
                }

                deleted += keys.Count;
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        return deleted;
    }

    public async IAsyncEnumerable<ObjectInfo> ListAsync(Guid tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureTenant(tenantId);
        var prefix = ObjectKey.TenantPrefix(tenantId);
        string? token = null;
        do
        {
            var page = await state.Client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = state.Bucket, Prefix = prefix, MaxKeys = PageSize, ContinuationToken = token }, ct).ConfigureAwait(false);
            foreach (var item in page.S3Objects ?? [])
            {
                var modified = new DateTimeOffset(DateTime.SpecifyKind(item.LastModified ?? DateTime.UtcNow, DateTimeKind.Utc));
                yield return new ObjectInfo(item.Key, ObjectKey.TryParse(item.Key, out var parsed) ? parsed : null, item.Size ?? 0, modified);
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
    }

    public async Task<StorageHealth> CheckAsync(CancellationToken ct)
    {
        try
        {
            await state.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = state.Bucket, MaxKeys = 1 }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            state.MarkUnhealthy();
            return new StorageHealth(false, "object storage or bucket is not reachable");
        }

        if (state.RequireEncryption)
        {
            try
            {
                var encryption = await state.Client.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = state.Bucket }, ct).ConfigureAwait(false);
                if (encryption.ServerSideEncryptionConfiguration?.ServerSideEncryptionRules is not { Count: > 0 })
                {
                    state.MarkUnhealthy();
                    return new StorageHealth(false, "bucket default encryption is not enabled");
                }
            }
            catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                state.MarkUnhealthy();
                return new StorageHealth(false, "bucket default encryption could not be verified");
            }
        }

        state.MarkHealthy(clock);
        return new StorageHealth(true, state.RequireEncryption ? "s3 (encrypted)" : "s3");
    }

    private async Task<StorageHealth> CheckCachedAsync(CancellationToken ct) =>
        state.IsRecentlyHealthy(clock) ? new StorageHealth(true, "cached") : await CheckAsync(ct).ConfigureAwait(false);

    /// <summary><c>Content-Range: bytes a-b/total</c> (aralıklı) ya da tam gövde (uzunluk).</summary>
    private static (long Start, long End, long Total) ParseContentRange(string? contentRange, long contentLength)
    {
        if (string.IsNullOrWhiteSpace(contentRange))
        {
            return (0, Math.Max(contentLength - 1, 0), contentLength);
        }

        // "bytes 0-9/100"
        var span = contentRange.AsSpan().Trim();
        var space = span.IndexOf(' ');
        var range = space >= 0 ? span[(space + 1)..] : span;
        var slash = range.IndexOf('/');
        var dash = range.IndexOf('-');
        if (slash < 0 || dash < 0
            || !long.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(range[(dash + 1)..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            || !long.TryParse(range[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var total))
        {
            return (0, Math.Max(contentLength - 1, 0), contentLength);
        }

        return (start, end, total);
    }

    private sealed class ResponseOwner(GetObjectResponse response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
