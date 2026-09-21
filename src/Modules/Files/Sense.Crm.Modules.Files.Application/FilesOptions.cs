using Sense.Crm.Modules.Files.Domain;

namespace Sense.Crm.Modules.Files.Application;

/// <summary>
/// <c>Files</c> yapılandırma bölümü (ortam değişkeni <c>Files__…</c>; sırlar dosyadan: <c>/run/secrets/Files__Storage__AccessKey</c>).
/// Tutarsız aralıklar açılışta <c>ValidateOnStart</c> ile reddedilir (bkz. <see cref="FilesOptionsValidator"/>).
/// </summary>
public sealed class FilesOptions
{
    public const string SectionName = "Files";

    public StorageOptions Storage { get; set; } = new();

    public UploadOptions Upload { get; set; } = new();

    public ScannerOptions Scanner { get; set; } = new();

    public PurgeOptions Purge { get; set; } = new();

    public ReconcileOptions Reconcile { get; set; } = new();

    public AccessLogOptions AccessLog { get; set; } = new();

    public RateLimitingOptions RateLimiting { get; set; } = new();
}

public static class StorageProviders
{
    public const string S3 = "s3";
    public const string FileSystem = "filesystem";
    public const string Memory = "memory";
}

public static class StorageEncryptionModes
{
    public const string Required = "required";
    public const string None = "none";
}

public sealed class StorageOptions
{
    /// <summary><c>s3</c> (üretim) | <c>filesystem</c> | <c>memory</c>. Boş = Development/Testing için <c>filesystem</c>/<c>memory</c>, Production için <c>s3</c> (aksi başlamaz).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>S3 uç noktası (ör. <c>http://minio:9000</c>).</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string Bucket { get; set; } = "crm-files";

    /// <summary>Servis hesabı anahtarları (sır; kök kimlik bilgisi <b>değil</b>).</summary>
    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public bool ForcePathStyle { get; set; } = true;

    /// <summary><c>required</c> (Production varsayılanı; <c>CheckAsync</c> kovada varsayılan şifrelemeyi doğrular) | <c>none</c> (yalnız Development/Testing).</summary>
    public string Encryption { get; set; } = string.Empty;

    /// <summary>Production'da <c>Encryption=none</c> için işletmenin "disk şifreli" beyanı (günlüğe uyarı).</summary>
    public bool AcknowledgeUnencrypted { get; set; }

    /// <summary><c>filesystem</c> sağlayıcısının kök dizini.</summary>
    public string RootPath { get; set; } = string.Empty;
}

public sealed class UploadOptions
{
    public int MaxFileMb { get; set; } = 25;

    public int MaxFilesPerRequest { get; set; } = 10;

    /// <summary>Tüm istek üst sınırı (MB); varsayılan <c>MaxFileMb × MaxFilesPerRequest</c> üst sınırı içinde 110.</summary>
    public int MaxRequestMb { get; set; } = 110;

    /// <summary>Geçici dosya dizini (üretimde <c>tmpfs</c>); boş = sistem geçici dizini.</summary>
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>İzinli uzantılar; boş = kataloğun tamamı. Yapılandırma yalnız <b>daraltabilir</b> (katalogda olmayan uzantı açılışta reddedilir).</summary>
    public string[] AllowedExtensions { get; set; } = [];

    public long MaxFileBytes => MaxFileMb * 1024L * 1024L;

    public long MaxRequestBytes => MaxRequestMb * 1024L * 1024L;
}

public static class ScannerProviders
{
    public const string None = "none";
}

public sealed class ScannerOptions
{
    /// <summary><c>none</c> (varsayılan; <c>scan_status = skipped</c>). ClamAV bağdaştırıcısı gelecekteki işidir.</summary>
    public string Provider { get; set; } = ScannerProviders.None;

    /// <summary><c>open</c> (tarayıcı hatasında yükleme sürer) | <c>closed</c> (<c>503 file.scan_unavailable</c>).</summary>
    public string FailMode { get; set; } = "open";

    /// <summary><c>reject</c> (bayt depoya yazılmaz, <c>422 file.infected</c>) | <c>quarantine</c>.</summary>
    public string OnInfected { get; set; } = "reject";

    public bool FailClosed => string.Equals(FailMode, "closed", StringComparison.OrdinalIgnoreCase);

    public bool Quarantine => string.Equals(OnInfected, "quarantine", StringComparison.OrdinalIgnoreCase);
}

public sealed class PurgeOptions
{
    public int SoftDeleteRetentionDays { get; set; } = 7;

    public int PollMinutes { get; set; } = 60;

    public int RecordMissingGraceDays { get; set; } = 30;

    public int ChunkSize { get; set; } = 200;
}

public sealed class ReconcileOptions
{
    public int PollHours { get; set; } = 24;

    public int OrphanGraceHours { get; set; } = 24;

    public double MaxOrphanDeleteFraction { get; set; } = 0.05;

    public int MaxOrphanDeletePerRun { get; set; } = 1000;
}

public sealed class AccessLogOptions
{
    public int RetentionDays { get; set; } = 365;
}

public sealed class RateLimitingOptions
{
    /// <summary>Kullanıcı başına dakikada yükleme parçası.</summary>
    public int Upload { get; set; } = 30;

