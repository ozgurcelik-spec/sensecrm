using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Files.Domain;

/// <summary>Files hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m8c-dosya-ekleri.md "Hata kodları").</summary>
public static class FilesErrors
{
    public const string UploadInvalid = "file.upload_invalid";
    public const string TooManyFiles = "file.too_many_files";
    public const string Empty = "file.empty";
    public const string NameInvalid = "file.name_invalid";
    public const string ExtensionChangeNotAllowed = "file.extension_change_not_allowed";
    public const string TooLarge = "file.too_large";
    public const string TypeNotAllowed = "file.type_not_allowed";
    public const string ContentMismatch = "file.content_mismatch";
    public const string Infected = "file.infected";
    public const string QuotaExceeded = "file.quota_exceeded";
    public const string Quarantined = "file.quarantined";
    public const string ContentMissing = "file.content_missing";
    public const string ScanUnavailable = "file.scan_unavailable";
    public const string StorageUnavailable = "file.storage_unavailable";
    public const string RecordNotFound = "file.record_not_found";

    // Doğrulama mesajı anahtarları
    public const string InvalidRecordType = "validation.file_record_type";
    public const string InvalidRecordId = "validation.file_record_id";
    public const string InvalidDisposition = "validation.file_disposition";
    public const string InvalidName = "validation.file_name";

    // Argüman adları
    public const string MaxArg = "max";
    public const string MaxBytesArg = "maxBytes";
    public const string UsedBytesArg = "usedBytes";
    public const string RequestedBytesArg = "requestedBytes";
    public const string ExtensionArg = "extension";

    public static Error UploadInvalidError() => Error.Validation(UploadInvalid);

    public static Error TooManyFilesError(int max) => Error.Validation(TooManyFiles, (MaxArg, max));

    public static Error EmptyError() => Error.Validation(Empty);

    public static Error NameInvalidError() => Error.Validation(NameInvalid);

    public static Error ExtensionChangeNotAllowedError() => Error.Validation(ExtensionChangeNotAllowed);

    public static Error TooLargeError(long maxBytes) => Error.PayloadTooLarge(TooLarge, (MaxBytesArg, maxBytes));

    public static Error TypeNotAllowedError(string extension) => Error.UnsupportedMediaType(TypeNotAllowed, (ExtensionArg, extension));

    public static Error ContentMismatchError() => Error.Rule(ContentMismatch);

    public static Error InfectedError() => Error.Rule(Infected);

    public static Error QuotaExceededError(long maxBytes, long usedBytes, long requestedBytes) =>
        Error.Payment(QuotaExceeded, (MaxBytesArg, maxBytes), (UsedBytesArg, usedBytes), (RequestedBytesArg, requestedBytes));

    public static Error QuarantinedError() => Error.Conflict(Quarantined);

    public static Error ContentMissingError() => Error.Gone(ContentMissing);

    public static Error ScanUnavailableError() => Error.Unavailable(ScanUnavailable);

    public static Error StorageUnavailableError() => Error.Unavailable(StorageUnavailable);

    public static Error RecordNotFoundError() => Error.NotFound(RecordNotFound);
}

/// <summary>Sütun uzunlukları ve genel sabitler.</summary>
public static class FilesLimits
{
    public const int NameMaxLength = 200;
    public const int ExtensionMaxLength = 16;
    public const int ContentTypeMaxLength = 100;
    public const int RecordTypeMaxLength = 16;
    public const int StorageKeyMaxLength = 80;
    public const int StateMaxLength = 12;
    public const int ScanStatusMaxLength = 10;
    public const int ActionMaxLength = 10;
    public const int Sha256Length = 64;
}
