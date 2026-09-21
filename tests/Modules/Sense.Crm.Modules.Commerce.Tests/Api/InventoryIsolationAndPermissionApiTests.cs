using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Commerce.Tests.Api;

/// <summary>M9C varlıkları için kiracı izolasyonu (çapraz kiracı), izin 403 matrisi, doğrulama-yetkiden-önce kuralı, izin kataloğu.</summary>
[Collection(ApiCollection.Name)]
public sealed class InventoryIsolationAndPermissionApiTests(CrmApiFactory factory)
{
    private static async Task ShouldBeNotFoundAsync(Task<HttpResponseMessage> pending, string code = "not_found") =>
        await (await pending).ReadProblemAsync(HttpStatusCode.NotFound, code);

    [Fact]
    public async Task CrossTenant_InvoicesPurchaseOrdersVendorsPriceBooksAndPayments_AreInvisibleAndImmutable()
    {
        var a = await factory.NewOrgAsync("Envanter İzole A");
        var b = await factory.NewOrgAsync("Envanter İzole B");
        var accountA = await a.Admin.CreateAccountAsync("A firma");
        var accountB = await b.Admin.CreateAccountAsync("B firma");
        var productA = await a.Admin.CreateProductAsync("A ürünü");
        var vendorA = await a.Admin.CreateVendorAsync("A tedarikçisi");
        var bookA = await a.Admin.CreatePriceBookAsync("A listesi");
        await a.Admin.PutJsonAsync($"{PriceBooksPath}/{bookA.Id()}/entries/{productA.Id()}", new { unitPrice = 5m });
        var draftInvoiceA = await a.Admin.CreateInvoiceAsync(accountA.Id());
        var sentInvoiceA = await a.Admin.SentInvoiceAsync(accountA.Id());
        var paymentA = await a.Admin.PayAsync(sentInvoiceA.Id(), 10m);
        var orderA = await a.Admin.ConfirmedOrderAsync(accountA.Id());
        var poA = await a.Admin.CreatePurchaseOrderAsync(vendorA.Id());
        var entryId = await factory.ScalarAsync<Guid>("SELECT id FROM commerce.price_book_entries WHERE price_book_id = @b", ("b", bookA.Id()));

        // Fatura: GET/PUT/DELETE + tüm geçişler + tahsilat.
        var invoiceUrl = $"{InvoicesPath}/{draftInvoiceA.Id()}";
        await ShouldBeNotFoundAsync(b.Admin.GetAsync(invoiceUrl, Ct));
        await ShouldBeNotFoundAsync(b.Admin.PutAsJsonAsync(invoiceUrl, new { subject = "x", accountId = accountB.Id() }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.DeleteAsync(invoiceUrl, Ct));
        foreach (var action in new[] { "send", "revert", "cancel" })
        {
            await ShouldBeNotFoundAsync(b.Admin.ActAsync($"{invoiceUrl}/{action}", new { }));
        }

        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync($"{InvoicesPath}/{sentInvoiceA.Id()}/payments", new { amount = 1m }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.DeleteAsync($"{InvoicesPath}/{sentInvoiceA.Id()}/payments/{paymentA.Id()}", Ct));
        await ShouldBeNotFoundAsync(b.Admin.ActAsync($"{OrdersPath}/{orderA.Id()}/invoice"));

        // Satın alma emri.
        var poUrl = $"{PurchaseOrdersPath}/{poA.Id()}";
        await ShouldBeNotFoundAsync(b.Admin.GetAsync(poUrl, Ct));
        await ShouldBeNotFoundAsync(b.Admin.PutAsJsonAsync(poUrl, new { subject = "x", vendorId = Guid.NewGuid() }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.DeleteAsync(poUrl, Ct));
        foreach (var action in new[] { "confirm", "receive", "cancel" })
        {
            await ShouldBeNotFoundAsync(b.Admin.ActAsync($"{poUrl}/{action}", new { }));
        }

        // Tedarikçi.
        var vendorUrl = $"{VendorsPath}/{vendorA.Id()}";
        await ShouldBeNotFoundAsync(b.Admin.GetAsync(vendorUrl, Ct));
        await ShouldBeNotFoundAsync(b.Admin.PutAsJsonAsync(vendorUrl, new { name = "x" }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.DeleteAsync(vendorUrl, Ct));

        // Fiyat listesi: başlık, girdiler, çözüm, firma varsayılanı.
        var bookUrl = $"{PriceBooksPath}/{bookA.Id()}";
        await ShouldBeNotFoundAsync(b.Admin.GetAsync(bookUrl, Ct));
        await ShouldBeNotFoundAsync(b.Admin.PutAsJsonAsync(bookUrl, new { name = "x", pricingModel = "perProduct" }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.DeleteAsync(bookUrl, Ct));
        await ShouldBeNotFoundAsync(b.Admin.GetAsync($"{bookUrl}/entries", Ct));
        await ShouldBeNotFoundAsync(b.Admin.PutAsJsonAsync($"{bookUrl}/entries/{productA.Id()}", new { unitPrice = 1m }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.DeleteAsync($"{bookUrl}/entries/{productA.Id()}", Ct));
        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync($"{bookUrl}/resolve", new { productIds = new[] { productA.Id() } }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.PutAsJsonAsync($"{PriceBooksPath}/accounts/{accountB.Id()}/default", new { priceBookId = bookA.Id() }, Ct));
        await ShouldBeNotFoundAsync(b.Admin.PutAsJsonAsync($"{PriceBooksPath}/accounts/{accountA.Id()}/default", new { priceBookId = bookA.Id() }, Ct), "commerce.related_not_found");

        // B'nin belgelerinde A'nın tedarikçisi/listesi/ürünü/firması kullanılamaz.
        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendorA.Id() }, Ct), "commerce.related_not_found");
        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = accountB.Id(), priceBookId = bookA.Id() }, Ct), "commerce.related_not_found");
        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = accountB.Id(), priceBookId = bookA.Id() }, Ct), "commerce.related_not_found");
        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = accountB.Id(), priceBookId = bookA.Id() }, Ct), "commerce.related_not_found");
        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = accountA.Id() }, Ct), "commerce.related_not_found");
        await ShouldBeNotFoundAsync(b.Admin.PostAsJsonAsync(ProductsPath, new { name = "x", vendorId = vendorA.Id() }, Ct), "commerce.related_not_found");
        await (await b.Admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = accountB.Id(), lines = new[] { Line(productId: productA.Id()) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].productId");
        await (await b.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = (await b.Admin.CreateVendorAsync("B tedarikçi")).Id(), lines = new[] { Line(productId: productA.Id()) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].productId");

        // Listeler, rapor, denetim sızdırmaz.
        foreach (var path in new[] { InvoicesPath, PurchaseOrdersPath, PriceBooksPath })
        {
            (await b.Admin.ListIdsAsync(path)).ShouldBeEmpty($"{path} B'de boş");
        }

        (await b.Admin.ListIdsAsync(VendorsPath)).ShouldHaveSingleItem("yalnız B'nin kendi tedarikçisi");
        (await b.Admin.ListIdsAsync(InvoicesPath, $"?accountId={accountA.Id()}")).ShouldBeEmpty();
        (await b.Admin.ListIdsAsync(ProductsPath, $"?vendorId={vendorA.Id()}")).ShouldBeEmpty();
        var report = await b.Admin.GetJsonAsync($"{Base}/reports/commerce/summary");
        report.GetProperty("invoices").GetProperty("totalCount").GetInt32().ShouldBe(0);
        report.GetProperty("invoices").Dec("paidAmount").ShouldBe(0m);
        report.GetProperty("purchaseOrders").GetProperty("totalCount").GetInt32().ShouldBe(0);
        foreach (var (type, id) in new[] { ("Invoice", draftInvoiceA.Id()), ("PurchaseOrder", poA.Id()), ("Vendor", vendorA.Id()), ("PriceBook", bookA.Id()), ("PriceBookEntry", entryId) })
        {
            (await b.Admin.GetJsonAsync($"{Base}/audit?entityType={type}&entityId={id}")).GetProperty("items").GetArrayLength().ShouldBe(0, $"{type} denetimi sızmaz");
            (await a.Admin.GetJsonAsync($"{Base}/audit?entityType={type}&entityId={id}")).GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0, $"{type} A için denetlenir");
        }

        // A'nın verisi bozulmadı; sayaçlar kiracı bazlıdır.
        (await a.Admin.GetJsonAsync(invoiceUrl)).Str("status").ShouldBe("draft");
        (await a.Admin.GetJsonAsync($"{InvoicesPath}/{sentInvoiceA.Id()}")).Dec("paidAmount").ShouldBe(10m);
        (await a.Admin.GetJsonAsync(poUrl)).Str("status").ShouldBe("draft");
        (await factory.CountAsync("document_counters", b.TenantId)).ShouldBe(0, "B hiç belge açmadı");
        foreach (var table in new[] { "invoices", "invoice_lines", "invoice_payments", "purchase_orders", "purchase_order_lines", "price_books", "price_book_entries", "account_price_books" })
        {
            (await factory.CountAsync(table, b.TenantId)).ShouldBe(0, $"{table} B için boş");
        }

        (await factory.CountAsync("vendors", b.TenantId)).ShouldBe(1);
    }

    [Fact]
    public async Task Permissions_EveryNewEndpoint_RequiresItsReadOrWriteKey()
    {
        var org = await factory.NewOrgAsync("Envanter Yetki");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Ürün");
        var vendor = await admin.CreateVendorAsync("Tedarikçi");
        var book = await admin.CreatePriceBookAsync("Liste");
        var invoice = await admin.CreateInvoiceAsync(account.Id());
        var sent = await admin.SentInvoiceAsync(account.Id());
        var payment = await admin.PayAsync(sent.Id(), 5m);
        var po = await admin.CreatePurchaseOrderAsync(vendor.Id());
        var order = await admin.ConfirmedOrderAsync(account.Id());
        var (readOnly, _) = await factory.AddMemberAsync(org, "Salt okur", "crm.invoices.read", "crm.pricebooks.read", "crm.vendors.read", "crm.purchaseorders.read", "crm.orders.read", "crm.reports.read");
        var (writeOnly, _) = await factory.AddMemberAsync(org, "Salt yazar", "crm.invoices.write", "crm.pricebooks.write", "crm.vendors.write", "crm.purchaseorders.write");
        var (nothing, _) = await factory.AddMemberAsync(org, "Hiçbiri", "crm.accounts.read");

        var reads = new[]
        {
            InvoicesPath, $"{InvoicesPath}/{invoice.Id()}", PurchaseOrdersPath, $"{PurchaseOrdersPath}/{po.Id()}", VendorsPath, $"{VendorsPath}/{vendor.Id()}",
            PriceBooksPath, $"{PriceBooksPath}/{book.Id()}", $"{PriceBooksPath}/{book.Id()}/entries", $"{PriceBooksPath}/accounts/{account.Id()}/default",
            $"{Base}/audit?entityType=Invoice&entityId={invoice.Id()}", $"{Base}/audit?entityType=PurchaseOrder&entityId={po.Id()}",
            $"{Base}/audit?entityType=Vendor&entityId={vendor.Id()}", $"{Base}/audit?entityType=PriceBook&entityId={book.Id()}",
            $"{Base}/audit?entityType=PriceBookEntry&entityId={Guid.NewGuid()}",
        };
        foreach (var url in reads)
        {
            (await readOnly.GetAsync(url, Ct)).IsSuccessStatusCode.ShouldBeTrue($"okuma izniyle GET {url}");
            await (await writeOnly.GetAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            await (await nothing.GetAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        await (await readOnly.PostAsJsonAsync($"{PriceBooksPath}/{book.Id()}/resolve", new { productIds = new[] { product.Id() } }, Ct)).ShouldBeSuccessAsync();
        await (await writeOnly.PostAsJsonAsync($"{PriceBooksPath}/{book.Id()}/resolve", new { productIds = new[] { product.Id() } }, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        var validInvoice = new { subject = "x", accountId = account.Id(), lines = new[] { Line() } };
        var writes = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, InvoicesPath, validInvoice),
            (HttpMethod.Put, $"{InvoicesPath}/{invoice.Id()}", validInvoice),
            (HttpMethod.Delete, $"{InvoicesPath}/{invoice.Id()}", null),
            (HttpMethod.Post, $"{InvoicesPath}/{invoice.Id()}/send", null),
            (HttpMethod.Post, $"{InvoicesPath}/{invoice.Id()}/revert", null),
            (HttpMethod.Post, $"{InvoicesPath}/{invoice.Id()}/cancel", new { }),
            (HttpMethod.Post, $"{InvoicesPath}/{sent.Id()}/payments", new { amount = 1m }),
            (HttpMethod.Delete, $"{InvoicesPath}/{sent.Id()}/payments/{payment.Id()}", null),
            (HttpMethod.Post, PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), lines = new[] { Line() } }),
            (HttpMethod.Put, $"{PurchaseOrdersPath}/{po.Id()}", new { subject = "x", vendorId = vendor.Id() }),
            (HttpMethod.Post, $"{PurchaseOrdersPath}/{po.Id()}/confirm", null),
            (HttpMethod.Post, $"{PurchaseOrdersPath}/{po.Id()}/receive", null),
            (HttpMethod.Post, $"{PurchaseOrdersPath}/{po.Id()}/cancel", new { }),
            (HttpMethod.Delete, $"{PurchaseOrdersPath}/{po.Id()}", null),
            (HttpMethod.Post, VendorsPath, new { name = "Yeni" }),
            (HttpMethod.Put, $"{VendorsPath}/{vendor.Id()}", new { name = "Yeni" }),
            (HttpMethod.Delete, $"{VendorsPath}/{vendor.Id()}", null),
            (HttpMethod.Post, PriceBooksPath, new { name = "Yeni", pricingModel = "perProduct" }),
            (HttpMethod.Put, $"{PriceBooksPath}/{book.Id()}", new { name = "Yeni", pricingModel = "perProduct" }),
            (HttpMethod.Put, $"{PriceBooksPath}/{book.Id()}/entries/{product.Id()}", new { unitPrice = 1m }),
            (HttpMethod.Delete, $"{PriceBooksPath}/{book.Id()}/entries/{product.Id()}", null),
            (HttpMethod.Put, $"{PriceBooksPath}/accounts/{account.Id()}/default", new { priceBookId = book.Id() }),
            (HttpMethod.Delete, $"{PriceBooksPath}/accounts/{account.Id()}/default", null),
            (HttpMethod.Delete, $"{PriceBooksPath}/{book.Id()}", null),
        };
        foreach (var (method, url, body) in writes)
        {
            HttpRequestMessage Request() => new(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            await (await readOnly.SendAsync(Request(), Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            await (await nothing.SendAsync(Request(), Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // Sipariş → fatura: crm.orders.read + crm.invoices.write birlikte gerekir.
        var (ordersReadOnly, _) = await factory.AddMemberAsync(org, "Sipariş okur", "crm.orders.read");
        var (invoicesWriteOnly, _) = await factory.AddMemberAsync(org, "Fatura yazar", "crm.invoices.write");
        var (both, _) = await factory.AddMemberAsync(org, "İkisi", "crm.orders.read", "crm.invoices.write");
        await (await ordersReadOnly.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await invoicesWriteOnly.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await both.ActAsync($"{OrdersPath}/{order.Id()}/invoice")).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Yazma izni olanlar yazabilir (yanıt gövdesi için okuma izni gerekmez); salt okuma yazamaz.
        (await writeOnly.PostAsJsonAsync(InvoicesPath, validInvoice, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await writeOnly.PostAsJsonAsync(VendorsPath, new { name = "Yazar tedarikçi" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await writeOnly.PostAsJsonAsync(PriceBooksPath, new { name = "Yazar liste", pricingModel = "perProduct" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await writeOnly.PostAsJsonAsync($"{InvoicesPath}/{sent.Id()}/payments", new { amount = 1m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await writeOnly.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), lines = new[] { Line() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Ticaret raporu crm.reports.read ister; yeni bölümler ekstra izin istemez.
        (await readOnly.GetAsync($"{Base}/reports/commerce/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await writeOnly.GetAsync($"{Base}/reports/commerce/summary", Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task ValidationRunsBeforeAuthorization_ForEveryNewCreateEndpoint()
    {
        var org = await factory.NewOrgAsync("Envanter Yetki Doğrulama");
        var (readOnly, _) = await factory.AddMemberAsync(org, "Salt okur", "crm.invoices.read", "crm.vendors.read", "crm.pricebooks.read", "crm.purchaseorders.read");

        await (await readOnly.PostAsJsonAsync(InvoicesPath, new { subject = "", accountId = Guid.NewGuid() }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await readOnly.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = Guid.NewGuid(), adjustment = 0.005m }, Ct)).ShouldBeValidationErrorAsync("adjustment");
        await (await readOnly.PostAsJsonAsync(VendorsPath, new { name = "" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await readOnly.PostAsJsonAsync(PriceBooksPath, new { name = "x", pricingModel = "flat" }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
        await (await readOnly.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = Guid.NewGuid(), lines = new[] { Line(0m) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].quantity");
        await (await readOnly.PostAsJsonAsync($"{InvoicesPath}/{Guid.NewGuid()}/payments", new { amount = 0m }, Ct)).ShouldBeValidationErrorAsync("amount");
        await (await readOnly.PostAsJsonAsync(InvoicesPath, new { subject = "geçerli", accountId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task PermissionCatalog_ContainsTheEightNewKeys_InTheCrmGroup_AndAdminHasThem()
    {
        var org = await factory.NewOrgAsync("Envanter Katalog");
        string[] keys =
        [
            "crm.invoices.read", "crm.invoices.write", "crm.pricebooks.read", "crm.pricebooks.write",
            "crm.vendors.read", "crm.vendors.write", "crm.purchaseorders.read", "crm.purchaseorders.write",
        ];

        var catalog = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => (Key: p.Str("key"), Group: p.Str("group"))).ToList();
        foreach (var key in keys)
        {
            catalog.ShouldContain((key, "crm"));
        }

        var mine = (await org.Admin.GetJsonAsync($"{Base}/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        keys.ShouldAllBe(key => mine.Contains(key), "Administrator kataloğun tümüne sahip");
    }
}

internal static class PermissionKit
{
    public static async Task ShouldBeSuccessAsync(this HttpResponseMessage response) =>
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(CommerceApiKit.Ct));
}
