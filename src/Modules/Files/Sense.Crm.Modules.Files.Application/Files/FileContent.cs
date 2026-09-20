using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Files.Application.Files;

/// <summary><c>Range</c> başlığı çözümlemesi (saf; RFC 7233 tek aralık).</summary>
public enum RangeKind
{
    /// <summary>Başlık yok / <c>bytes</c> dışı birim / <b>birden çok aralık</b> → tam gövde (200).</summary>
    None,

    /// <summary>Geçerli tek aralık (206).</summary>
    Satisfiable,

    /// <summary>Geçersiz sözdizimi ya da nesneye sığmayan aralık (416).</summary>
    NotSatisfiable,
}

public readonly record struct RangeParseResult(RangeKind Kind, long Start, long End);

public static class HttpRange
{
    public static RangeParseResult Parse(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return new RangeParseResult(RangeKind.None, 0, 0);
        }

        var trimmed = header.Trim();
        const string prefix = "bytes=";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RangeParseResult(RangeKind.None, 0, 0);
        }

        var spec = trimmed[prefix.Length..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal))
        {
            return new RangeParseResult(RangeKind.None, 0, 0);
        }

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || length <= 0)
        {
            return NotSatisfiable();
        }

        var first = spec[..dash].Trim();
        var second = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            // Sonek aralığı: son n bayt.
            if (!TryParse(second, out var suffix) || suffix <= 0)
            {
                return NotSatisfiable();
            }

            return new RangeParseResult(RangeKind.Satisfiable, Math.Max(length - suffix, 0), length - 1);
        }

        if (!TryParse(first, out var start) || start >= length)
        {
            return NotSatisfiable();
        }

        if (second.Length == 0)
        {
            return new RangeParseResult(RangeKind.Satisfiable, start, length - 1);
        }

        if (!TryParse(second, out var end) || end < start)
        {
            return NotSatisfiable();
        }

        return new RangeParseResult(RangeKind.Satisfiable, start, Math.Min(end, length - 1));
    }

    private static RangeParseResult NotSatisfiable() => new(RangeKind.NotSatisfiable, 0, 0);

    private static bool TryParse(string value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    /// <summary><c>If-None-Match</c> (virgüllü liste, <c>*</c>, zayıf <c>W/</c> öneki) bu güçlü ETag ile eşleşiyor mu.</summary>
    public static bool MatchesETag(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        foreach (var candidate in ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var value = candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate;
            if (value == "*" || string.Equals(value, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public enum FileContentKind
{
    /// <summary>200 + tam gövde.</summary>
    Full,

    /// <summary>206 + aralık.</summary>
    Partial,

    /// <summary>304 (gövde yok).</summary>
    NotModified,

    /// <summary>416.</summary>
    RangeNotSatisfiable,
}

/// <summary>Açılmış dosya içeriği: meta + (gerekirse) depo akışı. <b>Kapatılmalıdır</b> (denetleyici yanıta kaydeder).</summary>
public sealed class FileContentResponse(
    FileContentKind kind,
    string name,
    string contentType,
    string sha256,
    long totalLength,
    bool inline,
    StoredObject? content) : IAsyncDisposable
{
    public FileContentKind Kind { get; } = kind;

    public string Name { get; } = name;

    /// <summary>Kanonik tür (veritabanından; istemci beyanı değil).</summary>
    public string ContentType { get; } = contentType;

    public string Sha256 { get; } = sha256;

    public long TotalLength { get; } = totalLength;

    public bool Inline { get; } = inline;

    public StoredObject? Content { get; } = content;

    public string ETag => "\"" + Sha256 + "\"";

    public async ValueTask DisposeAsync()
    {
        if (Content is not null)
        {
            await Content.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// <c>GET /files/{id}/content?disposition=attachment|inline</c>: kaydın <b>okuma</b> izni; <c>quarantined</c> → 409, <c>missing</c> → 410, depo erişilemez → 503;
/// önizlenemeyen türde <c>inline</c> → 400 (<c>errors.disposition</c>). ETag/<c>If-None-Match</c> → 304; tek <c>Range</c> → 206 (depodan); geçersiz → 416; çoklu → tam gövde.
/// Erişim günlüğüne yalnız <b>ilk parça</b> (Range yok ya da başlangıç 0) yazılır: bir indirme = bir satır.
/// </summary>
[AnyAuthenticatedUser("Yetki handler içinde: dosyanın kaydının okuma izni (IAttachmentAccess, çalışma anında)")]
public sealed record OpenFileContentQuery(Guid Id, string? Disposition, string? Range, string? IfNoneMatch) : IQuery<FileContentResponse>;

public sealed class OpenFileContentValidator : AbstractValidator<OpenFileContentQuery>
{
    public OpenFileContentValidator() =>
        RuleFor(x => x.Disposition)
            .Must(d => d is null or "" || string.Equals(d, "attachment", StringComparison.OrdinalIgnoreCase) || string.Equals(d, "inline", StringComparison.OrdinalIgnoreCase))
            .WithMessage(FilesErrors.InvalidDisposition);
}

public sealed partial class OpenFileContentHandler(
    IAttachmentAccess access,
    IFileStorage storage,
    IFileAttachmentRepository files,
    IFilesUnitOfWork unitOfWork,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock,
    ILogger<OpenFileContentHandler> logger) : IQueryHandler<OpenFileContentQuery, FileContentResponse>
{
    public async Task<Result<FileContentResponse>> Handle(OpenFileContentQuery query, CancellationToken cancellationToken)
    {
        var authorized = await access.AuthorizeFileAsync(query.Id, AttachmentAccessMode.Read, cancellationToken).ConfigureAwait(false);
        if (authorized.IsFailure)
        {
            return authorized.Error;
        }

        var file = authorized.Value.File;
        var inline = string.Equals(query.Disposition, "inline", StringComparison.OrdinalIgnoreCase);
        if (inline && !(FileTypeCatalog.TryGet(file.Extension, out var type) && type.Previewable))
        {
            // Yalnız önizlenebilir türler satır içi sunulur (txt/csv/Office asla).
            throw new ValidationException([new ValidationFailure(nameof(query.Disposition), FilesErrors.InvalidDisposition)]);
        }

        if (file.State == FileState.Quarantined)
        {
            return FilesErrors.QuarantinedError();
        }

        if (file.State == FileState.Missing)
        {
            return FilesErrors.ContentMissingError();
        }

        // Kurcalanmış satır başka kiracının nesnesini okutamaz: anahtar her kullanımdan önce yeniden doğrulanır (500 değil 404 + güvenlik günlüğü).
        if (!ObjectKey.TryParse(file.StorageKey, out var key) || key.TenantId != tenant.TenantId || key.FileId != file.Id)
        {
            LogKeyMismatch(logger, file.Id, tenant.TenantId);
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var etag = "\"" + file.Sha256 + "\"";
        if (HttpRange.MatchesETag(query.IfNoneMatch, etag))
        {
            return new FileContentResponse(FileContentKind.NotModified, file.Name, file.ContentType, file.Sha256, file.SizeBytes, inline, null);
        }

        var range = HttpRange.Parse(query.Range, file.SizeBytes);
        if (range.Kind == RangeKind.NotSatisfiable)
        {
            return new FileContentResponse(FileContentKind.RangeNotSatisfiable, file.Name, file.ContentType, file.Sha256, file.SizeBytes, inline, null);
        }

        var byteRange = range.Kind == RangeKind.Satisfiable ? new ByteRange(range.Start, range.End) : (ByteRange?)null;
        StoredObject? stored;
        try
        {
            stored = await storage.GetAsync(key, byteRange, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStorageFailed(logger, ex, file.Id, tenant.TenantId);
            return FilesErrors.StorageUnavailableError();
        }

        if (stored is null)
        {
            // Satır var, nesne yok: uzlaştırma henüz işaretlememiş olabilir; kullanıcıya 410 (satır değiştirilmez).
            return FilesErrors.ContentMissingError();
        }

        try
        {
            // Bir indirme = bir satır: Range devamı (başlangıç > 0) satır eklemez.
            if (stored.RangeStart == 0 && user.UserId is { } userId)
            {
                var action = inline ? FileAccessAction.Preview : FileAccessAction.Download;
                files.AddAccess(FileAccessLogEntry.Create(tenant.TenantId, file.Id, userId, action, clock.GetUtcNow().UtcDateTime));
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await stored.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new FileContentResponse(
            range.Kind == RangeKind.Satisfiable ? FileContentKind.Partial : FileContentKind.Full,
            file.Name,
            file.ContentType,
            file.Sha256,
            file.SizeBytes,
            inline,
            stored);
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Warning, Message = "Security: storage key of file {FileId} does not match tenant {TenantId}; content not served")]
    private static partial void LogKeyMismatch(ILogger logger, Guid fileId, Guid tenantId);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Error, Message = "Object storage read failed for file {FileId} of tenant {TenantId}")]
    private static partial void LogStorageFailed(ILogger logger, Exception exception, Guid fileId, Guid tenantId);
}
