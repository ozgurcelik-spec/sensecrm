using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Shared.Kernel.Results;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Commerce.Tests.Domain;

/// <summary>
/// M9C belge yuvarlaması (adjustment): plan referans vektörleri A1–A8 (web <c>computeTotals</c> testi aynı tabloyu kullanır), değişmez
/// <c>grandTotal == Σ lineTotal + adjustment</c>, KDV/iskonto değişmezliği, <c>total_too_large</c> sınırı, adres normalizasyonu ve numara biçimi.
/// </summary>
public sealed class AdjustmentDomainTests
{
    // Belge {1,3} (M6A): 3 × 19.99 (%10 iskonto, %20 KDV) + 2.5 × 10.10 (%18 KDV) = Σ lineTotal 94.56.
    private static readonly LineInput[] DocumentOneAndThree =
    [
        new(null, "Lisans", 3m, 19.99m, 10m, 20m),
        new(null, "Danışmanlık", 2.5m, 10.10m, 0m, 18m),
    ];

    private static Result<Quote> QuoteWith(decimal adjustment, params LineInput[] lines) =>
        Quote.Create(Fixtures.Tenant, "Q-2026-0001", Fixtures.Header() with { Adjustment = adjustment }, null, lines);

    [Theory]
    [InlineData("-0.56", "94.00")]
    [InlineData("0.44", "95.00")]
    [InlineData("-94.56", "0.00")]
    [InlineData("0", "94.56")]
    public void A1toA3_AdjustmentIsAddedAfterTax(string adjustment, string expectedGrandTotal)
    {
        var quote = QuoteWith(Parse(adjustment), DocumentOneAndThree).Value;

        quote.GrandTotal.ShouldBe(Parse(expectedGrandTotal));
        quote.Adjustment.ShouldBe(Parse(adjustment));
        quote.Subtotal.ShouldBe(85.22m, "yuvarlama alt toplamı değiştirmez");
        quote.DiscountTotal.ShouldBe(6.00m);
        quote.TaxTotal.ShouldBe(15.34m);
        quote.GrandTotal.ShouldBe(quote.Lines.Sum(l => l.LineTotal) + quote.Adjustment);
        quote.GrandTotal.ShouldBe(quote.Subtotal - quote.DiscountTotal + quote.TaxTotal + quote.Adjustment);
    }

    [Fact]
    public void A4_NegativeTotalIsRejected() =>
        QuoteWith(-94.57m, DocumentOneAndThree).ShouldFail(CommerceErrors.AdjustmentNegativeTotal, ErrorType.Validation);

    [Fact]
    public void A5_ThreeDecimalsAreRejectedNotRounded() =>
        Should.Throw<ArgumentException>(() => QuoteWith(0.005m, DocumentOneAndThree));

    [Fact]
    public void A6_SingleLine_KeepsTaxAndDiscountUntouched()
    {
        var quote = QuoteWith(-0.76m, DocumentOneAndThree[0]).Value;

        quote.GrandTotal.ShouldBe(64.00m);
        quote.TaxTotal.ShouldBe(10.79m, "KDV yeniden hesaplanmaz");
        quote.DiscountTotal.ShouldBe(6.00m, "iskonto yeniden hesaplanmaz");
    }

    [Fact]
    public void A7_EmptyDocument_CannotCarryAnAdjustment_ButZeroIsFine()
    {
        QuoteWith(0.01m).ShouldFail(CommerceErrors.AdjustmentRequiresLines, ErrorType.Validation);

        var empty = QuoteWith(0m).Value;
        (empty.Subtotal, empty.DiscountTotal, empty.TaxTotal, empty.Adjustment, empty.GrandTotal).ShouldBe((0m, 0m, 0m, 0m, 0m));
    }

