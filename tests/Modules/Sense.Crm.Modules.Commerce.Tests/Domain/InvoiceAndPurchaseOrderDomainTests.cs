using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Modules.Commerce.Domain.Vendors;
using Sense.Crm.Shared.Kernel.Results;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Commerce.Tests.Domain;

/// <summary>Fatura: saklanan durum makinesi, sahte "bugün" ile <b>türetilen</b> etkin durum, tahsilat defteri kuralları.</summary>
public sealed class InvoiceDomainTests
{
    private static readonly DateOnly Today = Fixtures.Today;
    private static readonly DateTime Now = Fixtures.Now;

    // Σ lineTotal = 94.56 (belge {1,3}).
    private static readonly LineInput[] Lines = [new(null, "Lisans", 3m, 19.99m, 10m, 20m), new(null, "Danışmanlık", 2.5m, 10.10m, 0m, 18m)];

    private static Invoice New(DateOnly? due = null, DateOnly? invoiceDate = null, params LineInput[] lines) =>
        Invoice.Create(Fixtures.Tenant, "INV-2026-0001", Fixtures.Header(), invoiceDate ?? (due is { } d && d < Today ? d.AddDays(-30) : Today), due, null, lines.Length == 0 ? Lines : lines).Value;

    private static Invoice Sent(DateOnly? due = null)
    {
        var invoice = New(due);
        invoice.Send(Now).IsSuccess.ShouldBeTrue();
        return invoice;
    }

    private static void Pay(Invoice invoice, decimal amount, DateOnly? on = null) =>
        invoice.RecordPayment(amount, on ?? Today, PaymentMethod.BankTransfer, "REF", null, Fixtures.User, Today, Now).IsSuccess.ShouldBeTrue();

    [Fact]
    public void NewInvoice_IsDraft_WithZeroPaidAmount_AndServerTotals()
    {
        var invoice = New();

        invoice.Status.ShouldBe(InvoiceStatus.Draft);
        invoice.PaidAmount.ShouldBe(0m);
        invoice.GrandTotal.ShouldBe(94.56m);
        invoice.BalanceAmount.ShouldBe(94.56m);
        invoice.OrderId.ShouldBeNull();
        invoice.EffectiveStatus(Today).ShouldBe(InvoiceStatus.Draft);
    }

    [Fact]
    public void DueDate_CannotPrecedeTheInvoiceDate()
    {
        Invoice.Create(Fixtures.Tenant, "INV-2026-0001", Fixtures.Header(), Today, Today.AddDays(-1), null, Lines).ShouldFail(CommerceErrors.DueBeforeInvoice, ErrorType.Validation);
        Invoice.Create(Fixtures.Tenant, "INV-2026-0001", Fixtures.Header(), Today, Today, null, Lines).IsSuccess.ShouldBeTrue("vade = fatura günü geçerli");
    }

    [Fact]
    public void Send_RequiresALine_AndOnlyFromDraft()
    {
        var empty = Invoice.Create(Fixtures.Tenant, "INV-2026-0002", Fixtures.Header(), Today, null, null, []).Value;
        empty.Send(Now).ShouldFail(CommerceErrors.InvoiceNoLines, ErrorType.Rule);

        var sent = Sent();
        sent.SentAt.ShouldBe(Now);
        sent.Send(Now).ShouldFail(CommerceErrors.InvoiceInvalidTransition, ErrorType.Conflict);
    }

