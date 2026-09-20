using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;

namespace Sense.Crm.Modules.Files.Infrastructure.Storage;

/// <summary>
/// Dosya sistemi <see cref="IFileStorage"/> (Development/Testing: Docker'sız çalışma). Yol <b>yalnız</b> <see cref="ObjectKey"/> ile kurulur (kullanıcıdan gelen hiçbir
/// parça yola girmez); yine de sonuç <c>Path.GetFullPath</c> ile kök önekine karşı denetlenir (kök dışına çıkış reddedilir). Yazma geçici dosya + atomik yeniden adlandırma.
/// </summary>
public sealed class FileSystemFileStorage(string rootPath, ITenantContext tenant, ILogger<FileSystemFileStorage> logger) : FileStorageBase(tenant, logger), IFileStorage
{
    private readonly string _root = EnsureTrailingSeparator(Path.GetFullPath(rootPath));

    public async Task PutAsync(ObjectKey key, Stream content, long length, CancellationToken ct)
    {
        EnsureTenant(key);
        ArgumentNullException.ThrowIfNull(content);
        var path = ResolvePath(key.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
                if (target.Length != length)
                {
                    throw new InvalidOperationException("Declared length does not match the stream length.");
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task<StoredObject?> GetAsync(ObjectKey key, ByteRange? range, CancellationToken ct)
    {
        EnsureTenant(key);
        var path = ResolvePath(key.ToString());
        if (!File.Exists(path))
        {
            return Task.FromResult<StoredObject?>(null);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var total = stream.Length;
        var start = range?.Start ?? 0;
        var end = range?.End is { } e ? Math.Min(e, total - 1) : total - 1;
        if (start < 0 || start >= total || end < start)
        {
            stream.Dispose();
            throw new ArgumentOutOfRangeException(nameof(range), "Range is outside the object.");
        }

        stream.Position = start;
        return Task.FromResult<StoredObject?>(new StoredObject(new BoundedReadStream(stream, end - start + 1), total, start, end));
    }

    public Task<ObjectStat?> StatAsync(ObjectKey key, CancellationToken ct)
    {
        EnsureTenant(key);
        var info = new FileInfo(ResolvePath(key.ToString()));
        return Task.FromResult(info.Exists ? new ObjectStat(info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) : null);
    }

    public Task<bool> DeleteAsync(ObjectKey key, CancellationToken ct)
    {
        EnsureTenant(key);
        var path = ResolvePath(key.ToString());
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public Task<long> DeletePrefixAsync(Guid tenantId, CancellationToken ct)
    {
        EnsureTenant(tenantId);
        var directory = ResolvePath(ObjectKey.TenantPrefix(tenantId).TrimEnd('/'));
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(0L);
        }

        long count = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).LongCount();
        Directory.Delete(directory, recursive: true);
        return Task.FromResult(count);
    }

    public async IAsyncEnumerable<ObjectInfo> ListAsync(Guid tenantId, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureTenant(tenantId);
        var directory = ResolvePath(ObjectKey.TenantPrefix(tenantId).TrimEnd('/'));
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(file);
            yield return new ObjectInfo(relative, ObjectKey.TryParse(relative, out var parsed) ? parsed : null, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            await Task.Yield();
        }
    }

    public Task<StorageHealth> CheckAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_root);
            var probe = Path.Combine(_root, ".probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
            return Task.FromResult(new StorageHealth(true, "filesystem"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new StorageHealth(false, "filesystem root is not writable"));
        }
    }

    /// <summary>Anahtar/önek → kök altında tam yol; kök dışına çıkan her yol reddedilir.</summary>
    private string ResolvePath(string relativeKey)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativeKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root, StringComparison.Ordinal) && !string.Equals(full + Path.DirectorySeparatorChar, _root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved path escapes the storage root.");
        }

        return full;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
