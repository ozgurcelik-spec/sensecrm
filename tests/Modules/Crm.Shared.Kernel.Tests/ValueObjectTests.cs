using Crm.Shared.Kernel.Results;
using Crm.Shared.Kernel.ValueObjects;
using Shouldly;
using Xunit;

namespace Crm.Shared.Kernel.Tests;

public sealed class MoneyTests
{
    [Fact]
    public void Of_RoundsToStoragePrecision_AndRoundedToCents()
    {
        var m = Money.Try(10.123456m);
        m.Amount.ShouldBe(10.1235m);
        m.Rounded.ShouldBe(10.12m);
    }

    [Fact]
    public void Arithmetic_KeepsCurrency()
    {
        var a = Money.Try(100m);
        var b = Money.Try(25.5m);
        (a + b).Amount.ShouldBe(125.5m);
        (a - b).Amount.ShouldBe(74.5m);
        (a * 1.5m).Amount.ShouldBe(150m);
        (a / 3m).Amount.ShouldBe(33.3333m);
        (a > b).ShouldBeTrue();
        (a == Money.Try(100m)).ShouldBeTrue();
    }

    [Fact]
    public void Add_DifferentCurrency_Throws()
    {
        var a = Money.Try(1m);
        var b = Money.Of(1m, Currencies.USD);
        Should.Throw<ArgumentException>(() => a + b);
    }

    [Fact]
    public void HalfUp_Rounding()
    {
        Money.Try(0.125m).Rounded.ShouldBe(0.13m);
        Money.Try(2.345m).Rounded.ShouldBe(2.35m);
    }
}

public sealed class CurrencyTests
{
    [Theory]
    [InlineData("TRY", true)]
    [InlineData("EUR", true)]
    [InlineData("try", false)]
    [InlineData("TL", false)]
    [InlineData("XYZ", false)]
    [InlineData(null, false)]
    public void Currencies_IsKnown_MatchesActiveIso4217Codes(string? code, bool expected) => Currencies.IsKnown(code).ShouldBe(expected);
}

public sealed class DateRangeTests
{
    [Fact]
    public void CalendarDays_IncludesBothEnds()
    {
        var r = DateRange.Of(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 10));
        r.CalendarDays.ShouldBe(10);
        r.Contains(new DateOnly(2026, 1, 10)).ShouldBeTrue();
        r.Days().Count().ShouldBe(10);
    }

    [Fact]
    public void Overlaps_And_Intersect()
    {
        var a = DateRange.Of(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 10));
        var b = DateRange.Of(new DateOnly(2026, 1, 8), new DateOnly(2026, 1, 20));
        var c = DateRange.Of(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 2));
        a.Overlaps(b).ShouldBeTrue();
        a.Overlaps(c).ShouldBeFalse();
        a.Intersect(b)!.CalendarDays.ShouldBe(3);
        a.Intersect(c).ShouldBeNull();
    }

    [Fact]
    public void EndBeforeStart_Throws() =>
        Should.Throw<ArgumentException>(() => DateRange.Of(new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 1)));
}

public sealed class ResultTests
{
    [Fact]
    public void Failure_CarriesErrorCode_AndValueThrows()
    {
        Result<int> r = Error.NotFound("x.not_found");
        r.IsFailure.ShouldBeTrue();
        r.Error.Code.ShouldBe("x.not_found");
        r.Error.Type.ShouldBe(ErrorType.NotFound);
        Should.Throw<InvalidOperationException>(() => r.Value);
    }

    [Fact]
    public void Map_And_Bind_Compose()
    {
        Result<int> ok = 2;
        ok.Map(x => x * 2).Value.ShouldBe(4);
        ok.Bind(x => Result.Success(x.ToString(System.Globalization.CultureInfo.InvariantCulture))).Value.ShouldBe("2");
        ok.Match(v => v, _ => -1).ShouldBe(2);
    }

    [Fact]
    public void Error_Args_AreKeyed()
    {
        var e = Error.Rule("role.in_use", ("count", 3));
        e.Args!["count"].ShouldBe(3);
    }
}
