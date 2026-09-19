namespace Sense.Crm.Shared.Kernel.ValueObjects;

/// <summary>
/// Loose E.164-ish phone normalization: keeps a leading '+' (international format), strips spaces/parens/dashes,
/// and drops a local leading trunk '0' when no '+' is present (e.g. "0532 123 45 67" -> "5321234567").
/// Not a full E.164 validator - just enough to store phone numbers consistently.
/// </summary>
public static class PhoneNormalizer
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hasLeadingPlus = value.TrimStart().StartsWith('+');
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        if (!hasLeadingPlus && digits.Length > 1 && digits[0] == '0')
        {
            digits = digits[1..];
        }

        return hasLeadingPlus ? string.Concat("+", digits) : digits;
    }
}
