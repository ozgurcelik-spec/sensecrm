using System.Globalization;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Infrastructure.Persistence;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Web.Controllers;
using Crm.Shared.Web.Localization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Crm.Shared.Web.ErrorHandling;

/// <summary>Beklenmeyen hataları RFC 9457 ProblemDetails'e çevirir (yerelleştirilmiş); prod'da detay sızdırmaz.</summary>
public sealed partial class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IErrorLocalizer localizer,
    IOptions<ProblemMappingOptions> options) : IExceptionHandler
{
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (type, code, errors, status) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            Log.Unhandled(logger, exception, httpContext.Request.Path);
        }
        else
        {
            Log.RequestError(logger, exception, status, httpContext.Request.Path);
        }

        var error = new Error(code, type);
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status == ClientClosedRequest ? localizer.MessageOrText(ProblemTitleKeys.Cancelled) : localizer.Title(type),
            Detail = status >= StatusCodes.Status500InternalServerError && !options.Value.IncludeExceptionDetails
                ? localizer.Message(error)
                : status >= StatusCodes.Status500InternalServerError ? exception.Message : localizer.Message(error),
            Type = options.Value.TypeBaseUrl + code,
            Instance = httpContext.Request.Path,
        };
        problem.Extensions[ProblemDetailsDefaults.CodeExtension] = code;
        problem.Extensions[ProblemDetailsDefaults.TraceIdExtension] = httpContext.TraceIdentifier;
        if (errors is not null)
        {
            problem.Extensions[ProblemDetailsDefaults.ErrorsExtension] = errors;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, MediaTypes.ProblemJson, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private (ErrorType Type, string Code, Dictionary<string, string[]>? Errors, int Status) Map(Exception ex) => ex switch
    {
        ValidationException v => (ErrorType.Validation, ErrorCodes.ValidationError, ToErrorDictionary(v), StatusCodes.Status400BadRequest),
        TenantMismatchException => (ErrorType.Forbidden, ErrorCodes.TenantMismatch, null, StatusCodes.Status403Forbidden),
        UnauthorizedAccessException => (ErrorType.Unauthorized, ErrorCodes.Unauthenticated, null, StatusCodes.Status401Unauthorized),
        OperationCanceledException => (ErrorType.Failure, ErrorCodes.RequestCancelled, null, ClientClosedRequest),
        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (ErrorType.Conflict, ErrorCodes.ConcurrencyConflict, null, StatusCodes.Status409Conflict),
        _ => (ErrorType.Failure, ErrorCodes.InternalError, null, StatusCodes.Status500InternalServerError),
    };

    /// <summary>Özel mesajlar kaynak anahtarı olarak yazılır ("tenant.invalid_slug"); burada çevrilir. FluentValidation'ın kendi mesajları zaten kültüre göredir.</summary>
    private Dictionary<string, string[]> ToErrorDictionary(ValidationException v) =>
        v.Errors
            .GroupBy(e => PropertyNames.ToCamelCase(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(FormatValidationMessage).Distinct().ToArray(), StringComparer.Ordinal);

    /// <summary>
    /// Kaynak anahtarını çevirir ve FluentValidation yer tutucularını ({MinLength}, {MaxLength}, {PropertyName} …) doldurur.
    /// Kaynaklarda camelCase ({minLength}) veya PascalCase yazım kabul edilir.
    /// </summary>
    private string FormatValidationMessage(ValidationFailure failure)
    {
        var text = localizer.MessageOrText(failure.ErrorMessage);
        if (failure.FormattedMessagePlaceholderValues is not { Count: > 0 } values)
        {
            return text;
        }

        foreach (var (key, value) in values)
        {
            var formatted = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            text = text
                .Replace(LocalizationFormat.Open + key + LocalizationFormat.Close, formatted, StringComparison.Ordinal)
                .Replace(LocalizationFormat.Open + PropertyNames.ToCamelCase(key) + LocalizationFormat.Close, formatted, StringComparison.Ordinal);
        }

        return text;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1300, Level = LogLevel.Error, Message = "Unhandled exception for {Path}")]
        public static partial void Unhandled(ILogger logger, Exception exception, string path);

        [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Request error ({Status}) for {Path}")]
        public static partial void RequestError(ILogger logger, Exception exception, int status, string path);
    }
}

public static class PropertyNames
{
    private const char PathSeparator = '.';
    private const char WordSeparator = '_';

    /// <summary>"OwnerFirstName" → "owner_first_name" (kaynak anahtarı biçimi).</summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && name[i - 1] != PathSeparator)
            {
                builder.Append(WordSeparator);
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var parts = name.Split(PathSeparator);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                parts[i] = char.ToLowerInvariant(parts[i][0]) + parts[i][1..];
            }
        }

        return string.Join(PathSeparator, parts);
    }
}