    [Fact]
    public void A8_AboveTheAbsoluteLimitIsRejected()
    {
        Should.Throw<ArgumentException>(() => QuoteWith(1_000_000_000.01m, DocumentOneAndThree));
        Should.Throw<ArgumentException>(() => QuoteWith(-1_000_000_000.01m, DocumentOneAndThree));
        SalesDocument.EnsureTotalsFit([new LineInput(null, "x", 1m, 2_000_000_000m, 0m, 0m)], 1_000_000_000m).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void GrandTotalInvariant_HoldsForRandomLinesAndAdjustments()
    {
        var random = new Random(20260920);
        for (var round = 0; round < 300; round++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 8))
                .Select(_ => new LineInput(
                    null,
                    "x",
                    Math.Round(((decimal)random.NextDouble() * 50m) + 0.0001m, CommerceLimits.QuantityScale),
                    Math.Round((decimal)random.NextDouble() * 5000m, CommerceLimits.UnitPriceScale),
                    Math.Round((decimal)random.NextDouble() * 100m, CommerceLimits.PercentScale),
                    Math.Round((decimal)random.NextDouble() * 100m, CommerceLimits.PercentScale)))
                .ToList();
            var sum = DocumentTotals.Sum(lines.Select(l => DocumentTotals.CalculateLine(l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxRate)));
            var adjustment = Math.Round(((decimal)random.NextDouble() * 2m) - 1m, 2) * Math.Min(sum.GrandTotal, 100m);
            adjustment = Math.Round(adjustment, 2);

            var result = QuoteWith(adjustment, lines.ToArray());

            if (sum.GrandTotal + adjustment < 0m)
            {
                result.ShouldFail(CommerceErrors.AdjustmentNegativeTotal, ErrorType.Validation);
                continue;
            }

            var quote = result.Value;
            quote.GrandTotal.ShouldBe(quote.Lines.Sum(l => l.LineTotal) + adjustment);
            quote.Subtotal.ShouldBe(sum.Subtotal);
            quote.TaxTotal.ShouldBe(sum.TaxTotal);
            quote.DiscountTotal.ShouldBe(sum.DiscountTotal);
        }
    }

    [Fact]
    public void TotalTooLarge_IsEvaluatedOnTheGrandTotalIncludingAdjustment()
    {
        // Σ lineTotal = 9.999.999.000.000.000: sınırın (9.999.999.999.999.999,99) altında, boşluk 999.999.999,99.
        var lines = Enumerable.Repeat(new LineInput(null, "x", 1_000_000m, 1_000_000_000m, 0m, 0m), 9)
            .Append(new LineInput(null, "y", 999_999m, 1_000_000_000m, 0m, 0m))
            .ToList();

        SalesDocument.EnsureTotalsFit(lines).IsSuccess.ShouldBeTrue();
        SalesDocument.EnsureTotalsFit(lines, 999_999_999.99m).IsSuccess.ShouldBeTrue("tam sınırda");
        SalesDocument.EnsureTotalsFit(lines, CommerceLimits.MaxAdjustment).Error.Code.ShouldBe(CommerceErrors.TotalTooLarge);
        var huge = new LineInput(null, "x", CommerceLimits.MaxQuantity, CommerceLimits.MaxUnitPrice, 0m, 100m);
        SalesDocument.EnsureTotalsFit(Enumerable.Repeat(huge, CommerceLimits.MaxLines).ToList(), 1_000_000_000m).Error.Code.ShouldBe(CommerceErrors.TotalTooLarge);
    }

    [Fact]
    public void EmptyBlockNormalizesToNull_AndLengthLimitsApply()
    {
        DocumentAddress.Normalize(new DocumentAddress("  ", null, "", " ", null, null)).ShouldBeNull();
        DocumentAddress.Normalize(null).ShouldBeNull();
        var address = DocumentAddress.Normalize(new DocumentAddress(" Atatürk Cd. 12 ", "Kat 3", " İstanbul ", null, "34000", "Türkiye"));
        address.ShouldBe(new DocumentAddress("Atatürk Cd. 12", "Kat 3", "İstanbul", null, "34000", "Türkiye"));

        Should.NotThrow(() => DocumentAddress.Normalize(new DocumentAddress(new string('a', CommerceLimits.StreetMaxLength), new string('b', 100), null, null, null, null)));
        Should.Throw<ArgumentException>(() => DocumentAddress.Normalize(new DocumentAddress(new string('a', CommerceLimits.StreetMaxLength + 1), null, null, null, null, null)));
        Should.Throw<ArgumentException>(() => DocumentAddress.Normalize(new DocumentAddress(null, new string('b', 101), null, null, null, null)));
    }

    [Fact]
    public void AddressesAreStoredAsIndependentFlatColumns_AndEmptyBlocksReadBackAsNull()
    {
        var header = Fixtures.Header() with
        {
            Carrier = "  Yurtiçi Kargo ",
            BillingAddress = new DocumentAddress("Fatura sk.", "Daire 4", "Ankara", "Ankara", "06000", "Türkiye"),
            ShippingAddress = new DocumentAddress(null, null, "İzmir", null, null, null),
        };

        var quote = Quote.Create(Fixtures.Tenant, "Q-2026-0001", header, null, [Fixtures.Line()]).Value;

        quote.Carrier.ShouldBe("Yurtiçi Kargo");
        quote.BillingStreet.ShouldBe("Fatura sk.");
        quote.BillingBuilding.ShouldBe("Daire 4");
        quote.ShippingCity.ShouldBe("İzmir");
        quote.ShippingStreet.ShouldBeNull();
        quote.BillingAddress.ShouldBe(new DocumentAddress("Fatura sk.", "Daire 4", "Ankara", "Ankara", "06000", "Türkiye"));
        quote.ShippingAddress.ShouldBe(new DocumentAddress(null, null, "İzmir", null, null, null));

        // Tam değiştirme: gönderilmeyen blok temizlenir.
        quote.Update(Fixtures.Header(), null, [Fixtures.Line()]).IsSuccess.ShouldBeTrue();
        quote.BillingAddress.ShouldBeNull();
        quote.ShippingAddress.ShouldBeNull();
        quote.Carrier.ShouldBeNull();
        quote.Adjustment.ShouldBe(0m);
    }

    [Fact]
    public void NumberFormat_CoversInvoiceAndPurchaseOrder_AndGrowsBeyond9999()
    {
        DocumentNumberFormat.Format(DocumentKinds.Invoice, 2026, 1).ShouldBe("INV-2026-0001");
        DocumentNumberFormat.Format(DocumentKinds.PurchaseOrder, 2026, 1).ShouldBe("PO-2026-0001");
        DocumentNumberFormat.Format(DocumentKinds.Invoice, 2027, 12345).ShouldBe("INV-2027-12345");
        DocumentNumberFormat.Format(DocumentKinds.Quote, 2026, 9999).ShouldBe("Q-2026-9999");
        DocumentKinds.PurchaseOrder.Length.ShouldBeLessThanOrEqualTo(CommerceLimits.EnumColumnMaxLength);
    }

    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Fiyat çözümü (saf): <c>flat</c> ve <c>perProduct</c> vektörleri, para birimi uyuşmazlığı, etkinlik (tarih aralığı).</summary>
