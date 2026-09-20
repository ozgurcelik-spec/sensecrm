using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Files;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Persistence;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Files.Application;

/// <summary>Files modülünün UnitOfWork yüzü (<c>FilesDbContext</c> uygular).</summary>
public interface IFilesUnitOfWork : IModuleUnitOfWork;

// ---------------------------------------------------------------------------------------------------------------------
// Nesne deposu portu (akış tabanlı; tenant-bilinçli ObjectKey alır, ham string almaz).
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Bir bayt aralığı (uçlar dahil). <c>End == null</c>: sona kadar.</summary>
public readonly record struct ByteRange(long Start, long? End);

/// <summary>Depodan açılmış nesne: <see cref="Content"/> akışı, nesnenin <b>toplam</b> boyutu ve (aralıklıysa) sunulan aralık. Kapatılınca akış bırakılır.</summary>
public sealed class StoredObject(Stream content, long totalLength, long rangeStart, long rangeEnd, IAsyncDisposable? owner = null) : IAsyncDisposable
{
    public Stream Content { get; } = content;

    /// <summary>Nesnenin toplam boyutu.</summary>
    public long TotalLength { get; } = totalLength;

    public long RangeStart { get; } = rangeStart;

    /// <summary>Uçlar dahil son bayt konumu.</summary>
    public long RangeEnd { get; } = rangeEnd;

    public long ContentLength => RangeEnd - RangeStart + 1;

