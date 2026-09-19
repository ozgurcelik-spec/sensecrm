using System.Net.Mail;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Shared.Kernel.ValueObjects;

/// <summary>Küçük harfe normalize e-posta adresi.</summary>
public sealed class EmailAddress : ValueObject
{
    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
        {
            return false;
        }

        return MailAddress.TryCreate(value.Trim(), out var parsed) && parsed.Address == value.Trim();
    }

    public static EmailAddress Of(string value)
    {
        Guard.Against(!IsValid(value), KernelMessages.InvalidEmail);
        return new EmailAddress(value.Trim().ToLowerInvariant());
    }

    public override string ToString() => Value;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
