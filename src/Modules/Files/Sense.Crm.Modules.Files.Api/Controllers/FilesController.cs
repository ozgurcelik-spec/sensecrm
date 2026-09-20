using System.Globalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Application.Files;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Files.Api.Controllers;

/// <summary>Files uç noktaları (docs/plan/m8c-dosya-ekleri.md). Taban: /api/v1. Yetki her isteğin handler'ında <c>IAttachmentAccess</c> ile (kaydın kendi izni) verilir.</summary>
public static class FilesRoutes
{
    public const string Files = ApiRoutes.VersionedBase + "/files";
}

/// <summary><c>PATCH /files/{id}</c> gövdesi: yeni ad (uzantı değişemez).</summary>
public sealed record RenameFileRequest(string? Name);

/// <summary>Multipart yüklemede MVC'nin form değerlerini/dosyalarını <b>okumasını engeller</b> (gövdeyi denetleyici akışla, <c>MultipartReader</c> ile okur).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

/// <summary>
/// Dosya ekleri. Yükleme/indirme <b>API üzerinden akışla</b> (tarayıcıya presigned URL verilmez, D5). Yükleme iki fazlıdır: faz 0 erken red (gövde okunmadan),
/// faz 1 hazırlama (geçici dosya + doğrulama, <c>IUploadStager</c>), faz 2 işleme (<c>AttachFileCommand</c>, kota + depo + satır). Her <c>file</c> parçası bağımsız işlenir.
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(FilesRoutes.Files)]
[Authorize]
public sealed class FilesController(
    IOptions<FilesOptions> options,
    IUploadStager stager,
    IFilesRateLimiter limiter,
    ITenantContext tenant,
    ICurrentUser user) : ApiControllerBase
{
    private const string FilePartName = "file";
    private const int MaxBoundaryLength = 128;

    [HttpGet]
    [ProducesResponseType<PagedResult<FileDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagedQuery paging, [FromQuery] string? recordType, [FromQuery] Guid? recordId, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListFilesQuery(paging, recordType, recordId), ct));

    /// <summary>Depolama kullanımı (canlı, önbelleksiz; <c>org.settings.manage</c>).</summary>
    [HttpGet("usage")]
    [ProducesResponseType<FilesUsageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Usage(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetFilesUsageQuery(), ct));

    /// <summary>Yükleme sınırları ve izinli uzantılar (web ön doğrulaması; sunucu her zaman yetkilidir).</summary>
    [HttpGet("limits")]
    [ProducesResponseType<FileLimitsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Limits(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetFilesLimitsQuery(), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<FileDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetFileQuery(id), ct));

    [HttpPatch(ApiRoutes.IdParam)]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameFileRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new RenameFileCommand(id, request.Name), ct));

    [HttpDelete(ApiRoutes.IdParam)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteFileCommand(id), ct));

    // -----------------------------------------------------------------------------------------------------------------
    // Yükleme
    // -----------------------------------------------------------------------------------------------------------------

    [HttpPost]
    [DisableFormValueModelBinding]
    [ProducesResponseType<UploadResultDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload([FromQuery] string? recordType, [FromQuery] Guid? recordId, CancellationToken ct)
    {
        var upload = options.Value.Upload;

        // Faz 0 — erken red: izin/plan/varlık/tenant.suspended/doğrulama hataları hiçbir bayt okunmadan döner.
        var authorized = await Dispatcher.Send(new AuthorizeUploadCommand(recordType, recordId), ct);
        if (authorized.IsFailure)
        {
            return Problem(authorized.Error);
        }

        if (Request.ContentLength is { } declared && declared > upload.MaxRequestBytes)
        {
            return Problem(FilesErrors.TooLargeError(upload.MaxRequestBytes));
        }

        // Kestrel gövde sınırı yalnız bu uçta MaxRequestMb'ye çıkarılır (genel 1 MB sınırı korunur).
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = upload.MaxRequestBytes;
        }

        using var slot = limiter.TryEnterUpload(tenant.TenantId);
        if (slot is null)
        {
            return Problem(new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests));
        }

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 and <= MaxBoundaryLength } boundary)
        {
            return Problem(FilesErrors.UploadInvalidError());
        }

        var items = new List<FileDto>();
        var failed = new List<UploadFailureDto>();
        Error? firstError = null;
        var parts = 0;

        try
        {
            var reader = new MultipartReader(boundary, Request.Body);
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                var disposition = section.GetContentDispositionHeader();
                if (disposition is null || !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, FilePartName, StringComparison.Ordinal))
                {
                    return Problem(FilesErrors.UploadInvalidError());
                }

                var rawName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
                if (++parts > upload.MaxFilesPerRequest)
                {
                    var tooMany = FilesErrors.TooManyFilesError(upload.MaxFilesPerRequest);
                    firstError ??= tooMany;
                    failed.Add(ToFailure(rawName, tooMany));
                    break;
                }

                var outcome = await ProcessPartAsync(recordType, recordId, rawName, section, ct);
                if (outcome.File is { } file)
                {
                    items.Add(file);
                }
                else
                {
                    firstError ??= outcome.Error!;
                    failed.Add(ToFailure(rawName, outcome.Error!));
                }
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Problem(FilesErrors.TooLargeError(upload.MaxRequestBytes));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException && !ct.IsCancellationRequested)
        {
            // Bozuk multipart gövdesi / kopan istek.
            return Problem(FilesErrors.UploadInvalidError());
        }

        if (parts == 0)
        {
            return Problem(FilesErrors.UploadInvalidError());
        }

        if (items.Count == 0)
        {
            return Problem(firstError!);
        }

        var result = new UploadResultDto(items, failed);
        if (failed.Count > 0)
        {
            return Ok(result);
        }

        if (items.Count == 1)
        {
            Response.Headers.Location = $"/api/v1/files/{items[0].Id:D}";
        }

        return StatusCode(StatusCodes.Status201Created, result);
    }

    private async Task<PartOutcome> ProcessPartAsync(string? recordType, Guid? recordId, string? rawName, MultipartSection section, CancellationToken ct)
    {
        if (user.UserId is not { } userId || !limiter.TryAcquireUploadPart(userId))
        {
            return new PartOutcome(null, new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests));
        }

        // Faz 1 — hazırlama (işlem yok): geçici dosya + doğrulama. Geçici dosya her yolda silinir (await using).
        var staged = await stager.StageAsync(new UploadPart(rawName, section.ContentType, section.Body), ct);
        if (staged.IsFailure)
        {
            return new PartOutcome(null, staged.Error);
        }

        await using var upload = staged.Value;

        // Faz 2 — işleme: kota + depo + satır (UoW işlemi açık).
        var attached = await Dispatcher.Send(new AttachFileCommand(recordType, recordId, upload), ct);
        return attached.IsFailure ? new PartOutcome(null, attached.Error) : new PartOutcome(attached.Value, null);
    }

    private static UploadFailureDto ToFailure(string? rawName, Error error) =>
        new(FileNamePolicy.Sanitize(rawName) ?? string.Empty, error.Code, error.Args);

    private sealed record PartOutcome(FileDto? File, Error? Error);

    // -----------------------------------------------------------------------------------------------------------------
    // İndirme / önizleme
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Dosya baytları (<c>?disposition=attachment|inline</c>, varsayılan attachment). <c>Range</c> tek aralık (206), <c>If-None-Match</c> (304); gövde belleğe alınmadan akıtılır,
    /// istemci koparsa depo akışı kapanır. <c>inline</c> yalnız önizlenebilir türlerde.
    /// </summary>
    [HttpGet("{id:guid}/content")]
    public async Task<IActionResult> Content(Guid id, [FromQuery] string? disposition, CancellationToken ct)
    {
        if (user.UserId is { } userId && !limiter.TryAcquireDownload(userId))
        {
            return Problem(new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests));
        }

        var opened = await Dispatcher.Query(
            new OpenFileContentQuery(id, disposition, Request.Headers.Range.ToString(), Request.Headers.IfNoneMatch.ToString()), ct);
        if (opened.IsFailure)
        {
            return Problem(opened.Error);
        }

        var content = opened.Value;
        Response.RegisterForDisposeAsync(content);

        var headers = Response.Headers;
        headers.CacheControl = "private, no-store";
        headers.ETag = content.ETag;
        headers.AcceptRanges = "bytes";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        switch (content.Kind)
        {
            case FileContentKind.NotModified:
                return StatusCode(StatusCodes.Status304NotModified);
            case FileContentKind.RangeNotSatisfiable:
                headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes */{content.TotalLength}");
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            default:
                break;
        }

        var stored = content.Content!;
        ApplyContentHeaders(content);
        Response.ContentType = content.ContentType;
        Response.ContentLength = stored.ContentLength;
        if (content.Kind == FileContentKind.Partial)
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {stored.RangeStart}-{stored.RangeEnd}/{stored.TotalLength}");
        }

        try
        {
            await stored.Content.CopyToAsync(Response.Body, 81920, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // İstemci koptu: depo akışı Response.RegisterForDisposeAsync ile kapanır.
        }

        return new EmptyResult();
    }

    /// <summary>
    /// <c>Content-Disposition</c> (ASCII yedek + <c>filename*</c> UTF-8; ASCII yedekte <c>"</c>, <c>\</c>, CR/LF ve ASCII dışı <c>_</c> olur → başlık enjeksiyonu yok).
    /// Satır içi önizleme başlıkları: <c>X-Frame-Options: SAMEORIGIN</c> (açıkça koyulan güvenlik başlığı kazanır), tür başına CSP.
    /// </summary>
    private void ApplyContentHeaders(FileContentResponse content)
    {
        var disposition = new ContentDispositionHeaderValue(content.Inline ? "inline" : "attachment")
        {
            FileName = FileNamePolicy.ToAsciiFallback(content.Name),
        };
        disposition.FileNameStar = content.Name;
        Response.Headers.ContentDisposition = disposition.ToString();

        if (!content.Inline)
        {
            return;
        }

        Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        Response.Headers.ContentSecurityPolicy = content.ContentType.StartsWith("image/", StringComparison.Ordinal)
            ? "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'self'; sandbox"
            : "default-src 'none'; frame-ancestors 'self'";
    }
}
