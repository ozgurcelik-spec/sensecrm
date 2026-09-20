using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Files.Domain;

/// <summary>Dosya durumu (tel değerleri küçük harf: <c>ready|quarantined|missing|deleted</c>).</summary>
public enum FileState
{
    Ready,

    /// <summary>Tarayıcı zararlı buldu (<c>OnInfected=quarantine</c>): nesne ve satır saklanır, indirilemez, yalnız silinebilir; kotaya sayılır.</summary>
    Quarantined,

    /// <summary>Satır var, nesne yok (uzlaştırma işaretledi): indirme <c>410 file.content_missing</c>, silinebilir.</summary>
    Missing,

    /// <summary>Yumuşak silinmiş: listelenmez, kotadan düşer; nesne bekleme süresi sonunda fiziksel silinir.</summary>
    Deleted,
}

/// <summary>Tarama sonucu (<c>skipped|clean|infected</c>).</summary>
public enum ScanStatus
{
    Skipped,
    Clean,
    Infected,
}

/// <summary>Erişim günlüğü eylemi.</summary>
public enum FileAccessAction
{
    Download,
    Preview,
}

/// <summary>Enum tel değerleri (küçük harf) — kalıcılık ve HTTP aynı dizgeyi kullanır.</summary>
public static class FileWire
{
    public static string Of(FileState state) => state switch
    {
        FileState.Ready => "ready",
        FileState.Quarantined => "quarantined",
        FileState.Missing => "missing",
        _ => "deleted",
    };

    public static FileState ParseState(string value) => value switch
    {
        "ready" => FileState.Ready,
        "quarantined" => FileState.Quarantined,
        "missing" => FileState.Missing,
        "deleted" => FileState.Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown file state."),
    };

    public static string Of(ScanStatus status) => status switch
    {
        ScanStatus.Skipped => "skipped",
        ScanStatus.Clean => "clean",
        _ => "infected",
    };

    public static ScanStatus ParseScan(string value) => value switch
    {
        "skipped" => ScanStatus.Skipped,
        "clean" => ScanStatus.Clean,
        "infected" => ScanStatus.Infected,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown scan status."),
    };

    public static string Of(FileAccessAction action) => action == FileAccessAction.Preview ? "preview" : "download";

    public static FileAccessAction ParseAction(string value) => value switch
    {
        "download" => FileAccessAction.Download,
        "preview" => FileAccessAction.Preview,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown access action."),
    };
}

/// <summary>
/// Bir kayda (firma, kişi, potansiyel müşteri, fırsat, aktivite, talep, teklif, sipariş, kampanya) eklenmiş dosya. Kayıt bağı <b>yumuşaktır</b> (FK yok;
/// modüller arası). <c>Id</c> nesne anahtarındaki <c>fileId</c>'dir. Silme yumuşaktır (<c>state = deleted</c>; global <c>is_deleted</c> filtresi
/// <b>kullanılmaz</b>, durum açık sorgulanır). Kişisel veri: ad (<c>SensitiveFields</c> maskelemez — kiracı içi iş verisi; imhada <c>audit</c> adımıyla silinir);
/// <c>sha256</c>/<c>storage_key</c> denetimde <b>maskelenir</b> (değerleri yazılmaz; yalnız "değişti").
/// </summary>
public sealed class FileAttachment : TenantAggregateRoot<Guid>, IAuditLogged
{
    public static IReadOnlySet<string> SensitiveFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { nameof(Sha256), nameof(StorageKey) };

    private FileAttachment()
    {
    }

    private FileAttachment(
        Guid id,
        Guid tenantId,
        string recordType,
        Guid recordId,
        string name,
        string extension,
        string contentType,
        long sizeBytes,
        string sha256,
        string storageKey,
        FileState state,
        ScanStatus scanStatus,
        Guid uploadedByUserId,
        DateTime nowUtc)
        : base(id, tenantId)
    {
        RecordType = recordType;
        RecordId = recordId;
        Name = name;
        Extension = extension;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        State = state;
        ScanStatus = scanStatus;
        UploadedByUserId = uploadedByUserId;
        UploadedAt = nowUtc;
    }

    public string RecordType { get; private set; } = string.Empty;

    public Guid RecordId { get; private set; }

    /// <summary>Temizlenmiş görünen ad (uzantı dahil).</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Küçük harf, noktasız.</summary>
    public string Extension { get; private set; } = string.Empty;

    /// <summary><b>Kanonik</b> tür (içerik imzasından; istemci beyanı saklanmaz).</summary>
    public string ContentType { get; private set; } = string.Empty;

    /// <summary>Sunucu ölçümü.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>Sunucu hesabı (küçük harf onaltılık).</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary><c>ObjectKey.ToString()</c>; benzersiz.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public FileState State { get; private set; }

    public ScanStatus ScanStatus { get; private set; }

    public Guid UploadedByUserId { get; private set; }

    public DateTime UploadedAt { get; private set; }

