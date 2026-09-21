using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Commerce.Tests.Api;

/// <summary>
/// Fatura: doğrudan oluşturma ve alanlar, durum makinesi, tahsilat defteri (kısmi/tam, fazla ödeme, silme, eşzamanlılık), türetilmiş etkin durum
/// (paid/partiallyPaid/overdue), liste süzgeçleri/sıralama, numaralandırma (eşzamanlılık, yıl dönümü, geri alma), olaylar.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvoiceApiTests(CrmApiFactory factory)
{
    private static int TenantYear() => TenantToday().Year;

    // Σ lineTotal = 94.56 (belge {1,3}).
    private static readonly object[] DocumentOneAndThree = [Line(3m, 19.99m, 10m, 20m, "Lisans"), Line(2.5m, 10.10m, 0m, 18m, "Danışmanlık")];

    private async Task<decimal> PaidSumAsync(Guid invoiceId) =>
        await factory.ScalarAsync<decimal?>("SELECT COALESCE(sum(amount), 0) FROM commerce.invoice_payments WHERE invoice_id = @i", ("i", invoiceId)) ?? 0m;

    private async Task<decimal> StoredPaidAsync(Guid invoiceId) =>
        await factory.ScalarAsync<decimal>("SELECT paid_amount FROM commerce.invoices WHERE id = @i", ("i", invoiceId));

    // ---- CRUD ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_DirectInvoice_IsADraft_WithServerNumberTotalsAndFields()
    {
        var org = await factory.NewOrgAsync("Fatura Oluştur");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Acme");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = account.Id() });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Fırsat", accountId = account.Id() });
        var today = TenantToday();

        var invoice = await admin.CreateInvoiceAsync(
            account.Id(),
            new
            {
                contactId = contact.Id(),
                dealId = deal.Id(),
                invoiceDate = today.DateString(),
                dueDate = today.AddDays(30).DateString(),
                customerPoNumber = "PO-77812",
                exciseTax = 0m,
                salesCommission = 150.00m,
                carrier = "Yurtiçi Kargo",
                adjustment = -0.56m,
                billingAddress = new { street = "Atatürk Cd. 12", city = "İstanbul" },
                terms = "Vade 30 gün.",
                orderId = Guid.NewGuid(),
                grandTotal = 5m,
                paidAmount = 5m,
                status = "paid",
            },
            DocumentOneAndThree);

        invoice.Str("number").ShouldBe($"INV-{TenantYear()}-0001");
        invoice.Str("status").ShouldBe("draft");
        invoice.Dec("grandTotal").ShouldBe(94.00m);
        invoice.Dec("paidAmount").ShouldBe(0m);
        invoice.Dec("balanceAmount").ShouldBe(94.00m);
        invoice.Dec("adjustment").ShouldBe(-0.56m);
        invoice.Str("invoiceDate").ShouldBe(today.DateString());
        invoice.Str("dueDate").ShouldBe(today.AddDays(30).DateString());
        invoice.Str("customerPoNumber").ShouldBe("PO-77812");
        invoice.Dec("salesCommission").ShouldBe(150m);
        invoice.Str("carrier").ShouldBe("Yurtiçi Kargo");
        invoice.GetProperty("contactId").GetGuid().ShouldBe(contact.Id());
        invoice.GetProperty("dealId").GetGuid().ShouldBe(deal.Id());
        invoice.Str("accountName").ShouldBe("Acme");
        invoice.TryGetProperty("orderId", out _).ShouldBeFalse("orderId gövdede kabul edilmez");
        invoice.GetProperty("payments").GetArrayLength().ShouldBe(0);
        invoice.GetProperty("lines").GetArrayLength().ShouldBe(2);

        (await factory.OutboxCountAsync(org.TenantId, "InvoiceCreated")).ShouldBe(1);
        var payload = JsonDocument.Parse(await factory.ScalarAsync<string>("SELECT payload::text FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE '%InvoiceCreated%'", ("t", org.TenantId))).RootElement;
        payload.GetProperty("source").GetString().ShouldBe("direct");
        payload.GetProperty("invoiceId").GetGuid().ShouldBe(invoice.Id());
        payload.GetProperty("grandTotal").GetDecimal().ShouldBe(94.00m);
    }

    [Fact]
    public async Task Create_DefaultsInvoiceDateToTodayAndValidatesLinkedRecords()
    {
        var admin = (await factory.NewOrgAsync("Fatura Bağlar")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var other = await admin.CreateAccountAsync("Diğer");
        var otherContact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Y", accountId = other.Id() });
        var otherDeal = await admin.PostJsonAsync($"{Base}/deals", new { name = "F", accountId = other.Id() });

        (await admin.CreateInvoiceAsync(account.Id())).Str("invoiceDate").ShouldBe(TenantToday().DateString());
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = account.Id(), contactId = otherContact.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "commerce.contact_account_mismatch");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = account.Id(), dealId = otherDeal.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "commerce.deal_account_mismatch");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = account.Id(), ownerUserId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "", accountId = account.Id() }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = account.Id(), currency = "XXX9" }, Ct)).ShouldBeValidationErrorAsync("currency");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = account.Id(), lines = new[] { Line(0m) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].quantity");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = account.Id(), invoiceDate = TenantToday().DateString(), dueDate = TenantToday().AddDays(-1).DateString() }, Ct)).ShouldBeValidationErrorAsync("dueDate");
    }

    [Fact]
    public async Task Update_IsAFullReplacement_OnlyForDrafts_AndKeepsNumberAndOrder()
    {
        var admin = (await factory.NewOrgAsync("Fatura Güncelle")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var invoice = await admin.CreateInvoiceAsync(account.Id(), new { customerPoNumber = "A", carrier = "Aras", exciseTax = 5m, dueDate = TenantToday().AddDays(9).DateString() });
        var url = $"{InvoicesPath}/{invoice.Id()}";

        await admin.PutJsonAsync(url, new { subject = "Yeni konu", accountId = account.Id(), lines = new[] { Line(1m, 50m, 0m, 0m) } });

        var replaced = await admin.GetJsonAsync(url);
        replaced.Str("subject").ShouldBe("Yeni konu");
        replaced.Str("number").ShouldBe(invoice.Str("number"));
        replaced.Dec("grandTotal").ShouldBe(50m);
        replaced.Str("invoiceDate").ShouldBe(TenantToday().DateString(), "invoiceDate verilmezse mevcut korunur");
        foreach (var field in new[] { "customerPoNumber", "carrier", "exciseTax", "dueDate" })
        {
            replaced.TryGetProperty(field, out _).ShouldBeFalse($"{field} temizlenir");
        }

        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        await (await admin.PutAsJsonAsync(url, new { subject = "x", accountId = account.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.not_editable");
        await (await admin.DeleteAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.not_editable");
    }

    [Fact]
    public async Task Delete_IsSoft_AndTheNumberIsNeverReused()
    {
        var admin = (await factory.NewOrgAsync("Fatura Sil")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var first = await admin.CreateInvoiceAsync(account.Id());
        var second = await admin.CreateInvoiceAsync(account.Id());

        await admin.DeleteJsonAsync($"{InvoicesPath}/{second.Id()}");

        await (await admin.GetAsync($"{InvoicesPath}/{second.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.ListIdsAsync(InvoicesPath)).ShouldBe([first.Id()]);
        (await admin.CreateInvoiceAsync(account.Id())).Str("number").ShouldBe($"INV-{TenantYear()}-0003");
        (await factory.ScalarAsync<bool>("SELECT is_deleted FROM commerce.invoices WHERE id = @i", ("i", second.Id()))).ShouldBeTrue();
    }

    // ---- Durum makinesi ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lifecycle_SendRevertCancel_WithEventsAndTransitionErrors()
    {
        var org = await factory.NewOrgAsync("Fatura Yaşam Döngüsü");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var empty = await admin.PostJsonAsync(InvoicesPath, new { subject = "Boş", accountId = account.Id() });
        var invoice = await admin.CreateInvoiceAsync(account.Id());
        var url = $"{InvoicesPath}/{invoice.Id()}";

        await (await admin.ActAsync($"{InvoicesPath}/{empty.Id()}/send")).ReadProblemAsync(HttpStatusCode.UnprocessableEntity, "invoice.no_lines");
        await (await admin.ActAsync($"{url}/revert")).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.invalid_transition");
        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        var sent = await admin.GetJsonAsync(url);
        sent.Str("status").ShouldBe("sent");
        sent.TryGetProperty("sentAt", out _).ShouldBeTrue();
        var problem = await (await admin.ActAsync($"{url}/send")).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.invalid_transition");
        problem.GetProperty("args").GetProperty("from").GetString().ShouldBe("sent");
        problem.GetProperty("args").GetProperty("to").GetString().ShouldBe("sent");
        (await factory.OutboxCountAsync(org.TenantId, "InvoiceSent")).ShouldBe(1);

        await admin.ActAsync($"{url}/revert").ShouldBeNoContentAsync();
        var reverted = await admin.GetJsonAsync(url);
        reverted.Str("status").ShouldBe("draft");
        reverted.TryGetProperty("sentAt", out _).ShouldBeFalse();

        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{url}/cancel", new { reason = "  hatalı fatura " }).ShouldBeNoContentAsync();
        var cancelled = await admin.GetJsonAsync(url);
        cancelled.Str("status").ShouldBe("cancelled");
        cancelled.Str("cancelReason").ShouldBe("hatalı fatura");
        cancelled.TryGetProperty("cancelledAt", out _).ShouldBeTrue();
        (await factory.OutboxCountAsync(org.TenantId, "InvoiceCancelled")).ShouldBe(1);

        foreach (var action in new[] { "send", "revert", "cancel" })
        {
            await (await admin.ActAsync($"{url}/{action}", new { })).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.invalid_transition");
        }

        await (await admin.PostAsJsonAsync($"{url}/cancel", new { reason = new string('x', 1001) }, Ct)).ShouldBeValidationErrorAsync("reason");
        await (await admin.PayRawAsync(invoice.Id(), 1m)).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.not_payable");
    }

    // ---- Tahsilat ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Payments_50Then44_56_SettleTheInvoice_PaidIsPublishedOnce_AndOverpaymentIsRejected()
    {
        var org = await factory.NewOrgAsync("Fatura Tahsilat");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var invoice = await admin.SentInvoiceAsync(account.Id(), null, DocumentOneAndThree);
        var url = $"{InvoicesPath}/{invoice.Id()}";
        invoice.Dec("grandTotal").ShouldBe(94.56m);

        var over = await admin.PayRawAsync(invoice.Id(), 94.57m);
        var problem = await over.ReadProblemAsync(HttpStatusCode.UnprocessableEntity, "invoice.payment_exceeds_balance");
        problem.GetProperty("args").GetProperty("balance").GetDecimal().ShouldBe(94.56m);

        var first = await admin.PayAsync(invoice.Id(), 50.00m, new { paidOn = TenantToday().DateString(), method = "bankTransfer", reference = "EFT-1", notes = "İlk taksit" });
        first.Dec("amount").ShouldBe(50.00m);
        first.Str("method").ShouldBe("bankTransfer");
        first.Str("reference").ShouldBe("EFT-1");
        first.GetProperty("recordedByUserId").GetGuid().ShouldBe(org.AdminUserId);
        first.TryGetProperty("recordedAt", out _).ShouldBeTrue();
        var afterFirst = await admin.GetJsonAsync(url);
        afterFirst.Str("status").ShouldBe("partiallyPaid");
        afterFirst.Dec("paidAmount").ShouldBe(50.00m);
        afterFirst.Dec("balanceAmount").ShouldBe(44.56m);
        (await factory.OutboxCountAsync(org.TenantId, "InvoicePaid")).ShouldBe(0);

        await (await admin.PayRawAsync(invoice.Id(), 44.57m)).ReadProblemAsync(HttpStatusCode.UnprocessableEntity, "invoice.payment_exceeds_balance");
        await admin.PayAsync(invoice.Id(), 44.56m);
        var paid = await admin.GetJsonAsync(url);
        paid.Str("status").ShouldBe("paid");
        paid.Dec("balanceAmount").ShouldBe(0m);
        paid.GetProperty("payments").GetArrayLength().ShouldBe(2);
        (await StoredPaidAsync(invoice.Id())).ShouldBe(await PaidSumAsync(invoice.Id()), "paid_amount == Σ tahsilat");
        (await factory.OutboxCountAsync(org.TenantId, "InvoicePaid")).ShouldBe(1, "bakiyenin 0'a ilk inişinde tek olay");
        var payload = JsonDocument.Parse(await factory.ScalarAsync<string>("SELECT payload::text FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE '%InvoicePaid%'", ("t", org.TenantId))).RootElement;
        payload.GetProperty("invoiceId").GetGuid().ShouldBe(invoice.Id());
        payload.GetProperty("grandTotal").GetDecimal().ShouldBe(94.56m);

        await (await admin.PayRawAsync(invoice.Id(), 0.01m)).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.not_payable");
        await (await admin.ActAsync($"{url}/revert")).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.has_payments");
        await (await admin.ActAsync($"{url}/cancel")).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.has_payments");
        (await admin.ListIdsAsync(InvoicesPath, "?status=paid")).ShouldBe([invoice.Id()]);
    }

    [Fact]
    public async Task Payments_Validation_AndPayableStates()
    {
        var admin = (await factory.NewOrgAsync("Fatura Tahsilat Kural")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var draft = await admin.CreateInvoiceAsync(account.Id());
        var today = TenantToday();
        var invoice = await admin.SentInvoiceAsync(account.Id(), new { invoiceDate = today.AddDays(-5).DateString() });
        var payments = $"{InvoicesPath}/{invoice.Id()}/payments";

        await (await admin.PayRawAsync(draft.Id(), 10m)).ReadProblemAsync(HttpStatusCode.Conflict, "invoice.not_payable");
        await (await admin.PostAsJsonAsync(payments, new { amount = 0m }, Ct)).ShouldBeValidationErrorAsync("amount");
        await (await admin.PostAsJsonAsync(payments, new { amount = -5m }, Ct)).ShouldBeValidationErrorAsync("amount");
        await (await admin.PostAsJsonAsync(payments, new { amount = 1.005m }, Ct)).ShouldBeValidationErrorAsync("amount");
        await (await admin.PostAsJsonAsync(payments, new { amount = 10m, reference = new string('x', 101) }, Ct)).ShouldBeValidationErrorAsync("reference");
        await (await admin.PostAsJsonAsync(payments, new { amount = 10m, notes = new string('x', 501) }, Ct)).ShouldBeValidationErrorAsync("notes");
        (await admin.PostAsJsonAsync(payments, new { amount = 10m, method = "bitcoin" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await (await admin.PostAsJsonAsync(payments, new { amount = 10m, paidOn = today.AddDays(1).DateString() }, Ct)).ShouldBeValidationErrorAsync("paidOn");
        await (await admin.PostAsJsonAsync(payments, new { amount = 10m, paidOn = today.AddDays(-6).DateString() }, Ct)).ShouldBeValidationErrorAsync("paidOn");
        await (await admin.PostAsJsonAsync($"{InvoicesPath}/{Guid.NewGuid()}/payments", new { amount = 10m }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await factory.CountAsync("invoice_payments", (await factory.ScalarAsync<Guid>("SELECT tenant_id FROM commerce.invoices WHERE id = @i", ("i", invoice.Id()))))).ShouldBe(0);

        var ok = await admin.PostJsonAsync(payments, new { amount = 10m, paidOn = today.AddDays(-5).DateString() });
        ok.Str("paidOn").ShouldBe(today.AddDays(-5).DateString());
        (await admin.PostAsJsonAsync(payments, new { amount = 10m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DeletingAPayment_RederivesTheStatus_AndInvoicesWithoutPaymentsCanBeReverted()
    {
        var admin = (await factory.NewOrgAsync("Fatura Tahsilat Sil")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var invoice = await admin.SentInvoiceAsync(account.Id(), null, Line(1m, 100m, 0m, 0m));
        var url = $"{InvoicesPath}/{invoice.Id()}";
        var partial = await admin.PayAsync(invoice.Id(), 40m);
        var rest = await admin.PayAsync(invoice.Id(), 60m);
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("paid");

        await admin.DeleteJsonAsync($"{url}/payments/{rest.Id()}");
        var afterDelete = await admin.GetJsonAsync(url);
        afterDelete.Str("status").ShouldBe("partiallyPaid");
        afterDelete.Dec("paidAmount").ShouldBe(40m);
        (await StoredPaidAsync(invoice.Id())).ShouldBe(await PaidSumAsync(invoice.Id()));

        await (await admin.DeleteAsync($"{url}/payments/{rest.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.DeleteAsync($"{url}/payments/{Guid.NewGuid()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await admin.DeleteJsonAsync($"{url}/payments/{partial.Id()}");
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("sent");

        // Kapanan bakiye yeniden ödenirse InvoicePaid yeniden yayınlanır (tüketici idempotent olmalı).
        await admin.PayAsync(invoice.Id(), 100m);
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("paid");
    }

    [Fact]
    public async Task ConcurrentPayments_NeverOverpay_TheSecondWriterLoses()
    {
        var org = await factory.NewOrgAsync("Fatura Tahsilat Yarış");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var conflicts = 0;

        for (var round = 0; round < 6; round++)
        {
            var invoice = await admin.SentInvoiceAsync(account.Id(), new { subject = $"Yarış {round}" }, Line(1m, 100m, 0m, 0m));

            var responses = await Task.WhenAll(admin.PayRawAsync(invoice.Id(), 60m), admin.PayRawAsync(invoice.Id(), 60m));

            var statuses = responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
            statuses[0].ShouldBe(HttpStatusCode.Created, $"tur {round}: tam olarak biri kazanır");
            statuses[1].ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity);
            var loser = responses.Single(r => r.StatusCode != HttpStatusCode.Created);
            var code = JsonDocument.Parse(await loser.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("code").GetString();
            code.ShouldBeOneOf("commerce.concurrent_update", "invoice.payment_exceeds_balance");
            if (code == "commerce.concurrent_update")
            {
                conflicts++;
            }

            (await StoredPaidAsync(invoice.Id())).ShouldBe(60m, "aşırı ödeme asla oluşmaz");
            (await PaidSumAsync(invoice.Id())).ShouldBe(60m);
            (await admin.GetJsonAsync($"{InvoicesPath}/{invoice.Id()}")).GetProperty("payments").GetArrayLength().ShouldBe(1);
        }

        conflicts.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ConcurrentPayments_ThatFit_BothSucceedOrTheLoserIsToldToRetry()
    {
        var admin = (await factory.NewOrgAsync("Fatura Tahsilat Yarış 2")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var invoice = await admin.SentInvoiceAsync(account.Id(), null, Line(1m, 100m, 0m, 0m));

        var responses = await Task.WhenAll(admin.PayRawAsync(invoice.Id(), 30m), admin.PayRawAsync(invoice.Id(), 30m), admin.PayRawAsync(invoice.Id(), 30m));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        created.ShouldBeGreaterThanOrEqualTo(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
        (await StoredPaidAsync(invoice.Id())).ShouldBe(created * 30m);
        (await PaidSumAsync(invoice.Id())).ShouldBe(created * 30m, "paid_amount == Σ tahsilat");
    }

    // ---- Türetilmiş durum (vade aşımı) ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Overdue_IsDerived_ConsistentlyInListDetailAndReport_WithoutAScheduledJob()
    {
        var org = await factory.NewOrgAsync("Fatura Vade");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var today = TenantToday();
        var dueToday = await admin.SentInvoiceAsync(account.Id(), new { subject = "Vadesi bugün", dueDate = today.DateString() }, Line(1m, 100m, 0m, 0m));
        var future = await admin.SentInvoiceAsync(account.Id(), new { subject = "Vadesi ileri", dueDate = today.AddDays(9).DateString() }, Line(1m, 200m, 0m, 0m));
        var late = await admin.SentInvoiceAsync(account.Id(), new { subject = "Vadesi geçti", dueDate = today.AddDays(9).DateString() }, Line(1m, 300m, 0m, 0m));
        var latePartial = await admin.SentInvoiceAsync(account.Id(), new { subject = "Vadesi geçti kısmi", dueDate = today.AddDays(9).DateString() }, Line(1m, 400m, 0m, 0m));
        var partial = await admin.SentInvoiceAsync(account.Id(), new { subject = "Kısmi", dueDate = today.AddDays(9).DateString() }, Line(1m, 500m, 0m, 0m));
        var paidLate = await admin.SentInvoiceAsync(account.Id(), new { subject = "Ödenmiş", dueDate = today.AddDays(9).DateString() }, Line(1m, 600m, 0m, 0m));
        var noDue = await admin.SentInvoiceAsync(account.Id(), new { subject = "Vadesiz" }, Line(1m, 700m, 0m, 0m));
        var draft = await admin.CreateInvoiceAsync(account.Id(), new { subject = "Taslak", dueDate = today.AddDays(9).DateString() }, Line(1m, 800m, 0m, 0m));
        var cancelled = await admin.CreateInvoiceAsync(account.Id(), new { subject = "İptal", dueDate = today.AddDays(9).DateString() }, Line(1m, 900m, 0m, 0m));
        await admin.ActAsync($"{InvoicesPath}/{cancelled.Id()}/cancel").ShouldBeNoContentAsync();
        await admin.PayAsync(latePartial.Id(), 150m);
        await admin.PayAsync(partial.Id(), 100m);
        await admin.PayAsync(paidLate.Id(), 600m);

        // Vade günü geçince (vade tarihleri geçmişe çekilir; fatura tarihi de öne alınır): zamanlanmış iş yok, durum türetilir.
        foreach (var id in new[] { late.Id(), latePartial.Id(), paidLate.Id(), draft.Id(), cancelled.Id() })
        {
            await factory.ExecuteAsync("UPDATE commerce.invoices SET invoice_date = @i, due_date = @d WHERE id = @id", ("i", today.AddDays(-40)), ("d", today.AddDays(-1)), ("id", id));
        }

        async Task<string> Status(JsonElement invoice) => (await admin.GetJsonAsync($"{InvoicesPath}/{invoice.Id()}")).Str("status");
        (await Status(dueToday)).ShouldBe("sent", "vade günü geçmemiş sayılır");
        (await Status(future)).ShouldBe("sent");
        (await Status(late)).ShouldBe("overdue");
        (await Status(latePartial)).ShouldBe("overdue", "vadesi geçmiş kısmi ödemeli fatura overdue görünür");
        (await Status(partial)).ShouldBe("partiallyPaid");
        (await Status(paidLate)).ShouldBe("paid", "tam ödeme vadeden önceliklidir");
        (await Status(noDue)).ShouldBe("sent");
        (await Status(draft)).ShouldBe("draft", "taslak asla türetilmez");
        (await Status(cancelled)).ShouldBe("cancelled");

        var overdueIds = await admin.ListIdsAsync(InvoicesPath, "?status=overdue");
        overdueIds.OrderBy(i => i).ShouldBe(new[] { late.Id(), latePartial.Id() }.OrderBy(i => i));
        (await admin.ListIdsAsync(InvoicesPath, "?status=partiallyPaid")).ShouldBe([partial.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, "?status=paid")).ShouldBe([paidLate.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, "?status=sent")).OrderBy(i => i).ShouldBe(new[] { dueToday.Id(), future.Id(), noDue.Id() }.OrderBy(i => i));
        (await admin.ListIdsAsync(InvoicesPath, "?status=draft")).ShouldBe([draft.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, "?status=cancelled")).ShouldBe([cancelled.Id()]);
        var listed = (await admin.GetJsonAsync($"{InvoicesPath}?status=overdue")).GetProperty("items").EnumerateArray().ToList();
        listed.ShouldAllBe(i => i.Str("status") == "overdue");
        listed.Single(i => i.Id() == latePartial.Id()).Dec("balanceAmount").ShouldBe(250m);

        var report = (await admin.GetJsonAsync($"{Base}/reports/commerce/summary")).GetProperty("invoices");
        Count(report, "overdue").ShouldBe((2, 700m));
        Count(report, "sent").ShouldBe((3, 1000m));
        Count(report, "partiallyPaid").ShouldBe((1, 500m));
        Count(report, "paid").ShouldBe((1, 600m));
        Count(report, "draft").ShouldBe((1, 800m));
        Count(report, "cancelled").ShouldBe((1, 900m));
        report.GetProperty("overdueCount").GetInt32().ShouldBe(2);
        report.Dec("overdueAmount").ShouldBe(550m, "vadesi geçenlerin bakiye toplamı: 300 + (400 − 150)");
        report.GetProperty("byStatus").EnumerateArray().Select(r => r.Str("status")).ShouldBe(["draft", "sent", "partiallyPaid", "paid", "overdue", "cancelled"]);
        report.GetProperty("totalCount").GetInt32().ShouldBe(8, "iptaller hariç, taslaklar dahil");
        report.Dec("totalAmount").ShouldBe(3600m);
        report.Dec("paidAmount").ShouldBe(850m, "150 + 100 + 600");
        report.Dec("outstandingAmount").ShouldBe(1950m, "açık (sent, partiallyPaid, overdue) faturaların bakiyesi: 100 + 200 + 300 + 250 + 400 + 700");
    }

    private static (int Count, decimal Amount) Count(JsonElement group, string status)
    {
        var row = group.GetProperty("byStatus").EnumerateArray().Single(r => r.Str("status") == status);
        return (row.GetProperty("count").GetInt32(), row.Dec("amount"));
    }

    [Fact]
    public async Task DueDateBoundary_UsesTheTenantTimeZone_ToTheNextLocalDay()
    {
        var clock = new TestClock();
        await using var derived = factory.Derive(clock);
        var org = await NewOrgAsync(derived.CreateClient, "Fatura Vade Sınırı");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");

        // Europe/Istanbul: 2026-10-14T20:30Z hâlâ 14 Ekim (vade günü); 21:30Z = 15 Ekim yerel → vade ertesi gün geçmiş.
        clock.Override = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
        var invoice = await admin.SentInvoiceAsync(account.Id(), new { invoiceDate = "2026-10-01", dueDate = "2026-10-14" }, Line(1m, 100m, 0m, 0m));
        var url = $"{InvoicesPath}/{invoice.Id()}";

        clock.Override = new DateTimeOffset(2026, 10, 14, 20, 30, 0, TimeSpan.Zero);
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("sent");
        clock.Override = new DateTimeOffset(2026, 10, 14, 21, 30, 0, TimeSpan.Zero);
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("overdue");
        (await admin.ListIdsAsync(InvoicesPath, "?status=overdue")).ShouldBe([invoice.Id()]);
        (await admin.GetJsonAsync($"{Base}/reports/commerce/summary?from=2026-10-01&to=2026-10-31")).GetProperty("invoices").GetProperty("overdueCount").GetInt32().ShouldBe(1);

        // Tahsilat tarihi "bugün"den sonra olamaz: kiracı saatine göre 15 Ekim.
        (await admin.PostAsJsonAsync($"{url}/payments", new { amount = 10m, paidOn = "2026-10-16" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{url}/payments", new { amount = 10m, paidOn = "2026-10-15" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        clock.Override = null;
    }

    // ---- Liste ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_FiltersSortsAndSearches()
    {
        var admin = (await factory.NewOrgAsync("Fatura Liste")).Admin;
        var acme = await admin.CreateAccountAsync("Acme");
        var beta = await admin.CreateAccountAsync("Beta");
        var today = TenantToday();
        var a = await admin.CreateInvoiceAsync(acme.Id(), new { subject = "Alfa", customerPoNumber = "PO-100", invoiceDate = today.AddDays(-3).DateString(), dueDate = today.AddDays(10).DateString() }, Line(1m, 300m, 0m, 0m));
        var b = await admin.CreateInvoiceAsync(beta.Id(), new { subject = "Beta %50", invoiceDate = today.AddDays(-2).DateString() }, Line(1m, 100m, 0m, 0m));
        var c = await admin.CreateInvoiceAsync(acme.Id(), new { subject = "Gama_x", invoiceDate = today.AddDays(-1).DateString(), dueDate = today.AddDays(5).DateString() }, Line(1m, 200m, 0m, 0m));
        await admin.ActAsync($"{InvoicesPath}/{a.Id()}/send").ShouldBeNoContentAsync();
        await admin.PayAsync(a.Id(), 100m);

        (await admin.ListIdsAsync(InvoicesPath, $"?accountId={acme.Id()}&sort=number")).ShouldBe([a.Id(), c.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, "?q=po-100")).ShouldBe([a.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, $"?q={a.Str("number")}")).ShouldBe([a.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, "?q=%25")).ShouldBe([b.Id()], "joker karakter kaçışlanır");
        (await admin.ListIdsAsync(InvoicesPath, "?q=_")).ShouldBe([c.Id()], "alt çizgi joker değil");
        (await admin.ListIdsAsync(InvoicesPath, $"?invoiceFrom={today.AddDays(-2).DateString()}&invoiceTo={today.AddDays(-1).DateString()}&sort=number")).ShouldBe([b.Id(), c.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, $"?dueFrom={today.AddDays(6).DateString()}")).ShouldBe([a.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, $"?dueTo={today.AddDays(5).DateString()}")).ShouldBe([c.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, "?sort=dueDate")).ShouldBe([c.Id(), a.Id(), b.Id()], "boş vade sonda");
        (await admin.ListIdsAsync(InvoicesPath, "?sort=-dueDate")).ShouldBe([a.Id(), c.Id(), b.Id()], "boş vade her yönde sonda");
        (await admin.ListIdsAsync(InvoicesPath, "?sort=-balanceAmount")).ShouldBe([a.Id(), c.Id(), b.Id()], "eşit bakiyede Id ile kararlı");
        (await admin.ListIdsAsync(InvoicesPath, "?sort=grandTotal")).ShouldBe([b.Id(), c.Id(), a.Id()]);
        (await admin.ListIdsAsync(InvoicesPath, "?sort=bilinmeyen,number")).ShouldBe([a.Id(), b.Id(), c.Id()], "bilinmeyen alan yok sayılır");

        var page = await admin.GetJsonAsync($"{InvoicesPath}?pageSize=2&page=2&sort=number");
        page.GetProperty("totalCount").GetInt64().ShouldBe(3);
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        var summary = (await admin.GetJsonAsync($"{InvoicesPath}?q=Alfa")).GetProperty("items")[0];
        (summary.Dec("grandTotal"), summary.Dec("paidAmount"), summary.Dec("balanceAmount")).ShouldBe((300m, 100m, 200m));
        summary.Str("accountName").ShouldBe("Acme");
    }

    // ---- Numaralandırma ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwentyParallelCreates_ProduceGaplessNumbers_AndOtherKindsAndTenantsAreIndependent()
    {
        var a = await factory.NewOrgAsync("Fatura Numara A");
        var b = await factory.NewOrgAsync("Fatura Numara B");
        var accountA = await a.Admin.CreateAccountAsync("A firma");
        var accountB = await b.Admin.CreateAccountAsync("B firma");
        var vendorA = await a.Admin.CreateVendorAsync("Tedarikçi");
        var year = TenantYear();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            a.Admin.PostAsJsonAsync(InvoicesPath, new { subject = $"Paralel {i}", accountId = accountA.Id(), lines = new[] { Line() } }, Ct)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        var numbers = (await a.Admin.ListNumbersAsync(InvoicesPath, "?sort=number&pageSize=100")).ToList();
        numbers.ShouldBe(Enumerable.Range(1, 20).Select(n => $"INV-{year}-{n:D4}"));
        (await factory.CounterAsync(a.TenantId, "invoice", year)).ShouldBe(20);

        (await a.Admin.CreatePurchaseOrderAsync(vendorA.Id())).Str("number").ShouldBe($"PO-{year}-0001");
        (await a.Admin.CreateQuoteAsync(accountA.Id())).Str("number").ShouldBe($"Q-{year}-0001");
        (await a.Admin.CreateOrderAsync(accountA.Id())).Str("number").ShouldBe($"SO-{year}-0001");
        (await b.Admin.CreateInvoiceAsync(accountB.Id())).Str("number").ShouldBe($"INV-{year}-0001", "kiracı bağımsız");
        (await factory.CounterAsync(a.TenantId, "purchaseOrder", year)).ShouldBe(1);
    }

    [Fact]
    public async Task Year_RollsOverInTheTenantTimeZone_AndAFailedSaveRollsTheCounterBack()
    {
        var clock = new TestClock();
        await using var derived = factory.Derive(clock);
        var org = await NewOrgAsync(derived.CreateClient, "Fatura Numara Yıl");
        var account = await org.Admin.CreateAccountAsync("Firma");

        clock.Override = new DateTimeOffset(2026, 12, 31, 20, 30, 0, TimeSpan.Zero);
        (await org.Admin.CreateInvoiceAsync(account.Id())).Str("number").ShouldBe("INV-2026-0001");
        clock.Override = new DateTimeOffset(2026, 12, 31, 21, 30, 0, TimeSpan.Zero);
        var firstOf2027 = await org.Admin.CreateInvoiceAsync(account.Id());
        firstOf2027.Str("number").ShouldBe("INV-2027-0001");
        firstOf2027.Str("invoiceDate").ShouldBe("2027-01-01");

        Faults.Mode = Faults.FailOnInvoiceInsert;
        try
        {
            (await org.Admin.PostAsJsonAsync(InvoicesPath, new { subject = "Patlayan", accountId = account.Id(), lines = new[] { Line() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            Faults.Mode = null;
        }

        (await factory.CounterAsync(org.TenantId, "invoice", 2027)).ShouldBe(1, "sayaç geri döndü");
        (await factory.CountAsync("invoices", org.TenantId)).ShouldBe(2);
        (await org.Admin.CreateInvoiceAsync(account.Id())).Str("number").ShouldBe("INV-2027-0002", "boşluksuz");
        clock.Override = null;
    }
}

internal static class InvoiceTestKit
{
    /// <summary>Tahsilat isteği (durum kodu denetlenmeden).</summary>
    public static Task<HttpResponseMessage> PayRawAsync(this HttpClient client, Guid invoiceId, decimal amount) =>
        client.PostAsJsonAsync($"{CommerceApiKit.InvoicesPath}/{invoiceId}/payments", new { amount }, CommerceApiKit.Ct);

}
