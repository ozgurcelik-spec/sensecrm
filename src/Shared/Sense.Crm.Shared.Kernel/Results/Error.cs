namespace Sense.Crm.Shared.Kernel.Results;

public enum ErrorType
{
    /// <summary>Beklenmeyen/iç hata → 500.</summary>
    Failure = 0,

    /// <summary>Girdi doğrulama → 400.</summary>
    Validation = 1,

    /// <summary>Kaynak yok veya kapsam dışı → 404.</summary>
    NotFound = 2,

    /// <summary>Çakışma / eşzamanlılık → 409.</summary>
    Conflict = 3,

    /// <summary>Yetki yok → 403.</summary>
    Forbidden = 4,

    /// <summary>Kimlik doğrulanmamış → 401.</summary>
    Unauthorized = 5,

    /// <summary>İş kuralı ihlali → 422.</summary>
    Rule = 6,

    /// <summary>Abonelik/modül kapalı → 402.</summary>
    Payment = 7,

    /// <summary>İstek hız sınırını aştı → 429.</summary>
    TooManyRequests = 8,
}

/// <summary>
/// Yalnızca kod ve tür taşıyan hata. Kod aynı zamanda yerelleştirme kaynak anahtarıdır
/// ("leave.insufficient_balance"); mesaj sunum katmanında kültüre göre üretilir. <see cref="Args"/> mesaj şablonu parametreleridir.
/// </summary>
public sealed record Error(string Code, ErrorType Type = ErrorType.Failure, IReadOnlyDictionary<string, object?>? Args = null)
{
    public static readonly Error None = new(string.Empty);

    public static readonly Error NullValue = new(ErrorCodes.NullValue, ErrorType.Validation);

    public static Error Validation(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.Validation, ToArgs(args));

    public static Error NotFound(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.NotFound, ToArgs(args));

    public static Error Conflict(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.Conflict, ToArgs(args));

    public static Error Forbidden(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.Forbidden, ToArgs(args));

    public static Error Unauthorized(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.Unauthorized, ToArgs(args));

    public static Error Rule(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.Rule, ToArgs(args));

    public static Error Payment(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.Payment, ToArgs(args));

    public static Error Failure(string code, params (string Key, object? Value)[] args) => new(code, ErrorType.Failure, ToArgs(args));

    private static IReadOnlyDictionary<string, object?>? ToArgs((string Key, object? Value)[] args) =>
        args.Length == 0 ? null : args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
}

/// <summary>
/// Modüller arası ortak hata kodları (web istemcisiyle sözleşme; değiştirilmez). Modül kodları kendi `XErrors` sınıflarında.
/// </summary>
public static class ErrorCodes
{
    public const string NullValue = "general.null_value";
    public const string ValidationError = "validation";
    public const string Unauthenticated = "auth.unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string TenantMismatch = "tenant.mismatch";
    public const string ConcurrencyConflict = "general.concurrency_conflict";
    public const string RequestCancelled = "general.request_cancelled";
    public const string InternalError = "general.internal_error";
    public const string RateLimitExceeded = "general.rate_limit_exceeded";
}
