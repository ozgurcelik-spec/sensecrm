namespace Sense.Crm.Shared.Kernel;

/// <summary>
/// Geliştiriciye yönelik (kullanıcıya gösterilmeyen) istisna mesajları. Kullanıcıya dönen metinler
/// hata kodları üzerinden sunum katmanında yerelleştirilir; burada yalnızca programlama hataları vardır.
/// </summary>
public static class KernelMessages
{
    public const string SuccessResultCannotCarryError = "A successful result cannot carry an error.";
    public const string FailureResultRequiresError = "A failed result must carry an error.";
    public const string ValueOfFailedResult = "The value of a failed result cannot be read: {0}";
    public const string ValueCannotBeEmpty = "Value cannot be empty.";
    public const string ValueTooLong = "Value cannot exceed {0} characters.";
    public const string EmptyGuid = "An empty Guid is not allowed.";
    public const string NegativeValue = "Value cannot be negative.";
    public const string NonPositiveValue = "Value must be positive.";
    public const string OutOfRange = "Value must be between {0} and {1}.";
    public const string CurrencyMustBeIso4217 = "Currency must be a 3-letter ISO 4217 code.";
    public const string CurrencyMismatch = "Currency mismatch: {0} vs {1}.";
    public const string DivisionByZero = "Division by zero.";
    public const string EndBeforeStart = "End date cannot be before start date.";
    public const string InvalidEmail = "Invalid e-mail address.";
}
