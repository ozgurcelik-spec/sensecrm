using Sense.Crm.Modules.Files.Domain;

namespace Sense.Crm.Modules.Files.Application;

/// <summary>Dosya yanıtı (<c>FileDto</c>). <c>state</c> ∈ <c>ready|quarantined|missing</c> (<c>deleted</c> asla listelenmez).</summary>
/// <param name="UploadedByName"><c>IMemberLookup</c> ile toplu çözülür; çözülemezse yazılmaz.</param>
/// <param name="UpdatedAt">Yalnız yeniden adlandırıldıysa.</param>
/// <param name="CanPreview">Önizlenebilir tür <b>ve</b> <c>ready</c>.</param>
public sealed record FileDto(
    Guid Id,
    string RecordType,
    Guid RecordId,
    string Name,
    string Extension,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string State,
    Guid UploadedByUserId,
    string? UploadedByName,
    DateTimeOffset UploadedAt,
    DateTimeOffset? UpdatedAt,
    bool CanPreview);

/// <summary>Çoklu yükleme başarısızlığı: <c>fileName</c> temizlenmiş addır (günlüğe yazılmaz).</summary>
public sealed record UploadFailureDto(string FileName, string Code, IReadOnlyDictionary<string, object?>? Args);

/// <summary>Yükleme yanıtı: <c>201</c> (hepsi başarılı) ya da <c>200</c> (kısmi başarı).</summary>
public sealed record UploadResultDto(IReadOnlyList<FileDto> Items, IReadOnlyList<UploadFailureDto> Failed);

public sealed record FilesUsageByRecordTypeDto(string RecordType, long FileCount, long SizeBytes);

/// <summary><c>GET /files/usage</c>: canlı, önbelleksiz. <c>maxBytes</c> yalnız sonlu limitte.</summary>
public sealed record FilesUsageDto(
    long UsedBytes,
    long FileCount,
    long? MaxBytes,
    long QuarantinedCount,
    long MissingCount,
    IReadOnlyList<FilesUsageByRecordTypeDto> ByRecordType,
    DateTimeOffset AsOf);

/// <summary><c>GET /files/limits</c>: web ön doğrulaması içindir; sunucu her zaman yetkilidir.</summary>
public sealed record FileLimitsDto(
    long MaxFileBytes,
    int MaxFilesPerRequest,
    IReadOnlyList<string> AllowedExtensions,
    IReadOnlyList<string> PreviewableExtensions);

public static class FileMapper
{
    public static FileDto ToDto(FileRow row, IReadOnlyDictionary<Guid, string> names) =>
        new(
            row.Id,
            row.RecordType,
            row.RecordId,
            row.Name,
            row.Extension,
            row.ContentType,
            row.SizeBytes,
            row.Sha256,
            FileWire.Of(row.State),
            row.UploadedByUserId,
            names.TryGetValue(row.UploadedByUserId, out var name) ? name : null,
            new DateTimeOffset(DateTime.SpecifyKind(row.UploadedAt, DateTimeKind.Utc)),
            row.RenamedAt is { } renamed ? new DateTimeOffset(DateTime.SpecifyKind(renamed, DateTimeKind.Utc)) : null,
            CanPreview(row.Extension, row.State));

    public static FileRow ToRow(FileAttachment file) =>
        new(
            file.Id,
            file.RecordType,
            file.RecordId,
            file.Name,
            file.Extension,
            file.ContentType,
            file.SizeBytes,
            file.Sha256,
            file.State,
            file.UploadedByUserId,
            file.UploadedAt,
            file.RenamedAt);

    public static bool CanPreview(string extension, FileState state) =>
        state == FileState.Ready && FileTypeCatalog.TryGet(extension, out var type) && type.Previewable;
}