    [Fact]
    public void EditingIsDraftOnly()
    {
        var sent = Sent();

        sent.Update(Fixtures.Header(), Today, null, Lines).ShouldFail(CommerceErrors.InvoiceNotEditable, ErrorType.Conflict);
        sent.EnsureEditable().ShouldFail(CommerceErrors.InvoiceNotEditable, ErrorType.Conflict);
        New().Update(Fixtures.Header(), Today.AddDays(1), Today.AddDays(30), Lines).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Revert_ClearsSentAt_ButIsBlockedByPayments()
    {
        var invoice = Sent();
        invoice.Revert().IsSuccess.ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Draft);
        invoice.SentAt.ShouldBeNull();

        var paid = Sent();
        Pay(paid, 10m);
        paid.Revert().ShouldFail(CommerceErrors.InvoiceHasPayments, ErrorType.Conflict);
        paid.Cancel("x", Now).ShouldFail(CommerceErrors.InvoiceHasPayments, ErrorType.Conflict);

        // Tahsilat silinince serbest.
        paid.RemovePayment(paid.Payments[0].Id, out _).IsSuccess.ShouldBeTrue();
        paid.Revert().IsSuccess.ShouldBeTrue();
        New().Revert().ShouldFail(CommerceErrors.InvoiceInvalidTransition, ErrorType.Conflict);
    }

    [Fact]
    public void Cancel_IsTerminal_FromDraftOrSent()
    {
        foreach (var invoice in new[] { New(), Sent() })
        {
            invoice.Cancel("  hatalı  ", Now).IsSuccess.ShouldBeTrue();
            invoice.Status.ShouldBe(InvoiceStatus.Cancelled);
            invoice.CancelReason.ShouldBe("hatalı");
            invoice.CancelledAt.ShouldBe(Now);
            invoice.Cancel(null, Now).ShouldFail(CommerceErrors.InvoiceInvalidTransition, ErrorType.Conflict);
            invoice.Send(Now).ShouldFail(CommerceErrors.InvoiceInvalidTransition, ErrorType.Conflict);
            invoice.Revert().ShouldFail(CommerceErrors.InvoiceInvalidTransition, ErrorType.Conflict);
            invoice.EffectiveStatus(Today.AddYears(1)).ShouldBe(InvoiceStatus.Cancelled, "iptal asla türetilmez");
        }
    }

    // ---- Etkin durum ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(InvoiceStatus.Draft, 100, 0, null, InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Draft, 100, 100, -5, InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Cancelled, 100, 0, -5, InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Sent, 100, 0, null, InvoiceStatus.Sent)]
    [InlineData(InvoiceStatus.Sent, 100, 0, 0, InvoiceStatus.Sent)] // vade günü geçmemiş sayılır
    [InlineData(InvoiceStatus.Sent, 100, 0, 1, InvoiceStatus.Sent)]
    [InlineData(InvoiceStatus.Sent, 100, 0, -1, InvoiceStatus.Overdue)]
    [InlineData(InvoiceStatus.Sent, 100, 40, null, InvoiceStatus.PartiallyPaid)]
    [InlineData(InvoiceStatus.Sent, 100, 40, 3, InvoiceStatus.PartiallyPaid)]
    [InlineData(InvoiceStatus.Sent, 100, 40, -1, InvoiceStatus.Overdue)] // vadesi geçmiş kısmi ödeme overdue görünür
    [InlineData(InvoiceStatus.Sent, 100, 100, -30, InvoiceStatus.Paid)] // tam ödeme vadeden önceliklidir
    [InlineData(InvoiceStatus.Sent, 100, 100, null, InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Sent, 0, 0, null, InvoiceStatus.Paid)] // sıfır tutarlı gönderilmiş fatura da paid
    [InlineData(InvoiceStatus.Sent, 0, 0, -5, InvoiceStatus.Paid)]
    public void EffectiveStatus_FollowsPrecedence(InvoiceStatus stored, int grand, int paid, int? dueOffsetDays, InvoiceStatus expected)
    {
        DateOnly? due = dueOffsetDays is { } offset ? Today.AddDays(offset) : null;

        InvoiceStatusExpression.Compute(stored, grand, paid, due, Today).ShouldBe(expected);
    }

