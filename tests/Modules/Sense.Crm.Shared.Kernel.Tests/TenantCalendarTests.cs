using Sense.Crm.Shared.Kernel.Time;
using Shouldly;
using Xunit;

namespace Sense.Crm.Shared.Kernel.Tests;

/// <summary>Kiracı saat dilimine göre "bugün", hafta başı ve rapor aralığı sınırları (Milestone 3).</summary>
public sealed class TenantCalendarTests
{
    private static readonly TenantCalendar Istanbul = TenantCalendar.For("Europe/Istanbul");
    private static readonly TenantCalendar NewYork = TenantCalendar.For("America/New_York");

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Today_UsesTheTenantTimeZone_NotUtc()
    {
        // 21:30 UTC = 00:30 ertesi gün İstanbul (UTC+3).
        var now = new DateTimeOffset(Utc(2026, 9, 19, 21, 30));

        Istanbul.Today(now).ShouldBe(new DateOnly(2026, 9, 20));
        TenantCalendar.Utc.Today(now).ShouldBe(new DateOnly(2026, 9, 19));
        NewYork.Today(now).ShouldBe(new DateOnly(2026, 9, 19));
    }

    [Fact]
    public void StartOfDayUtc_ConvertsLocalMidnightToUtc()
    {
        Istanbul.StartOfDayUtc(new DateOnly(2026, 9, 20)).ShouldBe(Utc(2026, 9, 19, 21));
        Istanbul.StartOfDayUtc(new DateOnly(2026, 9, 20)).Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void StartOfDayUtc_FollowsDaylightSaving()
    {
        // ABD yaz saati 2026-03-08'de başlar: gece yarısı hâlâ EST (-5), ertesi gün EDT (-4) → o gün 23 saat sürer.
        NewYork.StartOfDayUtc(new DateOnly(2026, 3, 8)).ShouldBe(Utc(2026, 3, 8, 5));
        NewYork.StartOfDayUtc(new DateOnly(2026, 3, 9)).ShouldBe(Utc(2026, 3, 9, 4));
    }

    [Theory]
    [InlineData(2026, 9, 14, 2026, 9, 14)] // pazartesi kendisi
    [InlineData(2026, 9, 19, 2026, 9, 14)] // cumartesi
    [InlineData(2026, 9, 20, 2026, 9, 14)] // pazar önceki pazartesiye gider
    [InlineData(2026, 9, 21, 2026, 9, 21)] // sonraki pazartesi
    public void StartOfWeek_IsMonday(int y, int m, int d, int ey, int em, int ed) =>
        TenantCalendar.StartOfWeek(new DateOnly(y, m, d)).ShouldBe(new DateOnly(ey, em, ed));

    [Fact]
    public void UtcBounds_AreInclusiveOfBothLocalDays_AsHalfOpenUtcRange()
    {
        var (from, toExclusive) = Istanbul.UtcBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        from.ShouldBe(Utc(2026, 8, 31, 21));
        toExclusive.ShouldBe(Utc(2026, 9, 30, 21)); // 1 Ekim 00:00 yerel
    }

    [Fact]
    public void UnknownOrEmptyTimeZone_FallsBackToUtc()
    {
        TenantCalendar.For("Nowhere/Land").IanaId.ShouldBe(TenantCalendar.UtcId);
        TenantCalendar.For(null).IanaId.ShouldBe(TenantCalendar.UtcId);
        TenantCalendar.For(" ").IanaId.ShouldBe(TenantCalendar.UtcId);
        Istanbul.IanaId.ShouldBe("Europe/Istanbul");
    }

    [Fact]
    public void ResolveReportRange_DefaultsToTheLastTwelveMonths_AndKeepsGivenEnds()
    {
        var today = new DateOnly(2026, 9, 19);

        TenantCalendar.ResolveReportRange(null, null, today).ShouldBe((new DateOnly(2025, 10, 1), today));
        TenantCalendar.ResolveReportRange(new DateOnly(2026, 1, 15), null, today).ShouldBe((new DateOnly(2026, 1, 15), today));
        TenantCalendar.ResolveReportRange(null, new DateOnly(2026, 3, 31), today).ShouldBe((new DateOnly(2025, 4, 1), new DateOnly(2026, 3, 31)));
    }

    [Theory]
    [InlineData(2026, 9, 17, "2026-W38")]
    [InlineData(2026, 1, 1, "2026-W01")]
    [InlineData(2025, 12, 29, "2026-W01")] // ISO yılı takvim yılından farklı olabilir
    [InlineData(2027, 1, 1, "2026-W53")]
    public void IsoWeekLabel_UsesIso8601Weeks(int y, int m, int d, string expected) =>
        TenantCalendar.IsoWeekLabel(new DateOnly(y, m, d)).ShouldBe(expected);

    [Fact]
    public void MonthLabel_IsYearDashMonth() => TenantCalendar.MonthLabel(new DateOnly(2026, 9, 5)).ShouldBe("2026-09");
}