public sealed class PriceResolutionTests
{
    [Theory]
    [InlineData("19.99", "-10", "17.9910")]
    [InlineData("10.10", "-33.33", "6.7337")]
    [InlineData("0.0001", "50", "0.0002")]
    [InlineData("100.00", "5.25", "105.2500")]
    [InlineData("100.00", "-99.99", "0.0100")]
    [InlineData("100.00", "1000", "1100.0000")]
    public void Flat_AppliesTheSignedPercentAndRoundsToFourDecimalsHalfUp(string catalog, string percent, string expected)
    {
        var resolved = PriceResolution.Resolve(PricingModel.Flat, Parse(percent), "TRY", "TRY", Parse(catalog), entryPrice: 5m);

        resolved.UnitPrice.ShouldBe(Parse(expected));
        resolved.Source.ShouldBe(PriceSource.Flat);
    }

    [Fact]
    public void PerProduct_UsesTheEntry_ElseTheCatalog()
    {
        var withEntry = PriceResolution.Resolve(PricingModel.PerProduct, null, "TRY", "TRY", 19.99m, 12.50m);
        var withoutEntry = PriceResolution.Resolve(PricingModel.PerProduct, null, "TRY", "TRY", 19.99m, null);

        (withEntry.UnitPrice, withEntry.Source).ShouldBe((12.5000m, PriceSource.Entry));
        (withoutEntry.UnitPrice, withoutEntry.Source).ShouldBe((19.9900m, PriceSource.Catalog));
    }