    [Fact]
    public void EffectiveStatusExpression_IsDisjointAndEquivalentToTheInMemoryRule()
    {
        var invoices = new List<Invoice>
        {
            New(),
            Sent(),
            Sent(Today),
            Sent(Today.AddDays(-1)),
            Sent(Today.AddDays(5)),
        };
        var partial = Sent(Today.AddDays(5));
        Pay(partial, 10m);
        var partialOverdue = Sent(Today.AddDays(-2));
        Pay(partialOverdue, 10m);
        var paid = Sent(Today.AddDays(-2));
        Pay(paid, 94.56m);
        var cancelled = New();
        cancelled.Cancel(null, Now).IsSuccess.ShouldBeTrue();
        var zeroTotal = Invoice.Create(Fixtures.Tenant, "INV-2026-0009", Fixtures.Header(), Today, null, null, [new LineInput(null, "Ücretsiz", 1m, 0m, 0m, 0m)]).Value;
        zeroTotal.Send(Now).IsSuccess.ShouldBeTrue();
        invoices.AddRange([partial, partialOverdue, paid, cancelled, zeroTotal]);

        foreach (var invoice in invoices)
        {
            var matching = Enum.GetValues<InvoiceStatus>()
                .Where(s => InvoiceStatusExpression.HasEffectiveStatus(s, Today).Compile()(invoice))
                .ToList();

            matching.ShouldBe([invoice.EffectiveStatus(Today)], "her fatura tam bir etkin duruma düşer (ifade = bellek kuralı)");
        }

        // Kiracı "bugün"ü ilerleyince (vade gününün ertesi) türev değişir; zamanlanmış iş yok.
        var due = Sent(Today);
        due.EffectiveStatus(Today).ShouldBe(InvoiceStatus.Sent);
        due.EffectiveStatus(Today.AddDays(1)).ShouldBe(InvoiceStatus.Overdue);
        invoices.Where(InvoiceStatusExpression.IsOpen.Compile().Invoke).ShouldNotContain(cancelled);
    }

    // ---- Tahsilat ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Payments_50Then44_56_SettleTheInvoice_AndThe94_57Overpayment_IsRejected()
    {
        var invoice = Sent();

        var overpay = invoice.RecordPayment(94.57m, Today, null, null, null, Fixtures.User, Today, Now);
        overpay.ShouldFail(CommerceErrors.InvoicePaymentExceedsBalance, ErrorType.Rule);
        overpay.Error.Args!["balance"].ShouldBe(94.56m);
        invoice.PaidAmount.ShouldBe(0m);

        Pay(invoice, 50.00m);
        invoice.PaidAmount.ShouldBe(50.00m);
        invoice.EffectiveStatus(Today).ShouldBe(InvoiceStatus.PartiallyPaid);
        invoice.BalanceAmount.ShouldBe(44.56m);

        invoice.RecordPayment(44.57m, Today, null, null, null, Fixtures.User, Today, Now).ShouldFail(CommerceErrors.InvoicePaymentExceedsBalance, ErrorType.Rule);
        Pay(invoice, 44.56m);
        invoice.PaidAmount.ShouldBe(94.56m);
        invoice.EffectiveStatus(Today).ShouldBe(InvoiceStatus.Paid);
        invoice.PaidAmount.ShouldBe(invoice.Payments.Sum(p => p.Amount), "paid_amount == Σ tahsilat");
        invoice.RecordPayment(0.01m, Today, null, null, null, Fixtures.User, Today, Now).ShouldFail(CommerceErrors.InvoiceNotPayable, ErrorType.Conflict);
    }

    [Fact]
    public void Payments_AreOnlyForSentInvoices()
    {
        New().RecordPayment(1m, Today, null, null, null, Fixtures.User, Today, Now).ShouldFail(CommerceErrors.InvoiceNotPayable, ErrorType.Conflict);

        var cancelled = New();
        cancelled.Cancel(null, Now).IsSuccess.ShouldBeTrue();
        cancelled.RecordPayment(1m, Today, null, null, null, Fixtures.User, Today, Now).ShouldFail(CommerceErrors.InvoiceNotPayable, ErrorType.Conflict);

        var overdue = Sent(Today.AddDays(-3));
        overdue.RecordPayment(1m, Today, null, null, null, Fixtures.User, Today, Now).IsSuccess.ShouldBeTrue("vadesi geçmiş fatura da tahsil edilebilir");
    }

