using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Kernel.Time;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Commerce.Tests.Domain;

internal static class Fixtures
{
    public static readonly Guid Tenant = Guid.NewGuid();
    public static readonly Guid User = Guid.NewGuid();
    public static readonly Guid Account = Guid.NewGuid();
    public static readonly DateOnly Today = new(2026, 9, 19);
    public static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public static DocumentHeader Header(string? currency = null) => new("  Yıllık lisans  ", Account, null, null, User, currency, " şart ", null);

    public static LineInput Line(decimal quantity = 2m, decimal unitPrice = 100m, decimal discount = 0m, decimal tax = 20m) =>
        new(null, "CRM Pro lisansı", quantity, unitPrice, discount, tax);

    public static Quote NewQuote(DateOnly? validUntil = null, params LineInput[] lines) =>
        Quote.Create(Tenant, "Q-2026-0001", Header(), validUntil, lines.Length == 0 ? [Line()] : lines).Value;

    public static Quote SentQuote(DateOnly? validUntil = null)
    {
        var quote = NewQuote(validUntil);
        quote.Send(Today, Now).IsSuccess.ShouldBeTrue();
        return quote;
    }

    public static SalesOrder NewOrder(params LineInput[] lines) =>
        SalesOrder.Create(Tenant, "SO-2026-0001", Header(), Today, null, lines.Length == 0 ? [Line()] : lines).Value;

    public static void ShouldFail(this Result result, string code, ErrorType type)
    {
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(code);
        result.Error.Type.ShouldBe(type);
    }
}

