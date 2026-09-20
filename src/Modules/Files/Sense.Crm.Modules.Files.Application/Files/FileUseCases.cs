using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Contracts;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Files;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Files.Application.Files;

// Dosya ekleri kullanım senaryoları (docs/plan/m8c-dosya-ekleri.md). Her istek gerekçeli [AnyAuthenticatedUser] taşır (kayıt türü istekten geldiği için
// izin çalışma anında verilir) ve her handler ilk iş IAttachmentAccess (tek koruma sınıfı) çağırır; mimari test zorlar.

/// <summary>Liste/yükleme isteklerinin ortak <c>recordType</c>/<c>recordId</c> doğrulaması (<c>errors.recordType</c>, <c>errors.recordId</c>).</summary>
internal static class RecordRules
{
    public static IRuleBuilderOptions<T, string?> ValidRecordType<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(AttachmentRecordTypes.IsKnown).WithMessage(FilesErrors.InvalidRecordType);

    public static IRuleBuilderOptions<T, Guid?> ValidRecordId<T>(this IRuleBuilder<T, Guid?> rule) =>
        rule.Must(id => id is { } value && value != Guid.Empty).WithMessage(FilesErrors.InvalidRecordId);
}

// ---------------------------------------------------------------------------------------------------------------------
// Liste ve meta
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>GET /files?recordType&amp;recordId&amp;q&amp;sort&amp;page&amp;pageSize</c> — kaydın <b>okuma</b> izni.</summary>
[AnyAuthenticatedUser("Yetki handler içinde: kaydın kendi okuma izni (IAttachmentAccess, çalışma anında)")]
public sealed record ListFilesQuery(PagedQuery Paging, string? RecordType, Guid? RecordId) : IQuery<PagedResult<FileDto>>;

public sealed class ListFilesValidator : AbstractValidator<ListFilesQuery>
{
    public ListFilesValidator()
    {
        RuleFor(x => x.RecordType).ValidRecordType();
        RuleFor(x => x.RecordId).ValidRecordId();
    }
}