    [Fact]
    public void PaymentDate_MustNotBeInTheFuture_NorBeforeTheInvoiceDate()
    {
        var invoice = Invoice.Create(Fixtures.Tenant, "INV-2026-0003", Fixtures.Header(), Today.AddDays(-10), null, null, Lines).Value;
        invoice.Send(Now).IsSuccess.ShouldBeTrue();

        invoice.RecordPayment(1m, Today.AddDays(1), null, null, null, Fixtures.User, Today, Now).ShouldFail(CommerceErrors.PaidInFuture, ErrorType.Validation);
        invoice.RecordPayment(1m, Today.AddDays(-11), null, null, null, Fixtures.User, Today, Now).ShouldFail(CommerceErrors.PaidBeforeInvoice, ErrorType.Validation);
        invoice.RecordPayment(1m, Today.AddDays(-10), null, null, null, Fixtures.User, Today, Now).IsSuccess.ShouldBeTrue("fatura günü ve bugün sınırları dahil");
        invoice.RecordPayment(1m, Today, null, null, null, Fixtures.User, Today, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Payment_AmountMustBePositiveWithTwoDecimals() =>
        Should.Throw<ArgumentException>(() => Sent().RecordPayment(0.005m, Today, null, null, null, Fixtures.User, Today, Now));

    [Fact]
    public void RemovingAPayment_LowersPaidAmount_AndRederivesTheStatus()
    {
        var invoice = Sent(Today.AddDays(-1));
        Pay(invoice, 94.56m);
        invoice.EffectiveStatus(Today).ShouldBe(InvoiceStatus.Paid);

        invoice.RemovePayment(invoice.Payments[0].Id, out var removed).IsSuccess.ShouldBeTrue();

        removed.ShouldNotBeNull();
        invoice.PaidAmount.ShouldBe(0m);
        invoice.EffectiveStatus(Today).ShouldBe(InvoiceStatus.Overdue, "vadesi geçmiş ve ödenmemiş");

        invoice.RemovePayment(Guid.NewGuid(), out var missing).IsSuccess.ShouldBeTrue();
        missing.ShouldBeNull();
    }

    [Fact]
    public void SentInvoiceWithAdjustment_UsesGrandTotalForBalance()
    {
        var invoice = Invoice.Create(Fixtures.Tenant, "INV-2026-0004", Fixtures.Header() with { Adjustment = -0.56m }, Today, null, null, Lines).Value;
        invoice.Send(Now).IsSuccess.ShouldBeTrue();

        invoice.GrandTotal.ShouldBe(94.00m);
        invoice.RecordPayment(94.01m, Today, null, null, null, Fixtures.User, Today, Now).ShouldFail(CommerceErrors.InvoicePaymentExceedsBalance, ErrorType.Rule);
        Pay(invoice, 94.00m);
        invoice.EffectiveStatus(Today).ShouldBe(InvoiceStatus.Paid);
    }

    [Fact]
    public void InformationalTaxFields_DoNotEnterTheTotals()
    {
        var invoice = Invoice.Create(Fixtures.Tenant, "INV-2026-0005", Fixtures.Header(), Today, null, null, Lines, new InvoiceExtras("PO-77812", 12.5m, 150m)).Value;

        invoice.GrandTotal.ShouldBe(94.56m);
        (invoice.CustomerPoNumber, invoice.ExciseTax, invoice.SalesCommission).ShouldBe(("PO-77812", 12.5m, 150m));
        Should.Throw<ArgumentOutOfRangeException>(() => Invoice.Create(Fixtures.Tenant, "INV-2026-0006", Fixtures.Header(), Today, null, null, Lines, new InvoiceExtras(null, -1m, null)));
    }
}

/// <summary>Teklif "Müzakere" aşaması, sipariş faturalama/iptal kuralları, satın alma emri makinesi, tedarikçi.</summary>
public sealed class M9CStateMachineDomainTests
{
    private static readonly DateOnly Today = Fixtures.Today;
    private static readonly DateTime Now = Fixtures.Now;

    [Fact]
    public void Quote_Negotiate_IsOnlyFromANonExpiredSentQuote()
    {
        var sent = Fixtures.SentQuote(Today.AddDays(5));
        sent.Negotiate(Today).IsSuccess.ShouldBeTrue();
        sent.Status.ShouldBe(QuoteStatus.Negotiation);
        sent.Negotiate(Today).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);

        Fixtures.NewQuote().Negotiate(Today).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        var expired = Fixtures.SentQuote(Today.AddDays(1));
        var result = expired.Negotiate(Today.AddDays(2));
        result.ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        result.Error.Args!["from"].ShouldBe("expired");
    }

    [Fact]
    public void Quote_FromNegotiation_CanBeAcceptedRejectedRevertedAndExtended()
    {
        Quote InNegotiation(DateOnly? validUntil = null)
        {
            var quote = Fixtures.SentQuote(validUntil ?? Today.AddDays(5));
            quote.Negotiate(Today).IsSuccess.ShouldBeTrue();
            return quote;
        }

        var accepted = InNegotiation();
        accepted.Accept(Today, Now).IsSuccess.ShouldBeTrue();
        accepted.Status.ShouldBe(QuoteStatus.Accepted);
        accepted.AcceptedAt.ShouldBe(Now);

        var rejected = InNegotiation();
        rejected.Reject("pahalı", Today, Now).IsSuccess.ShouldBeTrue();
        rejected.Status.ShouldBe(QuoteStatus.Rejected);

        var reverted = InNegotiation();
        reverted.Revert(Today).IsSuccess.ShouldBeTrue();
        reverted.Status.ShouldBe(QuoteStatus.Draft);

        var extended = InNegotiation();
        extended.Extend(Today.AddDays(30), Today).IsSuccess.ShouldBeTrue();
        extended.Status.ShouldBe(QuoteStatus.Negotiation, "extend durumu değiştirmez");
        extended.ValidUntil.ShouldBe(Today.AddDays(30));

        // Süresi dolan müzakere: etkin durum expired; accept quote.expired; reject/revert/extend serbest.
        var lapsed = InNegotiation(Today.AddDays(1));
        var later = Today.AddDays(3);
        lapsed.EffectiveStatus(later).ShouldBe(QuoteStatus.Expired);
        lapsed.Accept(later, Now).ShouldFail(CommerceErrors.QuoteExpired, ErrorType.Conflict);
        lapsed.Extend(later.AddDays(10), later).IsSuccess.ShouldBeTrue();
        lapsed.EffectiveStatus(later).ShouldBe(QuoteStatus.Negotiation);

        // Kalan hücreler: yalnız sent/negotiation kabul/ret; accepted uçtur.
        accepted.Reject(null, Today, Now).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        accepted.Revert(Today).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
        Fixtures.NewQuote().Accept(Today, Now).ShouldFail(CommerceErrors.QuoteInvalidTransition, ErrorType.Conflict);
    }

    [Fact]
    public void QuoteStatusExpression_TreatsNegotiationLikeSent()
    {
        var today = Today;
        QuoteStatusExpression.Compute(QuoteStatus.Negotiation, today.AddDays(-1), today).ShouldBe(QuoteStatus.Expired);
        QuoteStatusExpression.Compute(QuoteStatus.Negotiation, today, today).ShouldBe(QuoteStatus.Negotiation);
        QuoteStatusExpression.Compute(QuoteStatus.Negotiation, null, today).ShouldBe(QuoteStatus.Negotiation);
        QuoteStatusExpression.Compute(QuoteStatus.Accepted, today.AddDays(-9), today).ShouldBe(QuoteStatus.Accepted);

        var negotiation = Fixtures.SentQuote(today.AddDays(1));
        negotiation.Negotiate(today).IsSuccess.ShouldBeTrue();
        var lapsed = QuoteStatusExpression.IsExpired(today.AddDays(2)).Compile();
        lapsed(negotiation).ShouldBeTrue();
        QuoteStatusExpression.HasEffectiveStatus(QuoteStatus.Negotiation, today.AddDays(2)).Compile()(negotiation).ShouldBeFalse();
        QuoteStatusExpression.HasEffectiveStatus(QuoteStatus.Negotiation, today).Compile()(negotiation).ShouldBeTrue();
        QuoteStatusExpression.HasEffectiveStatus(QuoteStatus.Sent, today).Compile()(negotiation).ShouldBeFalse();
    }

    [Fact]
    public void Order_WithAnActiveInvoice_CannotBeCancelled_AndOnlyConfirmedOrFulfilledIsInvoiceable()
    {
        var order = Fixtures.NewOrder();
        order.EnsureInvoiceable().ShouldFail(CommerceErrors.OrderNotInvoiceable, ErrorType.Conflict);
        order.Confirm().IsSuccess.ShouldBeTrue();
        order.EnsureInvoiceable().IsSuccess.ShouldBeTrue();

        order.Cancel("x", Now, hasActiveInvoice: true).ShouldFail(CommerceErrors.OrderHasActiveInvoice, ErrorType.Conflict);
        order.Status.ShouldBe(SalesOrderStatus.Confirmed);

        order.Fulfill(Now).IsSuccess.ShouldBeTrue();
        order.EnsureInvoiceable().IsSuccess.ShouldBeTrue("fulfilled sipariş de faturalanabilir");
        order.Status.ShouldBe(SalesOrderStatus.Fulfilled, "fatura siparişin durumunu değiştirmez");

        var draft = Fixtures.NewOrder();
        draft.Cancel(null, Now).IsSuccess.ShouldBeTrue();
        draft.EnsureInvoiceable().ShouldFail(CommerceErrors.OrderNotInvoiceable, ErrorType.Conflict);
    }

    [Fact]
    public void Order_ExtraFields_AreValidated_AndAreNotPartOfTheTotals()
    {
        var extras = new OrderExtras(" PO-1 ", Today.AddDays(10), 5m, 10m, "  Onay bekliyor ");
        var order = SalesOrder.Create(Fixtures.Tenant, "SO-2026-0001", Fixtures.Header(), Today, null, [Fixtures.Line()], extras).Value;

        (order.CustomerPoNumber, order.DueDate, order.ExciseTax, order.SalesCommission, order.Pending).ShouldBe(("PO-1", Today.AddDays(10), 5m, 10m, "Onay bekliyor"));
        order.GrandTotal.ShouldBe(240m);

        SalesOrder.Create(Fixtures.Tenant, "SO-2026-0002", Fixtures.Header(), Today, null, [Fixtures.Line()], extras with { DueDate = Today.AddDays(-1) })
            .ShouldFail(CommerceErrors.DueBeforeOrder, ErrorType.Validation);
        order.Update(Fixtures.Header(), Today, [Fixtures.Line()]).IsSuccess.ShouldBeTrue();
        (order.CustomerPoNumber, order.DueDate, order.ExciseTax, order.SalesCommission, order.Pending).ShouldBe((null, null, null, null, null), "tam değiştirme: gönderilmeyen alan temizlenir");
    }

    private static PurchaseOrder NewPo(DateOnly? due = null, params LineInput[] lines) =>
        PurchaseOrder.Create(
            Fixtures.Tenant,
            "PO-2026-0001",
            new PurchaseOrderHeader("Yedek parça", Guid.NewGuid(), null, Fixtures.User, null, null, null),
            Today,
            lines.Length == 0 ? [Fixtures.Line()] : lines,
            new PurchaseOrderExtras(due, null, null)).Value;

    [Fact]
    public void PurchaseOrder_StateMachine_DraftConfirmedReceived_AndCancelFromDraftOrConfirmed()
    {
        var order = NewPo();
        order.Status.ShouldBe(PurchaseOrderStatus.Draft);
        order.Receive(Now).ShouldFail(CommerceErrors.PurchaseOrderInvalidTransition, ErrorType.Conflict);
        order.Confirm(Now).IsSuccess.ShouldBeTrue();
        order.ConfirmedAt.ShouldBe(Now);
        order.Confirm(Now).ShouldFail(CommerceErrors.PurchaseOrderInvalidTransition, ErrorType.Conflict);
        order.Update(new PurchaseOrderHeader("x", order.VendorId, null, Fixtures.User, null, null, null), Today, [Fixtures.Line()])
            .ShouldFail(CommerceErrors.PurchaseOrderNotEditable, ErrorType.Conflict);
        order.Receive(Now).IsSuccess.ShouldBeTrue();
        order.ReceivedAt.ShouldBe(Now);
        order.Cancel(null, Now).ShouldFail(CommerceErrors.PurchaseOrderInvalidTransition, ErrorType.Conflict);

        foreach (var cancellable in new[] { NewPo(), ConfirmedPo() })
        {
            cancellable.Cancel(" iptal ", Now).IsSuccess.ShouldBeTrue();
            cancellable.Status.ShouldBe(PurchaseOrderStatus.Cancelled);
            cancellable.CancelReason.ShouldBe("iptal");
            cancellable.Confirm(Now).ShouldFail(CommerceErrors.PurchaseOrderInvalidTransition, ErrorType.Conflict);
        }
    }

    private static PurchaseOrder ConfirmedPo()
    {
        var order = NewPo();
        order.Confirm(Now).IsSuccess.ShouldBeTrue();
        return order;
    }

    [Fact]
    public void PurchaseOrder_ConfirmRequiresALine_AndDueDateCannotPrecedeThePoDate()
    {
        var empty = PurchaseOrder.Create(Fixtures.Tenant, "PO-2026-0002", new PurchaseOrderHeader("x", Guid.NewGuid(), null, Fixtures.User, null, null, null), Today, []).Value;
        empty.Confirm(Now).ShouldFail(CommerceErrors.PurchaseOrderNoLines, ErrorType.Rule);

        PurchaseOrder.Create(
            Fixtures.Tenant,
            "PO-2026-0003",
            new PurchaseOrderHeader("x", Guid.NewGuid(), null, Fixtures.User, null, null, null),
            Today,
            [Fixtures.Line()],
            new PurchaseOrderExtras(Today.AddDays(-1), null, null)).ShouldFail(CommerceErrors.DueBeforePo, ErrorType.Validation);
        NewPo(Today).EnsureEditable().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Vendor_NormalizesTextAndLowercasesEmail_AndMasksPersonalDataInAudit()
    {
        var vendor = Vendor.Create(
            Fixtures.Tenant,
            "  Acme Tedarik ",
            Fixtures.User,
            " 0532 000 00 00 ",
            "  INFO@Acme.COM ",
            "https://acme.example",
            " Yedek parça ",
            " Cost of Goods Sold ",
            new DocumentAddress(" Sanayi sk. ", null, "Bursa", null, null, null),
            "  not ",
            emailOptOut: true);

        (vendor.Name, vendor.Phone, vendor.Email, vendor.Category, vendor.GlAccount, vendor.Description).ShouldBe(
            ("Acme Tedarik", "0532 000 00 00", "info@acme.com", "Yedek parça", "Cost of Goods Sold", "not"));
        vendor.Address.ShouldBe(new DocumentAddress("Sanayi sk.", null, "Bursa", null, null, null));
        vendor.EmailOptOut.ShouldBeTrue();
        Vendor.SensitiveFields.ShouldBe(["Email", "Phone"], ignoreOrder: true);

        vendor.Update("Yeni ad", Fixtures.User, null, null, null, null, null, null, null, emailOptOut: null);
        vendor.EmailOptOut.ShouldBeTrue("verilmeyen emailOptOut korunur");
        (vendor.Phone, vendor.Email, vendor.Address).ShouldBe((null, null, null), "tam değiştirme");
        vendor.Update("Yeni ad", Fixtures.User, null, null, null, null, null, null, null, emailOptOut: false);
        vendor.EmailOptOut.ShouldBeFalse();
    }
}
