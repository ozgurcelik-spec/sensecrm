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
/// Sipariş → fatura dönüşümü: tek transaction (numara sayacı + fatura + kalemler + outbox), aktif fatura başına tek sipariş (kısmi benzersiz indeks + danışma kilidi),
/// hata enjeksiyonunda tam geri alma, iptal/silme sonrası yeniden faturalama, sipariş iptal kuralı.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvoiceConversionApiTests(CrmApiFactory factory)
{
    private static int TenantYear() => TenantToday().Year;

    private static readonly object[] VectorLines =
    [
        Line(3m, 19.99m, 10m, 20m, "Lisans"),
        Line(2.5m, 10.10m, 0m, 18m, "Danışmanlık"),
        Line(1m, 0.05m, 50m, 20m, "Küçük kalem"),
    ];

    [Fact]
    public async Task Convert_ConfirmedOrder_CreatesADraftInvoice_CopyingEverything_AndRecomputingIdenticalTotals()
    {
        var org = await factory.NewOrgAsync("Fatura Dönüşüm");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.orders.read");
        var account = await admin.CreateAccountAsync("Acme");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = account.Id() });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Fırsat", accountId = account.Id() });
        var book = await admin.CreatePriceBookAsync("Bayi", "flat");
        var today = TenantToday();
        var order = await admin.ConfirmedOrderAsync(
            account.Id(),
            new
            {
                contactId = contact.Id(),
                dealId = deal.Id(),
                ownerUserId = memberId,
                currency = "TRY",
                terms = "Peşin",
                notes = "Not",
                carrier = "Aras",
                adjustment = -0.56m,
                billingAddress = new { street = "Atatürk Cd. 12", city = "İstanbul" },
                shippingAddress = new { city = "Bursa" },
                priceBookId = book.Id(),
                customerPoNumber = "PO-77812",
                exciseTax = 12.5m,
                salesCommission = 150m,
                pending = "Onay bekliyor",
                dueDate = today.AddDays(20).DateString(),
            },
            VectorLines);
        var orderFull = await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}");

        var response = await admin.PostAsJsonAsync($"{OrdersPath}/{order.Id()}/invoice", new { invoiceDate = today.DateString(), dueDate = today.AddDays(30).DateString() }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var invoice = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.Headers.Location!.ToString().ShouldEndWith($"{InvoicesPath}/{invoice.Id()}");
        invoice.Str("number").ShouldBe($"INV-{TenantYear()}-0001");
        invoice.Str("status").ShouldBe("draft");
        invoice.GetProperty("orderId").GetGuid().ShouldBe(order.Id());
        invoice.Str("orderNumber").ShouldBe(order.Str("number"));
        invoice.Str("subject").ShouldBe(order.Str("subject"));
        invoice.GetProperty("accountId").GetGuid().ShouldBe(account.Id());
        invoice.GetProperty("contactId").GetGuid().ShouldBe(contact.Id());
        invoice.GetProperty("dealId").GetGuid().ShouldBe(deal.Id());
        invoice.GetProperty("ownerUserId").GetGuid().ShouldBe(memberId);
        invoice.Str("terms").ShouldBe("Peşin");
        invoice.Str("notes").ShouldBe("Not");
        invoice.Str("carrier").ShouldBe("Aras");
        invoice.GetProperty("billingAddress").GetProperty("street").GetString().ShouldBe("Atatürk Cd. 12");
        invoice.GetProperty("shippingAddress").GetProperty("city").GetString().ShouldBe("Bursa");
        invoice.GetProperty("priceBookId").GetGuid().ShouldBe(book.Id());
        invoice.Str("priceBookName").ShouldBe("Bayi");
        invoice.Str("customerPoNumber").ShouldBe("PO-77812");
        invoice.Dec("exciseTax").ShouldBe(12.5m);
        invoice.Dec("salesCommission").ShouldBe(150m);
        invoice.Str("invoiceDate").ShouldBe(today.DateString());
        invoice.Str("dueDate").ShouldBe(today.AddDays(30).DateString(), "vade istekten gelir, siparişten kopyalanmaz");
        invoice.TryGetProperty("pending", out _).ShouldBeFalse("pending kopyalanmaz");
        invoice.Dec("adjustment").ShouldBe(-0.56m);
        foreach (var total in new[] { "subtotal", "discountTotal", "taxTotal", "adjustment", "grandTotal" })
        {
            invoice.Dec(total).ShouldBe(orderFull.Dec(total), $"{total} siparişten birebir");
        }

        var orderLines = orderFull.GetProperty("lines").EnumerateArray().ToList();
        var invoiceLines = invoice.GetProperty("lines").EnumerateArray().ToList();
        invoiceLines.Count.ShouldBe(3);
        for (var i = 0; i < 3; i++)
        {
            invoiceLines[i].Id().ShouldNotBe(orderLines[i].Id(), "yeni kimliklerle kopyalanır");
            foreach (var field in new[] { "quantity", "unitPrice", "discountPercent", "taxRate", "lineSubtotal", "lineDiscount", "lineTax", "lineTotal" })
            {
                invoiceLines[i].Dec(field).ShouldBe(orderLines[i].Dec(field), $"lines[{i}].{field}");
            }
        }

        // Fatura siparişin durumunu değiştirmez; sipariş faturayı gösterir.
        var after = await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}");
        after.Str("status").ShouldBe("confirmed");
        after.GetProperty("invoiceId").GetGuid().ShouldBe(invoice.Id());
        after.Str("invoiceNumber").ShouldBe(invoice.Str("number"));
        var listed = (await admin.GetJsonAsync(OrdersPath)).GetProperty("items").EnumerateArray().Single(o => o.Id() == order.Id());
        listed.GetProperty("invoiceId").GetGuid().ShouldBe(invoice.Id());
        (await admin.ListIdsAsync(InvoicesPath, $"?orderId={order.Id()}")).ShouldBe([invoice.Id()]);

        (await factory.OutboxCountAsync(org.TenantId, "InvoiceCreated")).ShouldBe(1);
        var payload = JsonDocument.Parse(await factory.ScalarAsync<string>("SELECT payload::text FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE '%InvoiceCreated%'", ("t", org.TenantId))).RootElement;
        payload.GetProperty("source").GetString().ShouldBe("order");
        payload.GetProperty("orderId").GetGuid().ShouldBe(order.Id());
        payload.GetProperty("number").GetString().ShouldBe(invoice.Str("number"));
        payload.GetProperty("dealId").GetGuid().ShouldBe(deal.Id());
        payload.GetProperty("grandTotal").GetDecimal().ShouldBe(orderFull.Dec("grandTotal"));
        (await factory.CounterAsync(org.TenantId, "invoice", TenantYear())).ShouldBe(1);
    }

    [Fact]
    public async Task Convert_DefaultsDatesAndAcceptsAnEmptyOrMissingBody_AndFulfilledOrdersAreInvoiceable()
    {
        var admin = (await factory.NewOrgAsync("Fatura Dönüşüm Varsayılan")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var fulfilled = await admin.ConfirmedOrderAsync(account.Id());
        await admin.ActAsync($"{OrdersPath}/{fulfilled.Id()}/fulfill").ShouldBeNoContentAsync();
        var confirmed = await admin.ConfirmedOrderAsync(account.Id());

        var fromFulfilled = await admin.PostJsonAsync($"{OrdersPath}/{fulfilled.Id()}/invoice", null);
        var fromConfirmed = await admin.PostJsonAsync($"{OrdersPath}/{confirmed.Id()}/invoice", new { });

        fromFulfilled.Str("invoiceDate").ShouldBe(TenantToday().DateString());
        fromFulfilled.TryGetProperty("dueDate", out _).ShouldBeFalse();
        fromConfirmed.Str("invoiceDate").ShouldBe(TenantToday().DateString());
        (await admin.GetJsonAsync($"{OrdersPath}/{fulfilled.Id()}")).Str("status").ShouldBe("fulfilled");
        await (await admin.PostAsJsonAsync($"{OrdersPath}/{confirmed.Id()}/invoice", new { invoiceDate = TenantToday().DateString(), dueDate = TenantToday().AddDays(-1).DateString() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "order.already_invoiced");
    }

    [Fact]
    public async Task Convert_ValidatesTheDates_AndRequiresAConfirmedOrFulfilledOrder()
    {
        var org = await factory.NewOrgAsync("Fatura Dönüşüm Durum");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var draft = await admin.CreateOrderAsync(account.Id());
        var cancelled = await admin.CreateOrderAsync(account.Id());
        await admin.ActAsync($"{OrdersPath}/{cancelled.Id()}/cancel").ShouldBeNoContentAsync();
        var confirmed = await admin.ConfirmedOrderAsync(account.Id());

        foreach (var order in new[] { draft, cancelled })
        {
            await (await admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).ReadProblemAsync(HttpStatusCode.Conflict, "order.not_invoiceable");
        }

        await (await admin.ActAsync($"{OrdersPath}/{Guid.NewGuid()}/invoice")).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{OrdersPath}/{confirmed.Id()}/invoice", new { invoiceDate = "2026-10-10", dueDate = "2026-10-09" }, Ct)).ShouldBeValidationErrorAsync("dueDate");
        (await factory.CountAsync("invoices", org.TenantId)).ShouldBe(0);
        (await factory.CounterAsync(org.TenantId, "invoice", TenantYear())).ShouldBe(0, "başarısız dönüşüm numara tüketmez");
    }

    [Fact]
    public async Task SecondConvert_IsAlreadyInvoiced_AndTheCounterDoesNotAdvance()
    {
        var org = await factory.NewOrgAsync("Fatura Dönüşüm İkinci");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.ConfirmedOrderAsync(account.Id());
        (await admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).StatusCode.ShouldBe(HttpStatusCode.Created);

        await (await admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).ReadProblemAsync(HttpStatusCode.Conflict, "order.already_invoiced");

        (await factory.CountAsync("invoices", org.TenantId)).ShouldBe(1);
        (await factory.CounterAsync(org.TenantId, "invoice", TenantYear())).ShouldBe(1);
        (await factory.OutboxCountAsync(org.TenantId, "InvoiceCreated")).ShouldBe(1);
    }

    [Fact]
    public async Task TwoConcurrentConverts_OneWins201_OtherIs409_SingleInvoice_CounterAdvancesOnce()
    {
        var org = await factory.NewOrgAsync("Fatura Dönüşüm Yarış");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");

        for (var round = 0; round < 5; round++)
        {
            var order = await admin.ConfirmedOrderAsync(account.Id(), new { subject = $"Yarış {round}" });

            var responses = await Task.WhenAll(
                admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice"),
                admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice"));

            responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict], $"tur {round}");
            await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).ReadProblemAsync(HttpStatusCode.Conflict, "order.already_invoiced");
            (await admin.ListIdsAsync(InvoicesPath, $"?orderId={order.Id()}")).Count.ShouldBe(1, "asla iki aktif fatura");
        }

        (await factory.CountAsync("invoices", org.TenantId)).ShouldBe(5);
        (await factory.CounterAsync(org.TenantId, "invoice", TenantYear())).ShouldBe(5, "sayaç yalnız kazanan kadar ilerler: numara boşluğu yok");
        (await factory.OutboxCountAsync(org.TenantId, "InvoiceCreated")).ShouldBe(5);
        (await admin.ListNumbersAsync(InvoicesPath, "?sort=number")).ShouldBe(Enumerable.Range(1, 5).Select(n => $"INV-{TenantYear()}-{n:D4}"));
    }

    [Fact]
    public async Task PartialUniqueIndex_IsTheBackstop_ForActiveInvoicesPerOrder()
    {
        var org = await factory.NewOrgAsync("Fatura Dönüşüm İndeks");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.ConfirmedOrderAsync(account.Id());
        var invoice = await admin.PostJsonAsync($"{OrdersPath}/{order.Id()}/invoice", null);

        Task Insert(string number, string status, bool deleted) => factory.ExecuteAsync(
            "INSERT INTO commerce.invoices (id, status, created_at, tenant_id, number, subject, account_id, owner_user_id, currency, invoice_date, subtotal, discount_total, tax_total, grand_total, is_deleted, order_id) " +
            "VALUES (@id, @s, now(), @t, @n, 's', @a, @o, 'TRY', @d, 0, 0, 0, 0, @del, @ord)",
            ("id", Guid.NewGuid()), ("s", status), ("t", org.TenantId), ("n", number), ("a", account.Id()), ("o", org.AdminUserId), ("d", TenantToday()), ("del", deleted), ("ord", order.Id()));

        var act = () => Insert("INV-9999-0001", "Draft", false);
        var exception = await Should.ThrowAsync<Npgsql.PostgresException>(act);
        exception.SqlState.ShouldBe("23505");
        exception.ConstraintName.ShouldBe("ux_invoices_tenant_order");

        // İptal edilmiş veya silinmiş satırlar indekse girmez.
        await Insert("INV-9999-0002", "Cancelled", false);
        await Insert("INV-9999-0003", "Draft", true);
        (await factory.CountAsync("invoices", org.TenantId)).ShouldBe(3);
        invoice.Str("status").ShouldBe("draft");
    }

    [Fact]
    public async Task InjectedFailureWhileWritingLines_LeavesNothingBehind()
    {
        var clock = new TestClock();
        await using var derived = factory.Derive(clock);
        var org = await NewOrgAsync(derived.CreateClient, "Fatura Dönüşüm Atomik");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.ConfirmedOrderAsync(account.Id(), null, VectorLines);
        var year = TenantYear();

        Faults.Mode = Faults.FailOnInvoiceLineInsert;
        try
        {
            (await admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            Faults.Mode = null;
        }

        (await factory.CountAsync("invoices", org.TenantId)).ShouldBe(0, "fatura kalıcı değil");
        (await factory.CountAsync("invoice_lines", org.TenantId)).ShouldBe(0, "kalemler kalıcı değil");
        (await factory.CounterAsync(org.TenantId, "invoice", year)).ShouldBe(0, "sayaç geri döndü");
        (await factory.OutboxCountAsync(org.TenantId, "InvoiceCreated")).ShouldBe(0, "olay yazılmadı");
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).TryGetProperty("invoiceId", out _).ShouldBeFalse();

        var retried = await admin.PostAsync($"{OrdersPath}/{order.Id()}/invoice", null, Ct);
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await retried.Content.ReadFromJsonAsync<JsonElement>(Ct)).Str("number").ShouldBe($"INV-{year}-0001");
    }

    [Fact]
    public async Task DeletingTheDraftInvoice_OrCancellingTheInvoice_MakesTheOrderInvoiceableAgain()
    {
        var org = await factory.NewOrgAsync("Fatura Dönüşüm Yeniden");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.ConfirmedOrderAsync(account.Id());
        var first = await admin.PostJsonAsync($"{OrdersPath}/{order.Id()}/invoice", null);

        await admin.DeleteJsonAsync($"{InvoicesPath}/{first.Id()}");
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).TryGetProperty("invoiceId", out _).ShouldBeFalse("silinen fatura sipariş üzerinde görünmez");

        var second = await admin.PostJsonAsync($"{OrdersPath}/{order.Id()}/invoice", null);
        second.Str("number").ShouldBe($"INV-{TenantYear()}-0002", "silinen numara yeniden kullanılmaz");
        await admin.ActAsync($"{InvoicesPath}/{second.Id()}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{InvoicesPath}/{second.Id()}/cancel").ShouldBeNoContentAsync();

        var third = await admin.PostJsonAsync($"{OrdersPath}/{order.Id()}/invoice", null);
        third.Str("number").ShouldBe($"INV-{TenantYear()}-0003");
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).GetProperty("invoiceId").GetGuid().ShouldBe(third.Id(), "yalnız iptal edilmemiş fatura gösterilir");
        (await admin.ListIdsAsync(InvoicesPath, $"?orderId={order.Id()}")).Count.ShouldBe(2, "iptal edilen fatura kayıtta kalır");
        (await factory.OutboxCountAsync(org.TenantId, "InvoiceCancelled")).ShouldBe(1);
    }

    [Fact]
    public async Task OrderWithAnActiveInvoice_CannotBeCancelled_UntilTheInvoiceIsCancelled()
    {
        var admin = (await factory.NewOrgAsync("Fatura Dönüşüm İptal")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.ConfirmedOrderAsync(account.Id());
        var invoice = await admin.PostJsonAsync($"{OrdersPath}/{order.Id()}/invoice", null);

        await (await admin.ActAsync($"{OrdersPath}/{order.Id()}/cancel", new { reason = "x" })).ReadProblemAsync(HttpStatusCode.Conflict, "order.has_active_invoice");
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).Str("status").ShouldBe("confirmed");

        await admin.ActAsync($"{InvoicesPath}/{invoice.Id()}/cancel").ShouldBeNoContentAsync();
        await admin.ActAsync($"{OrdersPath}/{order.Id()}/cancel", new { reason = "x" }).ShouldBeNoContentAsync();
        await (await admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).ReadProblemAsync(HttpStatusCode.Conflict, "order.not_invoiceable");
    }

    [Fact]
    public async Task ConcurrentCancelAndConvert_NeverLeaveACancelledOrderWithAnActiveInvoice()
    {
        var org = await factory.NewOrgAsync("Fatura Dönüşüm İptal Yarış");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");

        for (var round = 0; round < 6; round++)
        {
            var order = await admin.ConfirmedOrderAsync(account.Id(), new { subject = $"Yarış {round}" });

            var responses = await Task.WhenAll(
                admin.ActAsync($"{OrdersPath}/{order.Id()}/invoice"),
                admin.ActAsync($"{OrdersPath}/{order.Id()}/cancel", new { reason = "yarış" }));

            responses.Count(r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent).ShouldBe(1, $"tur {round}: tam olarak biri kazanır");
            var status = (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).Str("status");
            var invoices = (await admin.ListIdsAsync(InvoicesPath, $"?orderId={order.Id()}&status=draft")).Count;
            (status, invoices).ShouldBeOneOf(("cancelled", 0), ("confirmed", 1));
        }
    }

    [Fact]
    public async Task ConvertedInvoice_KeepsTheOrderSnapshot_EvenIfTheOrderIsNoLongerTheSame()
    {
        var admin = (await factory.NewOrgAsync("Fatura Dönüşüm Anlık Görüntü")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Lisans", new { unitPrice = 100m });
        var order = await admin.ConfirmedOrderAsync(account.Id(), null, UnpricedLine(product.Id(), 2m, 0m, 20m));
        await admin.PutJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Lisans", unitPrice = 500m, taxRate = 20m });

        var invoice = await admin.PostJsonAsync($"{OrdersPath}/{order.Id()}/invoice", null);

        invoice.GetProperty("lines")[0].Dec("unitPrice").ShouldBe(100m, "fiyat siparişteki anlık görüntüden gelir, katalogdan değil");
        invoice.Dec("grandTotal").ShouldBe(240m);
    }
}
