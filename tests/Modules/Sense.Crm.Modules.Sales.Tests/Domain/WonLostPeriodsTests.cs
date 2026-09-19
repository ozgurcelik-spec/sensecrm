using Sense.Crm.Modules.Sales.Application.Reports;
using Sense.Crm.Modules.Sales.Domain.Pipelines;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Sales.Tests.Domain;

/// <summary>Kazanılan/kaybedilen raporunun dönem bölme ve sıfır doldurma hesabı (saf, veritabanısız).</summary>
public sealed class WonLostPeriodsTests
{
    private static readonly DateOnly D = new(2026, 1, 1);

    [Fact]
    public void Month_FillsEveryMonthOfTheRange_AcrossYearBoundary()
    {
        var rows = new[]
        {
            new ClosedDealDayRow(new DateOnly(2025, 12, 31), StageKind.Won, 1, 10m),
            new ClosedDealDayRow(new DateOnly(2026, 2, 3), StageKind.Lost, 2, 20m),
        };

        var periods = WonLostPeriods.Build(rows, new DateOnly(2025, 11, 15), new DateOnly(2026, 2, 10), WonLostGroupBy.Month);

        periods.Select(p => p.Period).ShouldBe(["2025-11", "2025-12", "2026-01", "2026-02"]);
        periods[0].ShouldBe(new WonLostPeriodDto("2025-11", 0, 0m, 0, 0m));
        periods[1].ShouldBe(new WonLostPeriodDto("2025-12", 1, 10m, 0, 0m));
        periods[2].ShouldBe(new WonLostPeriodDto("2026-01", 0, 0m, 0, 0m));
        periods[3].ShouldBe(new WonLostPeriodDto("2026-02", 0, 0m, 2, 20m));
    }

    [Fact]
    public void Week_UsesIsoWeeks_WithTheIsoYearOfTheWeek()
    {
        // 29 Aralık 2025 (pazartesi) ile 4 Ocak 2026 arası ISO 2026-W01; 5 Ocak'tan başlayan hafta W02.
        var rows = new[]
        {
            new ClosedDealDayRow(new DateOnly(2025, 12, 31), StageKind.Won, 1, 5m),
            new ClosedDealDayRow(new DateOnly(2026, 1, 6), StageKind.Won, 1, 7m),
        };

        var periods = WonLostPeriods.Build(rows, new DateOnly(2025, 12, 22), new DateOnly(2026, 1, 11), WonLostGroupBy.Week);

        periods.Select(p => p.Period).ShouldBe(["2025-W52", "2026-W01", "2026-W02"]);
        periods.Select(p => p.WonAmount).ShouldBe([0m, 5m, 7m]);
    }

    [Fact]
    public void Rows_OutsideTheRange_OrOfOpenStages_AreIgnored_AndSameDayRowsAreSummed()
    {
        var rows = new[]
        {
            new ClosedDealDayRow(D, StageKind.Won, 1, 100m),
            new ClosedDealDayRow(D, StageKind.Won, 2, 50m),
            new ClosedDealDayRow(D, StageKind.Open, 9, 900m),
            new ClosedDealDayRow(D.AddDays(-1), StageKind.Won, 5, 500m),
            new ClosedDealDayRow(new DateOnly(2026, 3, 1), StageKind.Lost, 5, 500m),
        };

        var periods = WonLostPeriods.Build(rows, D, new DateOnly(2026, 2, 28), WonLostGroupBy.Month);

        periods.ShouldBe([new WonLostPeriodDto("2026-01", 3, 150m, 0, 0m), new WonLostPeriodDto("2026-02", 0, 0m, 0, 0m)]);
    }
}
