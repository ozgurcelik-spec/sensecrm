using System.Globalization;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Shared.Kernel.ValueObjects;

/// <summary>ISO 4217 para birimi kodları.</summary>
public static class Currencies
{
    public const string TRY = "TRY";
    public const string USD = "USD";
    public const string EUR = "EUR";
    public const string GBP = "GBP";
    public const int CodeLength = 3;

    /// <summary>Active ISO 4217 currency codes (funds/precious-metal/testing codes excluded).</summary>
    private static readonly HashSet<string> Iso4217 = new(StringComparer.Ordinal)
    {
        "AED", "AFN", "ALL", "AMD", "ANG", "AOA", "ARS", "AUD", "AWG", "AZN", "BAM", "BBD", "BDT", "BGN", "BHD", "BIF", "BMD", "BND", "BOB",
        "BRL", "BSD", "BTN", "BWP", "BYN", "BZD", "CAD", "CDF", "CHF", "CLP", "CNY", "COP", "CRC", "CUP", "CVE", "CZK", "DJF", "DKK", "DOP",
        "DZD", "EGP", "ERN", "ETB", "EUR", "FJD", "FKP", "GBP", "GEL", "GHS", "GIP", "GMD", "GNF", "GTQ", "GYD", "HKD", "HNL", "HTG", "HUF",
        "IDR", "ILS", "INR", "IQD", "IRR", "ISK", "JMD", "JOD", "JPY", "KES", "KGS", "KHR", "KMF", "KPW", "KRW", "KWD", "KYD", "KZT", "LAK",
        "LBP", "LKR", "LRD", "LSL", "LYD", "MAD", "MDL", "MGA", "MKD", "MMK", "MNT", "MOP", "MRU", "MUR", "MVR", "MWK", "MXN", "MYR", "MZN",
        "NAD", "NGN", "NIO", "NOK", "NPR", "NZD", "OMR", "PAB", "PEN", "PGK", "PHP", "PKR", "PLN", "PYG", "QAR", "RON", "RSD", "RUB", "RWF",
        "SAR", "SBD", "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SOS", "SRD", "SSP", "STN", "SVC", "SYP", "SZL", "THB", "TJS", "TMT", "TND",
        "TOP", "TRY", "TTD", "TWD", "TZS", "UAH", "UGX", "USD", "UYU", "UZS", "VES", "VND", "VUV", "WST", "XAF", "XCD", "XOF", "XPF", "YER",
        "ZAR", "ZMW", "ZWG",
    };

    /// <summary>True for an active ISO 4217 code, exact (uppercase) match only.</summary>
    public static bool IsKnown(string? code) => code is { Length: CodeLength } && Iso4217.Contains(code);
}

/// <summary>Para tutarı. Ara hesaplar <see cref="StoragePrecision"/> hane, çıktı <see cref="DisplayPrecision"/> hane HALF_UP (kuruş).</summary>
public sealed class Money : ValueObject, IComparable<Money>
{
    public const int StoragePrecision = 4;
    public const int DisplayPrecision = 2;
    private const string DisplayFormat = "{0:0.00} {1}";

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money Zero(string currency) => new(0m, currency);

    public static Money Of(decimal amount, string currency)
    {
        var cur = Guard.NotEmpty(currency).ToUpperInvariant();
        Guard.Against(cur.Length != Currencies.CodeLength, KernelMessages.CurrencyMustBeIso4217);
        return new Money(decimal.Round(amount, StoragePrecision, MidpointRounding.AwayFromZero), cur);
    }

    public static Money Try(decimal amount) => Of(amount, Currencies.TRY);

    public bool IsZero => Amount == 0m;

    public bool IsNegative => Amount < 0m;

    /// <summary>Kuruşa yuvarlanmış (2 hane, HALF_UP) tutar.</summary>
    public decimal Rounded => decimal.Round(Amount, DisplayPrecision, MidpointRounding.AwayFromZero);

    public Money Round() => new(Rounded, Currency);

    public Money Add(Money other) => new(Amount + Same(other).Amount, Currency);

    public Money Subtract(Money other) => new(Amount - Same(other).Amount, Currency);

    public Money Multiply(decimal factor) => new(decimal.Round(Amount * factor, StoragePrecision, MidpointRounding.AwayFromZero), Currency);

    public Money Divide(decimal divisor)
    {
        Guard.Against(divisor == 0m, KernelMessages.DivisionByZero);
        return new Money(decimal.Round(Amount / divisor, StoragePrecision, MidpointRounding.AwayFromZero), Currency);
    }

    public Money Negate() => new(-Amount, Currency);

    public Money Max(Money other) => Amount >= Same(other).Amount ? this : other;

    public Money Min(Money other) => Amount <= Same(other).Amount ? this : other;

    public static Money operator +(Money a, Money b) => a.Add(b);

    public static Money operator -(Money a, Money b) => a.Subtract(b);

    public static Money operator *(Money a, decimal f) => a.Multiply(f);

    public static Money operator /(Money a, decimal d) => a.Divide(d);

    public static Money operator -(Money a) => a.Negate();

    public static bool operator >(Money a, Money b) => a.CompareTo(b) > 0;

    public static bool operator <(Money a, Money b) => a.CompareTo(b) < 0;

    public static bool operator >=(Money a, Money b) => a.CompareTo(b) >= 0;

    public static bool operator <=(Money a, Money b) => a.CompareTo(b) <= 0;

    public static bool operator ==(Money? a, Money? b) => a?.Equals(b) ?? b is null;

    public static bool operator !=(Money? a, Money? b) => !(a == b);

    public int CompareTo(Money? other) => other is null ? 1 : Amount.CompareTo(Same(other).Amount);

    public override bool Equals(object? obj) => base.Equals(obj);

    public override int GetHashCode() => base.GetHashCode();

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, DisplayFormat, Rounded, Currency);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    private Money Same(Money other)
    {
        Guard.NotNull(other);
        Guard.Against(!string.Equals(other.Currency, Currency, StringComparison.Ordinal),
            string.Format(CultureInfo.InvariantCulture, KernelMessages.CurrencyMismatch, Currency, other.Currency));
        return other;
    }
}