public sealed class ListFilesHandler(IAttachmentAccess access, IFilesReadStore store, IMemberLookup members) : IQueryHandler<ListFilesQuery, PagedResult<FileDto>>
{
    public async Task<Result<PagedResult<FileDto>>> Handle(ListFilesQuery query, CancellationToken cancellationToken)
    {
        var authorized = await access.AuthorizeRecordAsync(query.RecordType, query.RecordId!.Value, AttachmentAccessMode.Read, cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        var page = await store.ListAsync(query.RecordType!, query.RecordId.Value, query.Paging, cancellationToken).ConfigureAwait(false);
        var names = await members.GetDisplayNamesAsync(page.Items.Select(i => i.UploadedByUserId).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
        return page.Map(row => FileMapper.ToDto(row, names));
    }
}

/// <summary><c>GET /files/{id}</c> — kaydın okuma izni; izinsiz veya yok: <c>404 not_found</c> (varlık sızmaz).</summary>
[AnyAuthenticatedUser("Yetki handler içinde: dosyanın kaydının okuma izni (IAttachmentAccess, çalışma anında)")]
public sealed record GetFileQuery(Guid Id) : IQuery<FileDto>;

public sealed class GetFileHandler(IAttachmentAccess access, IMemberLookup members) : IQueryHandler<GetFileQuery, FileDto>
{
    public async Task<Result<FileDto>> Handle(GetFileQuery query, CancellationToken cancellationToken)
    {
        var authorized = await access.AuthorizeFileAsync(query.Id, AttachmentAccessMode.Read, cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        var file = authorized.Value.File;
        var names = await members.GetDisplayNamesAsync([file.UploadedByUserId], cancellationToken).ConfigureAwait(false);
        return FileMapper.ToDto(FileMapper.ToRow(file), names);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Yükleme (faz 0 + faz 2)
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Faz 0 — erken red: izin/plan/varlık/<c>tenant.suspended</c> hataları <b>hiçbir bayt okunmadan</b> döner. Komut olduğundan salt okunur/askıda kiracıda
/// <c>EntitlementBehaviour</c> 403 <c>tenant.suspended</c> verir.
/// </summary>
[AnyAuthenticatedUser("Yetki handler içinde: kaydın kendi yazma izni (IAttachmentAccess, çalışma anında)")]
public sealed record AuthorizeUploadCommand(string? RecordType, Guid? RecordId) : ICommand;

public sealed class AuthorizeUploadValidator : AbstractValidator<AuthorizeUploadCommand>
{
    public AuthorizeUploadValidator()
    {
        RuleFor(x => x.RecordType).ValidRecordType();
        RuleFor(x => x.RecordId).ValidRecordId();
    }
}

public sealed class AuthorizeUploadHandler(IAttachmentAccess access) : ICommandHandler<AuthorizeUploadCommand>
{
    public async Task<Result> Handle(AuthorizeUploadCommand command, CancellationToken cancellationToken)
    {
        var authorized = await access.AuthorizeRecordAsync(command.RecordType, command.RecordId!.Value, AttachmentAccessMode.Write, cancellationToken).ConfigureAwait(false);
        return authorized.IsFailure ? authorized.Error : Result.Success();
    }
}

/// <summary>Depo hatası (erişilemez / şifreleme doğrulanamadı): yakalanır ve <c>503 file.storage_unavailable</c>'a çevrilir.</summary>
public sealed class StorageUnavailableException : Exception
{
    public StorageUnavailableException()
    {
    }

    public StorageUnavailableException(string message)
        : base(message)
    {
    }

    public StorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Faz 2 — işleme (UoW işlemi açık): (i) erişim yeniden (TOCTOU), (ii) <b>kota</b> (<c>ILimitGuard</c> storage; kiracı başına istişari kilit), (iii) <c>PutAsync</c>,
/// (iv) satır + <c>FileAttached</c> outbox olayı, (v) işlem <c>commit</c>. PUT hatası → işlem geri alınır, <c>503 file.storage_unavailable</c>, satır yok;
/// PUT sonrası kaydetme hatası → nesne <c>DeleteAsync</c> ile telafi edilir (başarısızsa yetim kalır, uzlaştırma siler).
/// </summary>
[AnyAuthenticatedUser("Yetki handler içinde: kaydın kendi yazma izni (IAttachmentAccess, çalışma anında)")]
[NoPlanLimit("Dosya eki kayıt sayısına değil depolama kotasına sayılır; kota handler'da ILimitGuard ile zorlanır")]
public sealed record AttachFileCommand(string? RecordType, Guid? RecordId, IStagedUpload Upload) : ICommand<FileDto>;

public sealed class AttachFileValidator : AbstractValidator<AttachFileCommand>
{
    public AttachFileValidator()
    {
        RuleFor(x => x.RecordType).ValidRecordType();
        RuleFor(x => x.RecordId).ValidRecordId();
    }
}

public sealed partial class AttachFileHandler(
    IAttachmentAccess access,
    ILimitGuard limits,
    IFileStorage storage,
    IFileAttachmentRepository files,
    IFilesUnitOfWork unitOfWork,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user,
    IMemberLookup members,
    TimeProvider clock,
    ILogger<AttachFileHandler> logger) : ICommandHandler<AttachFileCommand, FileDto>
{
    public async Task<Result<FileDto>> Handle(AttachFileCommand command, CancellationToken cancellationToken)
    {
        var authorized = await access.AuthorizeRecordAsync(command.RecordType, command.RecordId!.Value, AttachmentAccessMode.Write, cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var upload = command.Upload;

        // Kota: sert, kesin, önbelleksiz. Kiracı başına istişari kilit Files UoW'sunun transaction'ında alınır ve commit'e kadar tutulur.
        var quota = await limits.EnsureAsync(new LimitDemand(LimitKeys.Storage, LimitKeys.StorageModule, checked((int)upload.SizeBytes)), cancellationToken).ConfigureAwait(false);
        if (quota.IsFailure)
        {
            return MapQuota(quota.Error, upload.SizeBytes);
        }

        var now = clock.GetUtcNow();
        var fileId = Guid.CreateVersion7();
        var key = ObjectKey.For(tenant.TenantId, now.UtcDateTime.Year, fileId);

        try
        {
            await using var content = upload.OpenRead();
            await storage.PutAsync(key, content, upload.SizeBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPutFailed(logger, ex, fileId, tenant.TenantId, upload.SizeBytes);
            return FilesErrors.StorageUnavailableError();
        }

        var file = FileAttachment.Create(
            fileId,
            tenant.TenantId,
            command.RecordType!,
            command.RecordId.Value,
            upload.Name,
            upload.Extension,
            upload.ContentType,
            upload.SizeBytes,
            upload.Sha256,
            key.ToString(),
            upload.ScanStatus,
            upload.Quarantine,
            userId,
            now.UtcDateTime);
        files.Add(file);
        outbox.Enqueue(new FileAttached(tenant.TenantId, fileId, command.RecordType!, command.RecordId.Value, upload.SizeBytes, upload.ContentType, userId));

        try
        {
            // Açık kaydetme: hata (ör. benzersiz ihlali) burada görülür ve nesne telafi edilir; işlem sonra UnitOfWorkBehaviour'da commit olur.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompensateAsync(key, fileId).ConfigureAwait(false);
            throw;
        }

        var names = await members.GetDisplayNamesAsync([userId], cancellationToken).ConfigureAwait(false);
        return FileMapper.ToDto(FileMapper.ToRow(file), names);
    }

    /// <summary><c>plan.limit_exceeded</c> (limit=storage) → <c>file.quota_exceeded</c> (<c>maxBytes</c>, <c>usedBytes</c>, <c>requestedBytes</c>).</summary>
    private static Error MapQuota(Error error, long requested)
    {
        if (error.Code != EntitlementErrors.LimitExceeded || error.Args is not { } args)
        {
            return error;
        }

        var max = args.TryGetValue(EntitlementErrors.MaxArg, out var m) ? Convert.ToInt64(m, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var used = args.TryGetValue(EntitlementErrors.UsedArg, out var u) ? Convert.ToInt64(u, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return FilesErrors.QuotaExceededError(max, used, requested);
    }

    private async Task CompensateAsync(ObjectKey key, Guid fileId)
    {
        try
        {
            await storage.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nesne yetim kalır; uzlaştırma işi (grace sonrası) siler.
            LogCompensationFailed(logger, ex, fileId, tenant.TenantId);
        }
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "File {FileId} of tenant {TenantId} ({SizeBytes} bytes) could not be written to object storage")]
    private static partial void LogPutFailed(ILogger logger, Exception exception, Guid fileId, Guid tenantId, long sizeBytes);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Error, Message = "Compensating delete of object for file {FileId} of tenant {TenantId} failed; reconciliation will remove the orphan")]
    private static partial void LogCompensationFailed(ILogger logger, Exception exception, Guid fileId, Guid tenantId);
}

// ---------------------------------------------------------------------------------------------------------------------
// Yeniden adlandırma ve silme
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>PATCH /files/{id}</c>: yazma izni; ad temizlenir; <b>uzantı değişemez</b>; ad çakışması denetlenmez. Yalnız <c>ready|quarantined|missing</c>.
/// </summary>
[AnyAuthenticatedUser("Yetki handler içinde: dosyanın kaydının yazma izni (IAttachmentAccess, çalışma anında)")]
public sealed record RenameFileCommand(Guid Id, string? Name) : ICommand;

public sealed class RenameFileValidator : AbstractValidator<RenameFileCommand>
{
    public RenameFileValidator() =>
        RuleFor(x => x.Name).NotEmpty().MaximumLength(2048).WithMessage(FilesErrors.InvalidName);
}

public sealed class RenameFileHandler(IAttachmentAccess access, TimeProvider clock) : ICommandHandler<RenameFileCommand>
{
    public async Task<Result> Handle(RenameFileCommand command, CancellationToken cancellationToken)
    {
        var authorized = await access.AuthorizeFileAsync(command.Id, AttachmentAccessMode.Write, cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        var file = authorized.Value.File;
        var name = FileNamePolicy.Sanitize(command.Name);
        if (name is null)
        {
            return FilesErrors.NameInvalidError();
        }

        var extension = FileNamePolicy.GetExtension(name);
        if (!string.Equals(extension, file.Extension, StringComparison.Ordinal))
        {
            return FilesErrors.ExtensionChangeNotAllowedError();
        }

        if (FileNamePolicy.HasDangerousDoubleExtension(name))
        {
            return FilesErrors.TypeNotAllowedError(extension);
        }

        file.Rename(name, clock.GetUtcNow().UtcDateTime);
        return Result.Success();
    }
}

/// <summary>
/// <c>DELETE /files/{id}</c>: yazma izni; <b>yumuşak</b> silme (kotadan anında düşer; nesne <c>SoftDeleteRetentionDays</c> sonra Worker işiyle fiziksel silinir);
/// <c>FileDeleted</c> outbox olayı.
/// </summary>
[AnyAuthenticatedUser("Yetki handler içinde: dosyanın kaydının yazma izni (IAttachmentAccess, çalışma anında)")]
public sealed record DeleteFileCommand(Guid Id) : ICommand;

public sealed class DeleteFileHandler(IAttachmentAccess access, ICurrentUser user, IIntegrationEventOutbox outbox, TimeProvider clock) : ICommandHandler<DeleteFileCommand>
{
    public async Task<Result> Handle(DeleteFileCommand command, CancellationToken cancellationToken)
    {
        var authorized = await access.AuthorizeFileAsync(command.Id, AttachmentAccessMode.Write, cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        var file = authorized.Value.File;
        if (!file.SoftDelete(user.UserId, clock.GetUtcNow().UtcDateTime))
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        outbox.Enqueue(new FileDeleted(file.TenantId, file.Id, file.RecordType, file.RecordId, user.UserId));
        return Result.Success();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Kullanım ve sınırlar
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>GET /files/usage</c> (<c>org.settings.manage</c>): canlı, önbelleksiz.</summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record GetFilesUsageQuery : IQuery<FilesUsageDto>;

public sealed class GetFilesUsageHandler(IFilesReadStore store, ITenantEntitlements entitlements, ITenantContext tenant, TimeProvider clock)
    : IQueryHandler<GetFilesUsageQuery, FilesUsageDto>
{
    public async Task<Result<FilesUsageDto>> Handle(GetFilesUsageQuery query, CancellationToken cancellationToken)
    {
        var usage = await store.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        return new FilesUsageDto(
            usage.UsedBytes,
            usage.FileCount,
            snapshot.MaxStorageBytes,
            usage.QuarantinedCount,
            usage.MissingCount,
            usage.ByRecordType.Select(r => new FilesUsageByRecordTypeDto(r.RecordType, r.FileCount, r.SizeBytes)).ToList(),
            clock.GetUtcNow());
    }
}

/// <summary><c>GET /files/limits</c>: kiracı-bağımsız yapılandırma (web ön doğrulaması); hiçbir kayıt verisi döndürmez, bu yüzden <c>IAttachmentAccess</c> gerekmez.</summary>
[AnyAuthenticatedUser("Kiracı-bağımsız yapılandırma: yalnız yükleme sınırları ve izinli uzantılar; hiçbir kayıt/dosya verisi döndürmez")]
public sealed record GetFilesLimitsQuery : IQuery<FileLimitsDto>;

public sealed class GetFilesLimitsHandler(IOptions<FilesOptions> options) : IQueryHandler<GetFilesLimitsQuery, FileLimitsDto>
{
    public Task<Result<FileLimitsDto>> Handle(GetFilesLimitsQuery query, CancellationToken cancellationToken)
    {
        var upload = options.Value.Upload;
        var allowed = FilesOptionsValidator.EffectiveExtensions(upload);
        var extensions = FileTypeCatalog.AllExtensions.Where(allowed.Contains).ToList();
        var previewable = FileTypeCatalog.PreviewableExtensions.Where(allowed.Contains).ToList();
        Result<FileLimitsDto> result = new FileLimitsDto(upload.MaxFileBytes, upload.MaxFilesPerRequest, extensions, previewable);
        return Task.FromResult(result);
    }
}
