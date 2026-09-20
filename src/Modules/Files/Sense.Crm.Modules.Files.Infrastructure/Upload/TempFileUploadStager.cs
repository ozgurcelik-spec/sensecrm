using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Files.Infrastructure.Upload;

/// <summary>Files metrikleri (<c>Meter "Sense.Crm.Files"</c>). Etiketlerde dosya adı/içerik <b>yoktur</b>.</summary>
public static class FilesMetrics
{
    public const string MeterName = "Sense.Crm.Files";

    private static readonly Meter Meter = new(MeterName);

    /// <summary><c>crm.files.upload.rejected{reason}</c>: imza uyuşmazlığı, zararlı, tür, boyut … (kişisel veri yok).</summary>
    public static readonly Counter<long> UploadRejected = Meter.CreateCounter<long>("crm.files.upload.rejected");

    /// <summary><c>crm.files.reconcile.missing</c>: uzlaştırmanın <c>missing</c> işaretlediği satırlar.</summary>
    public static readonly Counter<long> ReconcileMissing = Meter.CreateCounter<long>("crm.files.reconcile.missing");

    public static readonly Counter<long> ReconcileOrphansDeleted = Meter.CreateCounter<long>("crm.files.reconcile.orphans_deleted");

    public static readonly Counter<long> ReconcileGuardTripped = Meter.CreateCounter<long>("crm.files.reconcile.guard_tripped");

    public static void Rejected(string reason) => UploadRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
}

/// <summary>
/// <see cref="IUploadStager"/> (faz 1, işlem yok): ad temizleme + uzantı allow-list (bayt okumadan) → <b>geçici dosyaya</b> kopyalama (bayt sayımı + SHA-256 aynı geçişte;
/// <c>MaxFileMb</c> aşılınca hemen kes, <c>Content-Length</c> yokken de) → boş kontrol → içerik imzası + beyan → tarayıcı. Geçici dosya <c>DeleteOnClose</c> ile açılır:
/// <b>her yolda</b> (başarı/hata/iptal/istemci kopması) <see cref="IStagedUpload.DisposeAsync"/> ya da hata dalı onu siler. Dosya adı/içerik/imza günlüğe yazılmaz.
/// </summary>
public sealed partial class TempFileUploadStager(IOptions<FilesOptions> options, IFileScanner scanner, ILogger<TempFileUploadStager> logger) : IUploadStager
{
    private const int BufferSize = 81920;

    public async Task<Result<IStagedUpload>> StageAsync(UploadPart part, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);
        var settings = options.Value;

        var name = FileNamePolicy.Sanitize(part.RawFileName);
        if (name is null)
        {
            return Reject("name", FilesErrors.NameInvalidError());
        }

        var extension = FileNamePolicy.GetExtension(name);
        var allowed = FilesOptionsValidator.EffectiveExtensions(settings.Upload);
        if (!allowed.Contains(extension) || FileNamePolicy.HasDangerousDoubleExtension(name) || !FileTypeCatalog.TryGet(extension, out var type))
        {
            return Reject("type", FilesErrors.TypeNotAllowedError(extension));
        }

        // Beyan edilen tür danışmandır ama tehlikeli/başka aile beyanı bayt okumadan reddedilir.
        if (!FileTypeCatalog.IsDeclaredTypeCompatible(type, part.DeclaredContentType))
        {
            return Reject("declared_mismatch", FilesErrors.ContentMismatchError());
        }

        var maxBytes = settings.Upload.MaxFileBytes;
        var temp = new FileStream(
            Path.Combine(ResolveTempDirectory(settings.Upload), Guid.NewGuid().ToString("N") + ".upload"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var keep = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = await part.Content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return Reject("too_large", FilesErrors.TooLargeError(maxBytes));
                }

                hash.AppendData(buffer, 0, read);
                await temp.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            if (total == 0)
            {
                return Reject("empty", FilesErrors.EmptyError());
            }

            await temp.FlushAsync(ct).ConfigureAwait(false);
            temp.Position = 0;
            if (FileTypeCatalog.Inspect(type, temp) != InspectionOutcome.Ok)
            {
                return Reject("content_mismatch", FilesErrors.ContentMismatchError());
            }

            var scanStatus = ScanStatus.Skipped;
            var quarantine = false;
            if (scanner.IsEnabled)
            {
                temp.Position = 0;
                var scan = await scanner.ScanAsync(temp, ct).ConfigureAwait(false);
                switch (scan.Outcome)
                {
                    case ScanOutcome.Infected when settings.Scanner.Quarantine:
                        scanStatus = ScanStatus.Infected;
                        quarantine = true;
                        FilesMetrics.Rejected("infected_quarantined");
                        break;
                    case ScanOutcome.Infected:
                        return Reject("infected", FilesErrors.InfectedError());
                    case ScanOutcome.Error when settings.Scanner.FailClosed:
                        return Reject("scan_unavailable", FilesErrors.ScanUnavailableError());
                    case ScanOutcome.Error:
                        break;
                    default:
                        scanStatus = ScanStatus.Clean;
                        break;
                }
            }

            temp.Position = 0;
            keep = true;
            return Result.Success<IStagedUpload>(new StagedUpload(temp, name, extension, type.ContentType, total, Convert.ToHexStringLower(hash.GetHashAndReset()), scanStatus, quarantine));
        }
        finally
        {
            if (!keep)
            {
                await temp.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Result<IStagedUpload> Reject(string reason, Error error)
    {
        FilesMetrics.Rejected(reason);
        LogRejected(logger, reason, error.Code);
        return error;
    }

    private static string ResolveTempDirectory(UploadOptions upload)
    {
        var directory = string.IsNullOrWhiteSpace(upload.TempDirectory) ? Path.GetTempPath() : upload.TempDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    [LoggerMessage(EventId = 5020, Level = LogLevel.Warning, Message = "Upload rejected: {Reason} ({Code})")]
    private static partial void LogRejected(ILogger logger, string reason, string code);

    private sealed class StagedUpload(FileStream file, string name, string extension, string contentType, long sizeBytes, string sha256, ScanStatus scanStatus, bool quarantine) : IStagedUpload
    {
        public string Name { get; } = name;

        public string Extension { get; } = extension;

        public string ContentType { get; } = contentType;

        public long SizeBytes { get; } = sizeBytes;

        public string Sha256 { get; } = sha256;

        public ScanStatus ScanStatus { get; } = scanStatus;

        public bool Quarantine { get; } = quarantine;

        public Stream OpenRead()
        {
            file.Position = 0;
            return new Storage.NonDisposingStream(file);
        }

        public ValueTask DisposeAsync() => file.DisposeAsync();
    }
}

/// <summary>Varsayılan tarayıcı: no-op (<c>Files:Scanner:Provider = none</c>; <c>scan_status = skipped</c>). ClamAV bağdaştırıcısı gelecekteki isteğe bağlı iştir.</summary>
public sealed class NoOpFileScanner : IFileScanner
{
    public bool IsEnabled => false;

    public Task<ScanResult> ScanAsync(Stream content, CancellationToken ct) => Task.FromResult(ScanResult.Clean);
}