public sealed class ProductDomainTests
{
    [Fact]
    public void Create_CleansTextAndNormalizesCode()
    {
        var product = Product.Create(Fixtures.Tenant, "  Lisans ", "  crm-pro ", "  açıklama ", 100m, " usd ", 20m, " adet ", true);

        product.Name.ShouldBe("Lisans");
        product.Code.ShouldBe("crm-pro");
        product.CodeNormalized.ShouldBe("CRM-PRO");
        product.Description.ShouldBe("açıklama");
        product.Currency.ShouldBe("USD");
        product.Unit.ShouldBe("adet");
        product.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void Create_DefaultsCurrencyToTry_AndBlankCodeToNull()
    {
        var product = Product.Create(Fixtures.Tenant, "Hizmet", "   ", null, 0m, null, 0m, null, true);

        product.Currency.ShouldBe("TRY");
        product.Code.ShouldBeNull();
        product.CodeNormalized.ShouldBeNull();
        product.UnitPrice.ShouldBe(0m);
    }

    [Theory]
    [InlineData("abc", "ABC")]
    [InlineData("  Sku-1 ", "SKU-1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeCode_IsTrimmedAndUpperInvariant(string? code, string? expected) => Product.NormalizeCode(code).ShouldBe(expected);

    [Fact]
    public void Update_ReplacesAllFields_AndClearsOptionalOnes()
    {
        var product = Product.Create(Fixtures.Tenant, "A", "x", "d", 5m, "EUR", 8m, "adet", true);

        product.Update("B", null, null, 7.5m, null, 0m, null, false);

        product.Name.ShouldBe("B");
        product.Code.ShouldBeNull();
        product.CodeNormalized.ShouldBeNull();
        product.Description.ShouldBeNull();
        product.Unit.ShouldBeNull();
        product.Currency.ShouldBe("TRY");
        product.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Range_Violations_AreProgrammingErrors()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Product.Create(Fixtures.Tenant, "A", null, null, -1m, null, 0m, null, true));
        Should.Throw<ArgumentOutOfRangeException>(() => Product.Create(Fixtures.Tenant, "A", null, null, 0m, null, 100.01m, null, true));
        Should.Throw<ArgumentException>(() => Product.Create(Fixtures.Tenant, " ", null, null, 0m, null, 0m, null, true));
    }
}

public sealed class QuoteStateMachineTests
{
    [Fact]
    public void NewQuote_IsDraft_WithServerComputedTotals()
    {
        var quote = Fixtures.NewQuote(null, Fixtures.Line(2m, 100m, 0m, 20m));

        quote.Status.ShouldBe(QuoteStatus.Draft);
        quote.Subject.ShouldBe("Yıllık lisans");
        quote.Terms.ShouldBe("şart");
        quote.Currency.ShouldBe("TRY");
        quote.Subtotal.ShouldBe(200m);
        quote.TaxTotal.ShouldBe(40m);
        quote.GrandTotal.ShouldBe(240m);
        quote.Lines.Single().Position.ShouldBe(0);
        quote.Lines.Single().TenantId.ShouldBe(Fixtures.Tenant);
    }

    [Fact]
    public void Send_RequiresAtLeastOneLine_As422()
    {
        var quote = Quote.Create(Fixtures.Tenant, "Q-2026-0002", Fixtures.Header(), null, []).Value;

        quote.Send(Fixtures.Today, Fixtures.Now).ShouldFail(CommerceErrors.QuoteNoLines, ErrorType.Rule);
        quote.Status.ShouldBe(QuoteStatus.Draft);
        quote.GrandTotal.ShouldBe(0m);
    }

    [Fact]
    public void Send_RejectsAPastValidUntil_ButAllowsTodayAndBlank()
    {
        Fixtures.NewQuote(Fixtures.Today.AddDays(-1)).Send(Fixtures.Today, Fixtures.Now).ShouldFail(CommerceErrors.ValidUntilPast, ErrorType.Validation);
        Fixtures.NewQuote(Fixtures.Today).Send(Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
        Fixtures.NewQuote(null).Send(Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Send_StampsSentAt()
    {
        var quote = Fixtures.SentQuote();

        quote.Status.ShouldBe(QuoteStatus.Sent);
        quote.SentAt.ShouldBe(Fixtures.Now);
    }

    [Fact]
    public void Accept_FromSent_Accepts_AndIsTerminal()
    {
        var quote = Fixtures.SentQuote();

        quote.Accept(Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();

        quote.Status.ShouldBe(QuoteStatus.Accepted);
        quote.AcceptedAt.ShouldBe(Fixtures.Now);
        quote.Revert(Fixtures.Today).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        quote.Reject("x", Fixtures.Today, Fixtures.Now).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        quote.Extend(Fixtures.Today.AddDays(3), Fixtures.Today).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        quote.Update(Fixtures.Header(), null, [Fixtures.Line()]).ShouldFail(CommerceErrors.QuoteNotEditable, ErrorType.Conflict);
        quote.EnsureEditable().ShouldFail(CommerceErrors.QuoteNotEditable, ErrorType.Conflict);
    }

    [Fact]
    public void SecondAccept_IsAConflict_NotIdempotent()
    {
        var quote = Fixtures.SentQuote();
        quote.Accept(Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();

        var again = quote.Accept(Fixtures.Today, Fixtures.Now);

        again.ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        again.Error.Args!["from"].ShouldBe("accepted");
        again.Error.Args["to"].ShouldBe("accepted");
    }

    [Fact]
    public void Accept_FromDraft_IsAnInvalidTransition_WithFromAndToArgs()
    {
        var result = Fixtures.NewQuote().Accept(Fixtures.Today, Fixtures.Now);

        result.ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        result.Error.Args!["from"].ShouldBe("draft");
        result.Error.Args["to"].ShouldBe("accepted");
    }

    [Fact]
    public void Accept_WhenExpired_IsQuoteExpired_Conflict()
    {
        var quote = Fixtures.SentQuote(Fixtures.Today);

        quote.Accept(Fixtures.Today.AddDays(1), Fixtures.Now).ShouldFail(CommerceErrors.QuoteExpired, ErrorType.Conflict);
        quote.Status.ShouldBe(QuoteStatus.Sent);
    }

    [Fact]
    public void Reject_FromSent_StoresReason_EvenWhenExpired()
    {
        var quote = Fixtures.SentQuote(Fixtures.Today);

        quote.Reject("  pahalı  ", Fixtures.Today.AddDays(5), Fixtures.Now).IsSuccess.ShouldBeTrue();

        quote.Status.ShouldBe(QuoteStatus.Rejected);
        quote.RejectionReason.ShouldBe("pahalı");
        quote.RejectedAt.ShouldBe(Fixtures.Now);
    }

    [Fact]
    public void Reject_FromDraftOrRejected_IsInvalid()
    {
        Fixtures.NewQuote().Reject(null, Fixtures.Today, Fixtures.Now).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);

        var rejected = Fixtures.SentQuote();
        rejected.Reject(null, Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
        rejected.RejectionReason.ShouldBeNull();
        rejected.Reject(null, Fixtures.Today, Fixtures.Now).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
    }

    [Fact]
    public void Revert_FromSentOrRejected_ReturnsToDraft_AndClearsSendAndRejectFields()
    {
        var quote = Fixtures.SentQuote();
        quote.Reject("x", Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();

        quote.Revert(Fixtures.Today).IsSuccess.ShouldBeTrue();

        quote.Status.ShouldBe(QuoteStatus.Draft);
        quote.SentAt.ShouldBeNull();
        quote.RejectedAt.ShouldBeNull();
        quote.RejectionReason.ShouldBeNull();

        var sent = Fixtures.SentQuote();
        sent.Revert(Fixtures.Today).IsSuccess.ShouldBeTrue();
        sent.Status.ShouldBe(QuoteStatus.Draft);
    }

    [Fact]
    public void Revert_FromDraft_IsInvalid() =>
        Fixtures.NewQuote().Revert(Fixtures.Today).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);

    [Fact]
    public void Revert_ExpiredQuote_Works()
    {
        var quote = Fixtures.SentQuote(Fixtures.Today);

        quote.Revert(Fixtures.Today.AddDays(9)).IsSuccess.ShouldBeTrue();

        quote.Status.ShouldBe(QuoteStatus.Draft);
    }

    [Fact]
    public void Extend_MakesAnExpiredQuoteValidAgain_AndRejectsPastDates()
    {
        var quote = Fixtures.SentQuote(Fixtures.Today);
        var later = Fixtures.Today.AddDays(10);
        quote.EffectiveStatus(later).ShouldBe(QuoteStatus.Expired);

        quote.Extend(later, later).IsSuccess.ShouldBeTrue();

        quote.ValidUntil.ShouldBe(later);
        quote.EffectiveStatus(later).ShouldBe(QuoteStatus.Sent);
        quote.Extend(later.AddDays(-1), later).ShouldFail(CommerceErrors.ValidUntilPast, ErrorType.Validation);
    }

    [Fact]
    public void Extend_FromDraft_IsInvalid() =>
        Fixtures.NewQuote().Extend(Fixtures.Today.AddDays(2), Fixtures.Today).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);

    [Fact]
    public void Update_OnlyDraft_ReplacesLinesAndRecomputesTotals_KeepingNumberAndStatus()
    {
        var quote = Fixtures.NewQuote();
        var originalLineId = quote.Lines.Single().Id;

        var result = quote.Update(Fixtures.Header("USD"), Fixtures.Today.AddDays(30), [Fixtures.Line(1m, 10m, 0m, 0m), Fixtures.Line(3m, 5m, 0m, 0m)]);

        result.IsSuccess.ShouldBeTrue();
        quote.Number.ShouldBe("Q-2026-0001");
        quote.Status.ShouldBe(QuoteStatus.Draft);
        quote.Currency.ShouldBe("USD");
        quote.ValidUntil.ShouldBe(Fixtures.Today.AddDays(30));
        quote.Lines.Select(l => l.Position).ShouldBe([0, 1]);
        quote.Lines.Select(l => l.Id).ShouldNotContain(originalLineId);
        quote.GrandTotal.ShouldBe(25m);

        Fixtures.SentQuote().Update(Fixtures.Header(), null, [Fixtures.Line()]).ShouldFail(CommerceErrors.QuoteNotEditable, ErrorType.Conflict);
    }

    [Fact]
    public void Create_RejectsDocumentsBeyondTheStorableTotal_WithoutMutatingAnything()
    {
        var huge = new LineInput(null, "x", CommerceLimits.MaxQuantity, CommerceLimits.MaxUnitPrice, 0m, 100m);

        var result = Quote.Create(Fixtures.Tenant, "Q-2026-0009", Fixtures.Header(), null, Enumerable.Repeat(huge, CommerceLimits.MaxLines).ToList());

        result.ShouldFail(CommerceErrors.TotalTooLarge, ErrorType.Validation);
    }

    [Fact]
    public void Create_MoreThan100Lines_IsAProgrammingError() =>
        Should.Throw<ArgumentException>(() => Quote.Create(Fixtures.Tenant, "Q-2026-0009", Fixtures.Header(), null, Enumerable.Repeat(Fixtures.Line(), CommerceLimits.MaxLines + 1).ToList()));
}

public sealed class QuoteExpiryTests
{
    [Fact]
    public void Expired_IsDerived_OnlyFromSentWithPastValidUntil()
    {
        var yesterday = Fixtures.Today.AddDays(-1);

        QuoteStatusExpression.Compute(QuoteStatus.Sent, yesterday, Fixtures.Today).ShouldBe(QuoteStatus.Expired);
        QuoteStatusExpression.Compute(QuoteStatus.Sent, Fixtures.Today, Fixtures.Today).ShouldBe(QuoteStatus.Sent);
        QuoteStatusExpression.Compute(QuoteStatus.Sent, null, Fixtures.Today).ShouldBe(QuoteStatus.Sent);
        QuoteStatusExpression.Compute(QuoteStatus.Draft, yesterday, Fixtures.Today).ShouldBe(QuoteStatus.Draft);
        QuoteStatusExpression.Compute(QuoteStatus.Accepted, yesterday, Fixtures.Today).ShouldBe(QuoteStatus.Accepted);
        QuoteStatusExpression.Compute(QuoteStatus.Rejected, yesterday, Fixtures.Today).ShouldBe(QuoteStatus.Rejected);
    }

    [Fact]
    public void ExpressionAndInMemoryRule_AgreeOnEveryCombination()
    {
        DateOnly?[] validUntils = [null, Fixtures.Today.AddDays(-3), Fixtures.Today.AddDays(-1), Fixtures.Today, Fixtures.Today.AddDays(2)];
        foreach (var stored in new[] { QuoteStatus.Draft, QuoteStatus.Sent, QuoteStatus.Accepted, QuoteStatus.Rejected })
        {
            foreach (var validUntil in validUntils)
            {
                var quote = Quote.Create(Fixtures.Tenant, "Q-2026-0001", Fixtures.Header(), validUntil, [Fixtures.Line()]).Value;
                Drive(quote, stored);
                var expected = quote.EffectiveStatus(Fixtures.Today);

                QuoteStatusExpression.IsExpired(Fixtures.Today).Compile()(quote).ShouldBe(expected == QuoteStatus.Expired);
                QuoteStatusExpression.IsNotExpired(Fixtures.Today).Compile()(quote).ShouldBe(expected != QuoteStatus.Expired);
                foreach (var probe in Enum.GetValues<QuoteStatus>())
                {
                    QuoteStatusExpression.HasEffectiveStatus(probe, Fixtures.Today).Compile()(quote).ShouldBe(expected == probe, $"{stored}/{validUntil}/{probe}");
                }
            }
        }
    }

    [Fact]
    public void TenantTimeZoneBoundary_DecidesTheTodayUsedForExpiry()
    {
        // validUntil = 2026-03-31 (Europe/Istanbul UTC+3): 31 Mart 21:30Z hâlâ 1 Nisan yerel → süresi dolmuş; 20:30Z hâlâ 31 Mart → dolmamış.
        var istanbul = TenantCalendar.For("Europe/Istanbul");
        var validUntil = new DateOnly(2026, 3, 31);
        var quote = Fixtures.NewQuote(validUntil);
        quote.Send(new DateOnly(2026, 3, 1), Fixtures.Now).IsSuccess.ShouldBeTrue();

        var beforeMidnight = istanbul.Today(new DateTimeOffset(2026, 3, 31, 20, 59, 0, TimeSpan.Zero));
        var afterMidnight = istanbul.Today(new DateTimeOffset(2026, 3, 31, 21, 1, 0, TimeSpan.Zero));

        beforeMidnight.ShouldBe(validUntil);
        quote.EffectiveStatus(beforeMidnight).ShouldBe(QuoteStatus.Sent);
        afterMidnight.ShouldBe(new DateOnly(2026, 4, 1));
        quote.EffectiveStatus(afterMidnight).ShouldBe(QuoteStatus.Expired);
        TenantCalendar.Utc.Today(new DateTimeOffset(2026, 3, 31, 21, 1, 0, TimeSpan.Zero)).ShouldBe(validUntil);
    }

    private static void Drive(Quote quote, QuoteStatus stored)
    {
        switch (stored)
        {
            case QuoteStatus.Sent:
                // Geçmiş validUntil'li taslağı göndermek için "bugün"ü validUntil'e/öncesine çekeriz.
                quote.Send(quote.ValidUntil ?? Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
                break;

            case QuoteStatus.Accepted:
                quote.Send(quote.ValidUntil ?? Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
                quote.Accept(quote.ValidUntil ?? Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
                break;

            case QuoteStatus.Rejected:
                quote.Send(quote.ValidUntil ?? Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
                quote.Reject(null, Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
                break;

            default:
                break;
        }
    }
}

public sealed class SalesOrderStateMachineTests
{
    [Fact]
    public void NewOrder_IsDraft_WithTotals_AndNoQuote()
    {
        var order = Fixtures.NewOrder();

        order.Status.ShouldBe(SalesOrderStatus.Draft);
        order.OrderDate.ShouldBe(Fixtures.Today);
        order.QuoteId.ShouldBeNull();
        order.GrandTotal.ShouldBe(240m);
    }

    [Fact]
    public void Confirm_RequiresALine_As422()
    {
        var empty = SalesOrder.Create(Fixtures.Tenant, "SO-2026-0002", Fixtures.Header(), Fixtures.Today, null, []).Value;

        empty.Confirm().ShouldFail(CommerceErrors.OrderNoLines, ErrorType.Rule);
    }

    [Fact]
    public void HappyPath_Draft_Confirmed_Fulfilled_ThenTerminal()
    {
        var order = Fixtures.NewOrder();

        order.Confirm().IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(SalesOrderStatus.Confirmed);
        order.Fulfill(Fixtures.Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(SalesOrderStatus.Fulfilled);
        order.FulfilledAt.ShouldBe(Fixtures.Now);

        order.Confirm().ShouldFail(CommerceErrors.OrderInvalidTransition, ErrorType.Conflict);
        order.Fulfill(Fixtures.Now).ShouldFail(CommerceErrors.OrderInvalidTransition, ErrorType.Conflict);
        order.Cancel("x", Fixtures.Now).ShouldFail(CommerceErrors.OrderInvalidTransition, ErrorType.Conflict);
        order.EnsureEditable().ShouldFail(CommerceErrors.OrderNotEditable, ErrorType.Conflict);
    }

    [Fact]
    public void Fulfill_FromDraft_IsInvalid_WithFromToArgs()
    {
        var result = Fixtures.NewOrder().Fulfill(Fixtures.Now);

        result.ShouldFail(CommerceErrors.OrderInvalidTransition, ErrorType.Conflict);
        result.Error.Args!["from"].ShouldBe("draft");
        result.Error.Args["to"].ShouldBe("fulfilled");
    }

    [Fact]
    public void Cancel_FromDraftOrConfirmed_StoresReason_AndIsTerminal()
    {
        var draft = Fixtures.NewOrder();
        draft.Cancel("  vazgeçti ", Fixtures.Now).IsSuccess.ShouldBeTrue();
        draft.Status.ShouldBe(SalesOrderStatus.Cancelled);
        draft.CancelReason.ShouldBe("vazgeçti");
        draft.CancelledAt.ShouldBe(Fixtures.Now);
        draft.Confirm().ShouldFail(CommerceErrors.OrderInvalidTransition, ErrorType.Conflict);
        draft.Cancel(null, Fixtures.Now).ShouldFail(CommerceErrors.OrderInvalidTransition, ErrorType.Conflict);

        var confirmed = Fixtures.NewOrder();
        confirmed.Confirm().IsSuccess.ShouldBeTrue();
        confirmed.Cancel(null, Fixtures.Now).IsSuccess.ShouldBeTrue();
        confirmed.CancelReason.ShouldBeNull();
    }

    [Fact]
    public void Update_OnlyDraft_KeepsQuoteLinkAndNumber()
    {
        var quoteId = Guid.NewGuid();
        var order = SalesOrder.Create(Fixtures.Tenant, "SO-2026-0003", Fixtures.Header(), Fixtures.Today, quoteId, [Fixtures.Line()]).Value;

        order.Update(Fixtures.Header("EUR"), Fixtures.Today.AddDays(2), [Fixtures.Line(1m, 5m, 0m, 0m)]).IsSuccess.ShouldBeTrue();

        order.QuoteId.ShouldBe(quoteId);
        order.Number.ShouldBe("SO-2026-0003");
        order.Currency.ShouldBe("EUR");
        order.OrderDate.ShouldBe(Fixtures.Today.AddDays(2));
        order.GrandTotal.ShouldBe(5m);

        order.Confirm().IsSuccess.ShouldBeTrue();
        order.Update(Fixtures.Header(), Fixtures.Today, [Fixtures.Line()]).ShouldFail(CommerceErrors.OrderNotEditable, ErrorType.Conflict);
    }

    [Fact]
    public void ConvertedLines_ReproduceTheQuoteTotalsExactly()
    {
        var quote = Fixtures.NewQuote(null, Fixtures.Line(3m, 19.99m, 10m, 20m), Fixtures.Line(2.5m, 10.10m, 0m, 18m), Fixtures.Line(1m, 0.05m, 50m, 20m));
        quote.Send(Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();
        quote.Accept(Fixtures.Today, Fixtures.Now).IsSuccess.ShouldBeTrue();

        var order = SalesOrder.Create(
            Fixtures.Tenant,
            "SO-2026-0004",
            Fixtures.Header(),
            Fixtures.Today,
            quote.Id,
            quote.Lines.OrderBy(l => l.Position).Select(l => l.ToInput()).ToList()).Value;

        order.Subtotal.ShouldBe(quote.Subtotal);
        order.DiscountTotal.ShouldBe(quote.DiscountTotal);
        order.TaxTotal.ShouldBe(quote.TaxTotal);
        order.GrandTotal.ShouldBe(quote.GrandTotal);
        order.Lines.Select(l => l.LineTotal).ShouldBe(quote.Lines.Select(l => l.LineTotal));
        order.Lines.Select(l => l.Id).ShouldAllBe(id => quote.Lines.All(q => q.Id != id));
    }
}

public sealed class DocumentNumberTests
{
    [Theory]
    [InlineData(DocumentKinds.Quote, 2026, 1L, "Q-2026-0001")]
    [InlineData(DocumentKinds.Quote, 2027, 20L, "Q-2027-0020")]
    [InlineData(DocumentKinds.Order, 2026, 9999L, "SO-2026-9999")]
    [InlineData(DocumentKinds.Order, 2026, 10000L, "SO-2026-10000")]
    public void Format_PadsToFourDigits_AndGrowsNaturally(string kind, int year, long sequence, string expected) =>
        DocumentNumberFormat.Format(kind, year, sequence).ShouldBe(expected);

    [Fact]
    public void Year_IsComputedInTheTenantTimeZone()
    {
        // Europe/Istanbul: 2026-12-31T21:30Z = 2027-01-01 yerel → numara yılı 2027.
        var instant = new DateTimeOffset(2026, 12, 31, 21, 30, 0, TimeSpan.Zero);

        TenantCalendar.For("Europe/Istanbul").Today(instant).Year.ShouldBe(2027);
        TenantCalendar.Utc.Today(instant).Year.ShouldBe(2026);
    }
}
