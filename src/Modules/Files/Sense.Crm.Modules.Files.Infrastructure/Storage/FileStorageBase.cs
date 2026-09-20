using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;

namespace Sense.Crm.Modules.Files.Infrastructure.Storage;

/// <summary>
/// Adaptörlerin ortak <b>kiracı koruması</b> (derinlemesine savunma): her metot <c>ObjectKey.TenantId == ITenantContext.TenantId</c> denetler; uyuşmazlık
/// <see cref="InvalidOperationException"/> + güvenlik günlüğüdür (anahtar/kimlik <b>hash'i</b>; dosya adı yoktur — anahtar zaten ad içermez).
/// Worker işleri ve imha, çağırmadan önce <c>ITenantContextSetter.BeginScope(tenantId)</c> kurar.
/// </summary>
public abstract partial class FileStorageBase(ITenantContext tenant, ILogger logger)
{
    protected ITenantContext Tenant { get; } = tenant;

    protected void EnsureTenant(ObjectKey key)
    {
        if (key.TenantId == Guid.Empty || !Tenant.IsResolved || key.TenantId != Tenant.TenantId)
        {
            LogTenantMismatch(logger, Tenant.IsResolved ? Tenant.TenantId : Guid.Empty, Fingerprint(key.ToString()));
            throw new InvalidOperationException("Object key does not belong to the current tenant.");
        }
    }

    protected void EnsureTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty || !Tenant.IsResolved || tenantId != Tenant.TenantId)
        {
            LogTenantMismatch(logger, Tenant.IsResolved ? Tenant.TenantId : Guid.Empty, Fingerprint(tenantId.ToString("D")));
            throw new InvalidOperationException("Tenant prefix does not match the current tenant.");
        }
    }

    /// <summary>Anahtarın kısa SHA-256 parmak izi (günlükte anahtarın kendisi yerine).</summary>
    internal static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    [LoggerMessage(EventId = 5100, Level = LogLevel.Error, Message = "Security: object storage call with tenant mismatch (context tenant {ContextTenantId}, key fingerprint {Fingerprint})")]
    private static partial void LogTenantMismatch(ILogger logger, Guid contextTenantId, string fingerprint);
}

/// <summary>Sınırlı okuma akışı (aralıklı okuma için): alttaki akıştan en çok <c>length</c> bayt verir; kapatınca alttakini de kapatır.</summary>
internal sealed class BoundedReadStream(Stream inner, long limit) : Stream
{
    private readonly long _limit = limit;
    private long _remaining = limit;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _limit;

    public override long Position
    {
        get => _limit - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Kapatılmayan sarmalayıcı: <c>PutAsync</c>'e verilen geçici dosya akışı yükleme sonrası kapanmaz (sahibi <c>IStagedUpload</c>).</summary>
internal sealed class NonDisposingStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Sahibi (geçici dosya) kapatır; alttaki akışa dokunulmaz.
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => base.DisposeAsync();
}
