using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Web.Localization;

namespace Sense.Crm.Shared.Web.Controllers;

/// <summary>Route sabitleri: api/v{version}/{module}/…</summary>
public static class ApiRoutes
{
    public const string VersionedBase = "api/v{version:apiVersion}";
    public const string DefaultVersion = "1.0";
    public const string IdParam = "{id:guid}";
}

/// <summary>
/// Tüm modül controller'larının tabanı (klasik MVC). Dispatcher'a delege eder, Result'ı HTTP'ye (ProblemDetails, yerelleştirilmiş) eşler.
/// </summary>
// [Produces] bilinçli olarak yok: o filtre hata yanıtlarının application/problem+json içerik türünü de ezerdi.
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    private IDispatcher? _dispatcher;
    private IErrorLocalizer? _localizer;
    private ProblemMappingOptions? _problemOptions;

    protected IDispatcher Dispatcher => _dispatcher ??= HttpContext.RequestServices.GetRequiredService<IDispatcher>();

    protected IErrorLocalizer Localizer => _localizer ??= HttpContext.RequestServices.GetRequiredService<IErrorLocalizer>();

    private ProblemMappingOptions ProblemOptions => _problemOptions ??= HttpContext.RequestServices.GetRequiredService<IOptions<ProblemMappingOptions>>().Value;

    /// <summary>Başarılı sonuçta 200 + değer; hata durumunda ProblemDetails.</summary>
    protected IActionResult FromResult<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(result.Error);

    /// <summary>Başarılı sonuçta 204; hata durumunda ProblemDetails.</summary>
    protected IActionResult FromResult(Result result) =>
        result.IsSuccess ? NoContent() : Problem(result.Error);

    /// <summary>Oluşturma: 201 + Location.</summary>
    protected IActionResult Created<T>(Result<T> result, string actionName, object routeValues) =>
        result.IsSuccess ? CreatedAtAction(actionName, routeValues, result.Value) : Problem(result.Error);

    /// <summary>Oluşturma: tekil GET ucu olmayan kaynaklar için Location'sız 201 + gövde.</summary>
    protected IActionResult CreatedWithBody<T>(Result<T> result) =>
        result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : Problem(result.Error);

    protected IActionResult Problem(Error error)
    {
        var status = HttpStatusMap.For(error.Type);
        var problem = new ProblemDetails
        {
            Title = Localizer.Title(error.Type),
            Detail = Localizer.Message(error),
            Status = status,
            Type = ProblemOptions.TypeBaseUrl + error.Code,
            Instance = HttpContext.Request.Path,
        };
        problem.Extensions[ProblemDetailsDefaults.CodeExtension] = error.Code;
        problem.Extensions[ProblemDetailsDefaults.TraceIdExtension] = HttpContext.TraceIdentifier;
        if (error.Args is not null)
        {
            problem.Extensions[ProblemDetailsDefaults.ArgsExtension] = error.Args;
        }

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { MediaTypes.ProblemJson },
        };
    }
}

public static class MediaTypes
{
    public const string Json = "application/json";
    public const string ProblemJson = "application/problem+json";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Csv = "text/csv";
    public const string Pdf = "application/pdf";
}

public static class HttpStatusMap
{
    public static int For(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Rule => StatusCodes.Status422UnprocessableEntity,
        ErrorType.Payment => StatusCodes.Status402PaymentRequired,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorType.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorType.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        ErrorType.Gone => StatusCodes.Status410Gone,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };
}
