using System.Globalization;
using System.Runtime.CompilerServices;

namespace Sense.Crm.Shared.Kernel;

/// <summary>Domain kurucularında ön koşul denetimleri (programlama hataları; kullanıcı hatası değil).</summary>
public static class Guard
{
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class =>
        value ?? throw new ArgumentNullException(name);

    public static string NotEmpty(string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(KernelMessages.ValueCannotBeEmpty, name);
        }

        return value.Trim();
    }

    public static string MaxLength(string value, int max, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value.Length > max)
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, KernelMessages.ValueTooLong, max), name);
        }

        return value;
    }

    public static Guid NotDefault(Guid value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value == Guid.Empty ? throw new ArgumentException(KernelMessages.EmptyGuid, name) : value;

    public static decimal NotNegative(decimal value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < 0 ? throw new ArgumentOutOfRangeException(name, KernelMessages.NegativeValue) : value;

    public static int Positive(int value, [CallerArgumentExpression(nameof(value))] string? name = null) =>
        value <= 0 ? throw new ArgumentOutOfRangeException(name, KernelMessages.NonPositiveValue) : value;

    public static T InRange<T>(T value, T min, T max, [CallerArgumentExpression(nameof(value))] string? name = null)
        where T : IComparable<T> =>
        value.CompareTo(min) < 0 || value.CompareTo(max) > 0
            ? throw new ArgumentOutOfRangeException(name, string.Format(CultureInfo.InvariantCulture, KernelMessages.OutOfRange, min, max))
            : value;

    public static void Against(bool condition, string message, [CallerArgumentExpression(nameof(condition))] string? name = null)
    {
        if (condition)
        {
            throw new ArgumentException(message, name);
        }
    }
}