    [Fact]
    public void CurrencyMismatch_FallsBackToTheCatalogPrice()
    {
        var flat = PriceResolution.Resolve(PricingModel.Flat, -10m, "USD", "TRY", 100m, null);
        var entry = PriceResolution.Resolve(PricingModel.PerProduct, null, "USD", "TRY", 100m, 50m);

        (flat.UnitPrice, flat.Source).ShouldBe((100m, PriceSource.Catalog));
        (entry.UnitPrice, entry.Source).ShouldBe((100m, PriceSource.Catalog));
    }

    [Fact]
    public void Effectiveness_UsesActiveFlagAndInclusiveDateBounds()
    {
        var today = new DateOnly(2026, 9, 20);
        PriceBook Make(bool active, DateOnly? from, DateOnly? to) =>
            PriceBook.Create(Fixtures.Tenant, "Liste", Fixtures.User, active, PricingModel.PerProduct, null, "TRY", from, to, null);

        Make(true, null, null).IsEffective(today).ShouldBeTrue();
        Make(false, null, null).IsEffective(today).ShouldBeFalse();
        Make(true, today, today).IsEffective(today).ShouldBeTrue("uçlar dahil");
        Make(true, today.AddDays(1), null).IsEffective(today).ShouldBeFalse("henüz başlamadı");
        Make(true, null, today.AddDays(-1)).IsEffective(today).ShouldBeFalse("süresi bitti");
        Make(true, null, today).IsEffective(today).ShouldBeTrue();
        Make(true, null, null).IsEffective(today).ShouldBeTrue();

        var compiled = PriceBook.EffectiveExpression(today).Compile();
        foreach (var book in new[] { Make(true, null, null), Make(false, null, null), Make(true, today.AddDays(1), null), Make(true, null, today.AddDays(-1)) })
        {
            compiled(book).ShouldBe(book.IsEffective(today), "ifade ve bellek tanımı eşdeğer");
        }
    }

    [Fact]
    public void ModelAndCurrencyAreImmutable_AndTheNameIsNormalizedForUniqueness()
    {
        var book = PriceBook.Create(Fixtures.Tenant, "  Bayi Listesi ", Fixtures.User, true, PricingModel.Flat, -10m, "TRY", null, null, null);

        book.NameNormalized.ShouldBe("BAYI LISTESI");
        book.Update("Bayi 2", Fixtures.User, true, PricingModel.PerProduct, -10m, null, null, null, null).ShouldFail(CommerceErrors.PriceBookModelImmutable, ErrorType.Conflict);
        var currencyChange = book.Update("Bayi 2", Fixtures.User, true, null, -10m, "USD", null, null, null);
        currencyChange.ShouldFail(CommerceErrors.PriceBookModelImmutable, ErrorType.Conflict);
        currencyChange.Error.Args!["property"].ShouldBe("currency");
        book.Update("Bayi 2", Fixtures.User, true, PricingModel.Flat, 5m, "try", null, null, null).IsSuccess.ShouldBeTrue();
        book.Name.ShouldBe("Bayi 2");
        book.AdjustmentPercent.ShouldBe(5m);
    }

    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