    /// <summary>Yalnız yeniden adlandırıldıysa dolar (<c>FileDto.updatedAt</c>).</summary>
    public DateTime? RenamedAt { get; private set; }

    public DateTime? DeletedAt { get; private set; }

    /// <summary><c>null</c>: sistem (kayıt-yok süpürmesi) ya da hâlâ silinmemiş.</summary>
    public Guid? DeletedByUserId { get; private set; }

    /// <summary>Hedef kayıt silinmiş/yok bulunduğunda ilk görüldüğü an (kayıt-yok süpürmesi; geri gelirse temizlenir).</summary>
    public DateTime? RecordMissingSince { get; private set; }

    /// <summary>Kotaya sayılan durumlar: <c>ready</c>, <c>quarantined</c>, <c>missing</c> (nesne var sayılır).</summary>
    public bool CountsTowardQuota => State != FileState.Deleted;

    public static FileAttachment Create(
        Guid id,
        Guid tenantId,
        string recordType,
        Guid recordId,
        string name,
        string extension,
        string contentType,
        long sizeBytes,
        string sha256,
        string storageKey,
        ScanStatus scanStatus,
        bool quarantined,
        Guid uploadedByUserId,
        DateTime nowUtc) =>
        new(
            Guard.NotDefault(id),
            Guard.NotDefault(tenantId),
            Guard.NotEmpty(recordType),
            Guard.NotDefault(recordId),
            Guard.NotEmpty(name),
            extension,
            Guard.NotEmpty(contentType),
            sizeBytes,
            Guard.NotEmpty(sha256),
            Guard.NotEmpty(storageKey),
            quarantined ? FileState.Quarantined : FileState.Ready,
            scanStatus,
            uploadedByUserId,
            nowUtc);

    /// <summary>Yeniden adlandırma (uzantı değişmez; handler denetler). Yalnız <c>ready|quarantined|missing</c>.</summary>
    public bool Rename(string newName, DateTime nowUtc)
    {
        if (State == FileState.Deleted || string.Equals(Name, newName, StringComparison.Ordinal))
        {
            return false;
        }

        Name = Guard.NotEmpty(newName);
        RenamedAt = nowUtc;
        return true;
    }

    /// <summary>Yumuşak silme (kotadan anında düşer). Zaten silinmişse <c>false</c>.</summary>
    public bool SoftDelete(Guid? userId, DateTime nowUtc)
    {
        if (State == FileState.Deleted)
        {
            return false;
        }

        State = FileState.Deleted;
        DeletedAt = nowUtc;
        DeletedByUserId = userId;
        return true;
    }

    /// <summary>Uzlaştırma: nesne yok (ya da boyut uyuşmuyor). Yalnız <c>ready</c> iken.</summary>
    public bool MarkMissing()
    {
        if (State != FileState.Ready)
        {
            return false;
        }

        State = FileState.Missing;
        return true;
    }

    /// <summary>Uzlaştırma: nesne geri geldi (geri yükleme). Yalnız <c>missing</c> iken.</summary>
    public bool MarkReady()
    {
        if (State != FileState.Missing)
        {
            return false;
        }

        State = FileState.Ready;
        return true;
    }

    /// <summary>Kayıt-yok süpürmesi: hedef kayıt yok. İlk görüldüğü an korunur.</summary>
    public bool MarkRecordMissing(DateTime nowUtc)
    {
        if (RecordMissingSince is not null)
        {
            return false;
        }

        RecordMissingSince = nowUtc;
        return true;
    }

    /// <summary>Hedef kayıt geri geldi (geri yükleme): işaret kalkar.</summary>
    public bool ClearRecordMissing()
    {
        if (RecordMissingSince is null)
        {
            return false;
        }

        RecordMissingSince = null;
        return true;
    }
}

/// <summary>
/// Dosya erişim günlüğü (append-only; <c>IAuditLogged</c> <b>değil</b>): "kim neyi indirdi" izi. IP, ad ve içerik <b>yoktur</b> (veri minimizasyonu).
/// Aralıklı isteklerde yalnız ilk parça (Range yok ya da <c>bytes=0-</c>) kaydedilir.
/// </summary>
public sealed class FileAccessLogEntry : TenantEntity<Guid>
{
    private FileAccessLogEntry()
    {
    }

    private FileAccessLogEntry(Guid id, Guid tenantId, Guid fileId, Guid userId, FileAccessAction action, DateTime occurredAt)
        : base(id, tenantId)
    {
        FileId = fileId;
        UserId = userId;
        Action = action;
        OccurredAt = occurredAt;
    }

    public DateTime OccurredAt { get; private set; }

    public Guid FileId { get; private set; }

    public Guid UserId { get; private set; }

    public FileAccessAction Action { get; private set; }

    public static FileAccessLogEntry Create(Guid tenantId, Guid fileId, Guid userId, FileAccessAction action, DateTime nowUtc) =>
        new(Guid.CreateVersion7(), Guard.NotDefault(tenantId), Guard.NotDefault(fileId), userId, action, nowUtc);
}
