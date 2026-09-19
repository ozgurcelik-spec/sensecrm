using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Commerce.Tests.Domain;

/// <summary>
/// <c>DocumentTotals</c> (saf domain, sunucu belirleyici): planın referans vektörleri (web <c>computeTotals</c> testi aynı tabloyu kullanır),
/// yarım yukarı yuvarlama, kalem-bazlı yuvarlama ve değişmezler.
/// </summary>
public sealed class DocumentTotalsTests
{
    public static TheoryData<decimal, decimal, decimal, decimal, decimal, decimal, decimal, decimal> ReferenceVectors => new()
    {
        // qty, unitPrice, disc %, tax %, lineSubtotal, lineDiscount, lineTax, lineTotal
        { 3m, 19.99m, 10m, 20m, 59.97m, 6.00m, 10.79m, 64.76m },
        { 1m, 0.05m, 50m, 20m, 0.05m, 0.03m, 0.00m, 0.02m },
        { 2.5m, 10.10m, 0m, 18m, 25.25m, 0.00m, 4.55m, 29.80m },
        { 1m, 100.00m, 100m, 20m, 100.00m, 100.00m, 0.00m, 0.00m },
    };

    [Theory]
    [MemberData(nameof(ReferenceVectors))]
    public void CalculateLine_MatchesPlanReferenceVectors(
        decimal quantity, decimal unitPrice, decimal discount, decimal tax, decimal subtotal, decimal lineDiscount, decimal lineTax, decimal total)
    {
        var line = DocumentTotals.CalculateLine(quantity, unitPrice, discount, tax);

        line.LineSubtotal.ShouldBe(subtotal);
        line.LineDiscount.ShouldBe(lineDiscount);
        line.LineTax.ShouldBe(lineTax);
        line.LineTotal.ShouldBe(total);
    }

    [Fact]
    public void Document_OfVectors1And3_SumsRoundedLineValues()
    {
        var lines = new[]
        {
            DocumentTotals.CalculateLine(3m, 19.99m, 10m, 20m),
            DocumentTotals.CalculateLine(2.5m, 10.10m, 0m, 18m),
        };

        var totals = DocumentTotals.Sum(lines);

        totals.Subtotal.ShouldBe(85.22m);
        totals.DiscountTotal.ShouldBe(6.00m);
        totals.TaxTotal.ShouldBe(15.34m);
        totals.GrandTotal.ShouldBe(94.56m);
        totals.GrandTotal.ShouldBe(lines.Sum(l => l.LineTotal));
    }

    [Theory]
    [InlineData("0.025", "0.03")]
    [InlineData("4.545", "4.55")]
    [InlineData("0.035", "0.04")]
    [InlineData("2.675", "2.68")]
    [InlineData("0.004", "0.00")]
    public void Round_IsHalfUp_AwayFromZero(string value, string expected) =>
        DocumentTotals.Round(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void FullDiscount_ZeroesNetAndTax()
    {
        var line = DocumentTotals.CalculateLine(7m, 12.34m, 100m, 20m);

        line.LineTotal.ShouldBe(0m);
        line.LineTax.ShouldBe(0m);
        line.LineDiscount.ShouldBe(line.LineSubtotal);
    }

    [Fact]
    public void EmptyDocument_IsAllZero() => DocumentTotals.Sum([]).ShouldBe(DocumentAmounts.Zero);

    [Fact]
    public void PerLineRounding_DiffersFromRoundingTheWhole()
    {
        // Üç kalem: 0.025 iskonto her satırda 0.03'e yuvarlanır (toplam 0.09), toplu yuvarlama 0.075 → 0.08 olurdu.
        var line = DocumentTotals.CalculateLine(1m, 0.05m, 50m, 0m);
        var perLine = DocumentTotals.Sum([line, line, line]);
        var wholeAtOnce = DocumentTotals.Round(3m * 0.05m * 50m / 100m);

        perLine.DiscountTotal.ShouldBe(0.09m);
        wholeAtOnce.ShouldBe(0.08m);
        perLine.DiscountTotal.ShouldNotBe(wholeAtOnce);
    }

    [Fact]
    public void GrandTotal_EqualsSumOfLineTotals_ForPseudoRandomLines()
    {
        var random = new Random(20260919);
        for (var round = 0; round < 200; round++)
        {
            var lines = Enumerable.Range(0, random.Next(0, 12))
                .Select(_ => DocumentTotals.CalculateLine(
                    Math.Round((decimal)random.NextDouble() * 50m, CommerceLimits.QuantityScale),
                    Math.Round((decimal)random.NextDouble() * 5000m, CommerceLimits.UnitPriceScale),
                    Math.Round((decimal)random.NextDouble() * 100m, CommerceLimits.PercentScale),
                    Math.Round((decimal)random.NextDouble() * 100m, CommerceLimits.PercentScale)))
                .ToList();

            var totals = DocumentTotals.Sum(lines);

            totals.GrandTotal.ShouldBe(lines.Sum(l => l.LineTotal));
            totals.GrandTotal.ShouldBe(totals.Subtotal - totals.DiscountTotal + totals.TaxTotal);
            lines.ShouldAllBe(l => l.LineTotal == l.LineSubtotal - l.LineDiscount + l.LineTax);
            lines.ShouldAllBe(l => DocumentTotals.Round(l.LineTotal) == l.LineTotal);
        }
    }

    [Fact]
    public void MaximumLimits_DoNotOverflowAStoredLine()
    {
        var line = DocumentTotals.CalculateLine(CommerceLimits.MaxQuantity, CommerceLimits.MaxUnitPrice, 0m, CommerceLimits.MaxPercent);

        line.LineSubtotal.ShouldBe(1_000_000_000_000_000m);
        line.LineTotal.ShouldBe(2_000_000_000_000_000m);
        line.LineTotal.ShouldBeLessThan(CommerceLimits.MaxDocumentTotal);
    }

    [Fact]
    public void EnsureTotalsFit_RejectsDocumentsBeyondTheStorableLimit()
    {
        var huge = new LineInput(null, "x", CommerceLimits.MaxQuantity, CommerceLimits.MaxUnitPrice, 0m, 100m);
        var lines = Enumerable.Repeat(huge, CommerceLimits.MaxLines).ToList();

        SalesDocument.EnsureTotalsFit(lines).Error.Code.ShouldBe(CommerceErrors.TotalTooLarge);
        SalesDocument.EnsureTotalsFit([huge]).IsSuccess.ShouldBeTrue();
    }
}