    public bool IsPartial => RangeStart != 0 || RangeEnd != TotalLength - 1;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        if (owner is not null)
        {
            await owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record ObjectStat(long Size, DateTimeOffset LastModified);

/// <summary>
/// Listeleme girdisi. <see cref="Key"/> yalnız anahtar katı ayrıştırmadan geçtiyse dolar; geçmeyen (yabancı) nesneler <c>null</c> anahtarla döner
/// (uzlaştırma bunları <b>asla</b> silmez).
/// </summary>
public sealed record ObjectInfo(string RawKey, ObjectKey? Key, long Size, DateTimeOffset LastModified);

public sealed record StorageHealth(bool Healthy, string Detail);

/// <summary>
/// Nesne deposu portu (D4). <b>Kiracı koruması (derinlemesine savunma):</b> her adaptör metodu <c>ObjectKey.TenantId == ITenantContext.TenantId</c> denetler;
/// uyuşmazlık <c>InvalidOperationException</c> + güvenlik günlüğüdür (anahtar/kimlik <b>hash'i</b>, dosya adı yok). Worker işleri ve imha, çağırmadan önce
/// <c>ITenantContextSetter.BeginScope(tenantId)</c> kurar.
/// </summary>
public interface IFileStorage
{
    /// <summary>Yazar. <paramref name="content"/> aranabilir (seekable) ve başa sarılmıştır; <paramref name="length"/> kesindir.</summary>
    Task PutAsync(ObjectKey key, Stream content, long length, CancellationToken ct);

    /// <summary>Yoksa <c>null</c>. Aralık nesnenin dışındaysa <c>null</c> değil <see cref="ArgumentOutOfRangeException"/>: çağıran önce <c>StatAsync</c>/boyutla doğrular.</summary>
    Task<StoredObject?> GetAsync(ObjectKey key, ByteRange? range, CancellationToken ct);

    Task<ObjectStat?> StatAsync(ObjectKey key, CancellationToken ct);

    /// <summary>Idempotent: nesne yoksa da <c>false</c> döner, hata vermez.</summary>
    Task<bool> DeleteAsync(ObjectKey key, CancellationToken ct);

    /// <summary>Kiracının <b>tüm</b> nesnelerini siler (imha): listeleme + 1000'lik toplu silme. Silinen sayıyı döner.</summary>
    Task<long> DeletePrefixAsync(Guid tenantId, CancellationToken ct);

    /// <summary><c>{tenantId:D}/</c> önekindeki nesneler; sayfalı (1000).</summary>
    IAsyncEnumerable<ObjectInfo> ListAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Erişilebilirlik + kova var + varsayılan şifreleme (<c>Encryption=required</c> iken).</summary>
    Task<StorageHealth> CheckAsync(CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------------------------------
// Virüs tarayıcı portu
// ---------------------------------------------------------------------------------------------------------------------

public enum ScanOutcome
{
    Clean,
    Infected,
    Error,
}

public sealed record ScanResult(ScanOutcome Outcome, string? Signature = null)
{
    public static ScanResult Clean { get; } = new(ScanOutcome.Clean);

    public static ScanResult Error { get; } = new(ScanOutcome.Error);

    public static ScanResult Infected(string? signature) => new(ScanOutcome.Infected, signature);
}

/// <summary>Tarama yalnız <b>ham baytlar</b> üzerindedir; sonuç metriği/günlüğü dosya adı içermez.</summary>
public interface IFileScanner
{
    /// <summary><c>false</c>: tarayıcı yapılandırılmamış (<c>Provider=none</c>) → <c>scan_status = skipped</c>.</summary>
    bool IsEnabled { get; }

    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------------------------------
// Yükleme hazırlama (faz 1; işlem yok)
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Multipart'tan gelen ham parça: ad ve beyan edilen tür <b>ham girdidir</b> (temizlenir/danışmandır).</summary>
public sealed record UploadPart(string? RawFileName, string? DeclaredContentType, Stream Content);

/// <summary>Doğrulanmış, geçici dosyaya alınmış yükleme. Kapatılınca geçici dosya <b>her yolda</b> silinir.</summary>
public interface IStagedUpload : IAsyncDisposable
{
    string Name { get; }

    string Extension { get; }

    /// <summary>Kanonik tür (içerik imzasından).</summary>
    string ContentType { get; }

    long SizeBytes { get; }

    string Sha256 { get; }

    ScanStatus ScanStatus { get; }

    /// <summary><c>OnInfected=quarantine</c> ile zararlı bulundu: nesne yazılır ama <c>state = quarantined</c>.</summary>
    bool Quarantine { get; }

    /// <summary>Başa sarılmış, aranabilir okuma akışı.</summary>
    Stream OpenRead();
}

public interface IUploadStager
{
    /// <summary>
    /// Ad temizleme + uzantı allow-list (bayt okumadan) → geçici dosyaya kopyalama (bayt sayımı + SHA-256 aynı geçişte; <c>MaxFileMb</c> aşılınca hemen kes) → boş kontrol →
    /// içerik imzası → tarayıcı. Hata sonucunda geçici dosya zaten silinmiştir.
    /// </summary>
    Task<Result<IStagedUpload>> StageAsync(UploadPart part, CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------------------------------
// Erişim kararı (TEK koruma sınıfı) ve hız/eşzamanlılık sınırı
// ---------------------------------------------------------------------------------------------------------------------

public enum AttachmentAccessMode
{
    Read,
    Write,
}

/// <summary>Yetkilendirilmiş hedef kayıt türü bilgisi.</summary>
public sealed record AttachmentTargetInfo(string RecordType, string Module, string ReadPermission, string WritePermission);

/// <summary>Yetkilendirilmiş dosya (kaydın kendi izniyle).</summary>
public sealed record AuthorizedFile(FileAttachment File, AttachmentTargetInfo Target);

/// <summary>
/// <b>Tek koruma sınıfı</b> (D3): tüm Files handler'ları erişim kararını buradan alır (mimari test zorlar). Karar sırası: tür bilinmiyorsa 400; <b>izin</b>
/// (eksikse liste/yükleme/yeniden adlandırma/silme 403, dosya kimliğiyle <i>okuma</i> 404); kapı modülü kapalıysa 403 <c>plan.module_disabled</c> (okuma dahil);
/// hedef kayıt yoksa/silinmişse/başka kiracıdaysa 404 (<c>file.record_not_found</c> ya da dosya kimliğiyle <c>not_found</c>). İzin varlıktan <b>önce</b> denetlenir.
/// </summary>
public interface IAttachmentAccess
{
    /// <summary>Kayıt (tür + kimlik) bazlı: liste (Read) ve yükleme (Write).</summary>
    Task<Result<AttachmentTargetInfo>> AuthorizeRecordAsync(string? recordType, Guid recordId, AttachmentAccessMode mode, CancellationToken ct);

    /// <summary>Dosya kimliği bazlı: meta/indirme (Read; izinsiz/yok 404), yeniden adlandırma/silme (Write; izinsiz 403). Silinmiş dosya yok sayılır.</summary>
    Task<Result<AuthorizedFile>> AuthorizeFileAsync(Guid fileId, AttachmentAccessMode mode, CancellationToken ct);
}

/// <summary>Kullanıcı başına parça/indirme hız sınırı (<c>files-upload</c>/<c>files-download</c>) ve kiracı başına eşzamanlı yükleme kapısı (bellek içi, süreç başına).</summary>
public interface IFilesRateLimiter
{
    /// <summary>Yükleme parçası başına bir jeton (kullanıcı <c>sub</c> başına dakikada <c>Files:RateLimiting:Upload</c>).</summary>
    bool TryAcquireUploadPart(Guid userId);

    bool TryAcquireDownload(Guid userId);

    /// <summary>Kiracı başına <c>ConcurrentUploadsPerTenant</c> ve genel <c>MaxConcurrentUploads</c>; <c>null</c> = sınır aşıldı (429). Dönen nesne serbest bırakılır.</summary>
    IDisposable? TryEnterUpload(Guid tenantId);
}

// ---------------------------------------------------------------------------------------------------------------------
// Kalıcılık
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Kotaya sayılan toplam kullanım (kesin <c>SUM</c>).</summary>
public sealed record StorageUsage(long UsedBytes, long FileCount, long QuarantinedCount, long MissingCount, IReadOnlyList<RecordTypeUsage> ByRecordType);

public sealed record RecordTypeUsage(string RecordType, long FileCount, long SizeBytes);

public interface IFileAttachmentRepository
{
    void Add(FileAttachment file);

    /// <summary>İzlenen varlık (kiracı filtresi altında); <c>state = deleted</c> dahil.</summary>
    Task<FileAttachment?> GetAsync(Guid id, CancellationToken ct);

    void AddAccess(FileAccessLogEntry entry);
}

public interface IFilesReadStore
{
    Task<PagedResult<FileRow>> ListAsync(string recordType, Guid recordId, PagedQuery paging, CancellationToken ct);

    Task<StorageUsage> GetUsageAsync(CancellationToken ct);
}

/// <summary>Liste satırı (proje edilmiş; <c>state != deleted</c>).</summary>
public sealed record FileRow(
    Guid Id,
    string RecordType,
    Guid RecordId,
    string Name,
    string Extension,
    string ContentType,
    long SizeBytes,
    string Sha256,
    FileState State,
    Guid UploadedByUserId,
    DateTime UploadedAt,
    DateTime? RenamedAt);
