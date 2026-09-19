using Microsoft.Extensions.Localization;
using Sense.Crm.Shared.Infrastructure;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Web.Localization;

/// <summary>Hata kodu → kültüre göre kullanıcı mesajı. Parametreler {name} biçiminde şablona gömülür.</summary>
public interface IErrorLocalizer
{
    string Message(Error error);

    string Title(ErrorType type);

    /// <summary>Anahtar gibi görünen (nokta içeren, boşluksuz) metinleri çevirir; aksi halde olduğu gibi döner.</summary>
    string MessageOrText(string keyOrText);
}

public sealed class ErrorLocalizer(IStringLocalizer<SharedResource> localizer) : IErrorLocalizer
{
    public string Message(Error error)
    {
        var template = localizer[error.Code];
        var text = template.ResourceNotFound ? error.Code : template.Value;
        if (error.Args is null)
        {
            return text;
        }

        foreach (var (key, value) in error.Args)
        {
            text = text.Replace(LocalizationFormat.Open + key + LocalizationFormat.Close, value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }

        return text;
    }

    public string Title(ErrorType type) => localizer[ProblemTitleKeys.For(type)];

    public string MessageOrText(string keyOrText)
    {
        if (string.IsNullOrWhiteSpace(keyOrText) || keyOrText.Contains(' ', StringComparison.Ordinal) || !keyOrText.Contains('.', StringComparison.Ordinal))
        {
            return keyOrText;
        }

        var value = localizer[keyOrText];
        return value.ResourceNotFound ? keyOrText : value.Value;
    }
}

public static class LocalizationFormat
{
    public const string Open = "{";
    public const string Close = "}";
}

/// <summary>ProblemDetails başlık kaynak anahtarları.</summary>
public static class ProblemTitleKeys
{
    public const string Validation = "problem.title.validation";
    public const string Unauthorized = "problem.title.unauthorized";
    public const string Forbidden = "problem.title.forbidden";
    public const string NotFound = "problem.title.not_found";
    public const string Conflict = "problem.title.conflict";
    public const string Rule = "problem.title.rule";
    public const string Payment = "problem.title.payment";
    public const string Failure = "problem.title.failure";
    public const string Cancelled = "problem.title.cancelled";
    public const string TooManyRequests = "problem.title.too_many_requests";

    public static string For(ErrorType type) => type switch
    {
        ErrorType.Validation => Validation,
        ErrorType.Unauthorized => Unauthorized,
        ErrorType.Forbidden => Forbidden,
        ErrorType.NotFound => NotFound,
        ErrorType.Conflict => Conflict,
        ErrorType.Rule => Rule,
        ErrorType.Payment => Payment,
        ErrorType.TooManyRequests => TooManyRequests,
        _ => Failure,
    };
}