    /// <summary>Kullanıcı başına dakikada indirme.</summary>
    public int Download { get; set; } = 120;

    public int ConcurrentUploadsPerTenant { get; set; } = 4;

    public int MaxConcurrentUploads { get; set; } = 16;
}

/// <summary>
/// Yapılandırma tutarlılığı (saf; ortam bilgisi parametre): <c>MaxFileMb</c> 1–200; <c>MaxFileMb ≤ MaxRequestMb ≤ MaxFilesPerRequest × MaxFileMb</c>; üretimde
/// <c>Provider = s3</c> ve şifreleme (<c>none</c> yalnız <c>AcknowledgeUnencrypted</c> ile); <c>AllowedExtensions</c> kataloğun dışına çıkamaz.
/// </summary>
public static class FilesOptionsValidator
{
    public static IReadOnlyList<string> Validate(FilesOptions options, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var upload = options.Upload;

        if (upload.MaxFileMb is < 1 or > 200)
        {
            errors.Add("Files:Upload:MaxFileMb must be between 1 and 200.");
        }

        if (upload.MaxFilesPerRequest < 1)
        {
            errors.Add("Files:Upload:MaxFilesPerRequest must be at least 1.");
        }

        if (upload.MaxRequestMb < upload.MaxFileMb || upload.MaxRequestMb > (long)upload.MaxFilesPerRequest * upload.MaxFileMb)
        {
            errors.Add("Files:Upload:MaxRequestMb must satisfy MaxFileMb <= MaxRequestMb <= MaxFilesPerRequest * MaxFileMb.");
        }

        foreach (var extension in upload.AllowedExtensions)
        {
            if (!FileTypeCatalog.TryGet(extension?.Trim().ToLowerInvariant(), out _))
            {
                errors.Add($"Files:Upload:AllowedExtensions contains '{extension}', which is not in the file type catalog (configuration can only narrow the catalog).");
            }
        }

        var provider = (options.Storage.Provider ?? string.Empty).Trim().ToLowerInvariant();
        if (provider.Length > 0 && provider is not (StorageProviders.S3 or StorageProviders.FileSystem or StorageProviders.Memory))
        {
            errors.Add("Files:Storage:Provider must be one of s3, filesystem, memory.");
        }

        if (provider == StorageProviders.S3)
        {
            if (string.IsNullOrWhiteSpace(options.Storage.Endpoint))
            {
                errors.Add("Files:Storage:Endpoint is required for the s3 provider.");
            }

            if (string.IsNullOrWhiteSpace(options.Storage.Bucket))
            {
                errors.Add("Files:Storage:Bucket is required.");
            }
        }

        var encryption = (options.Storage.Encryption ?? string.Empty).Trim().ToLowerInvariant();
        if (encryption.Length > 0 && encryption is not (StorageEncryptionModes.Required or StorageEncryptionModes.None))
        {
            errors.Add("Files:Storage:Encryption must be 'required' or 'none'.");
        }

        if (isProduction)
        {
            if (provider != StorageProviders.S3)
            {
                errors.Add("Files:Storage:Provider must be 's3' in Production (filesystem/memory are for Development/Testing only).");
            }

            if (encryption == StorageEncryptionModes.None && !options.Storage.AcknowledgeUnencrypted)
            {
                errors.Add("Files:Storage:Encryption 'none' is not allowed in Production unless Files:Storage:AcknowledgeUnencrypted=true (operator attests disk encryption).");
            }
        }

        if (!string.Equals(options.Scanner.Provider, ScannerProviders.None, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Files:Scanner:Provider must be 'none' (only the no-op scanner is implemented; ClamAV is a future item).");
        }

        if (options.Purge.SoftDeleteRetentionDays < 0 || options.Purge.PollMinutes < 1 || options.Purge.RecordMissingGraceDays < 0 || options.Purge.ChunkSize < 1)
        {
            errors.Add("Files:Purge values are out of range.");
        }

        if (options.Reconcile.PollHours < 1 || options.Reconcile.OrphanGraceHours < 0 || options.Reconcile.MaxOrphanDeletePerRun < 0
            || options.Reconcile.MaxOrphanDeleteFraction is < 0 or > 1)
        {
            errors.Add("Files:Reconcile values are out of range.");
        }

        if (options.RateLimiting.Upload < 1 || options.RateLimiting.Download < 1 || options.RateLimiting.ConcurrentUploadsPerTenant < 1 || options.RateLimiting.MaxConcurrentUploads < 1)
        {
            errors.Add("Files:RateLimiting values must be positive.");
        }

        return errors;
    }

    /// <summary>Etkin izinli uzantılar: yapılandırma boşsa katalogun tamamı, doluysa (kataloğa göre daraltılmış) küme.</summary>
    public static IReadOnlySet<string> EffectiveExtensions(UploadOptions upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var all = FileTypeCatalog.AllExtensions;
        if (upload.AllowedExtensions.Length == 0)
        {
            return new HashSet<string>(all, StringComparer.Ordinal);
        }

        var configured = upload.AllowedExtensions.Select(e => e.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return new HashSet<string>(all.Where(configured.Contains), StringComparer.Ordinal);
    }
}
