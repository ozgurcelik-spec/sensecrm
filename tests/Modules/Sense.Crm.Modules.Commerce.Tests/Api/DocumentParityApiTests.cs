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
/// M9C belge alan paritesi: adres blokları, nakliye, yuvarlama (adjustment), sipariş alanları, teklif "Müzakere" aşaması, teklif → sipariş kopyası ve
/// göç (adjustment = 0, diğer alanlar null olan eski satırlar). Toplamlar her zaman sunucuda hesaplanır.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DocumentParityApiTests(CrmApiFactory factory)
{
    // Belge {1,3} (M6A): Σ lineTotal 94.56.
    private static readonly object[] DocumentOneAndThree = [Line(3m, 19.99m, 10m, 20m, "Lisans"), Line(2.5m, 10.10m, 0m, 18m, "Danışmanlık")];

    private static readonly object Billing = new { street = "Atatürk Cd. 12", building = "Kat 3", city = "İstanbul", state = "İstanbul", postalCode = "34000", country = "Türkiye" };

    [Fact]
    public async Task Quote_RoundTripsAddressesCarrierAndAdjustment_AndTheServerRecomputesTotals()
    {
        var admin = (await factory.NewOrgAsync("Parite Teklif")).Admin;
        var account = await admin.CreateAccountAsync("Firma");

        var quote = await admin.CreateQuoteAsync(
            account.Id(),
            new
            {
                carrier = "  Yurtiçi Kargo ",
                adjustment = -0.56m,
                billingAddress = Billing,
                shippingAddress = new { city = "İzmir", country = "  " },
                grandTotal = 1m,
                subtotal = 2m,
            },
            DocumentOneAndThree);

        quote.Dec("adjustment").ShouldBe(-0.56m);
        quote.Dec("grandTotal").ShouldBe(94.00m, "gövdedeki sahte grandTotal yok sayılır");
        quote.Dec("subtotal").ShouldBe(85.22m);
        quote.Dec("taxTotal").ShouldBe(15.34m);
        quote.Dec("discountTotal").ShouldBe(6.00m);
        quote.Str("carrier").ShouldBe("Yurtiçi Kargo");
        quote.GetProperty("billingAddress").GetProperty("building").GetString().ShouldBe("Kat 3");
        quote.GetProperty("billingAddress").GetProperty("postalCode").GetString().ShouldBe("34000");
        var shipping = quote.GetProperty("shippingAddress");
        shipping.GetProperty("city").GetString().ShouldBe("İzmir");
        shipping.TryGetProperty("country", out _).ShouldBeFalse("boş alanlar yazılmaz");
        shipping.TryGetProperty("street", out _).ShouldBeFalse();

        var fetched = await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}");
        fetched.Dec("grandTotal").ShouldBe(94.00m);
        fetched.GetProperty("billingAddress").GetProperty("street").GetString().ShouldBe("Atatürk Cd. 12");

        // Tam değiştirme: gönderilmeyen adres bloğu/nakliye temizlenir, adjustment 0'a döner.
        await admin.PutJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "Yeni", accountId = account.Id(), lines = DocumentOneAndThree });
        var replaced = await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}");
        replaced.Dec("adjustment").ShouldBe(0m);
        replaced.Dec("grandTotal").ShouldBe(94.56m);
        replaced.TryGetProperty("billingAddress", out _).ShouldBeFalse();
        replaced.TryGetProperty("shippingAddress", out _).ShouldBeFalse();
        replaced.TryGetProperty("carrier", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AllBlankAddressBlocks_AreStoredAsNull()
    {
        var admin = (await factory.NewOrgAsync("Parite Boş Adres")).Admin;
        var account = await admin.CreateAccountAsync("Firma");

        var order = await admin.CreateOrderAsync(account.Id(), new { billingAddress = new { street = " ", city = "" }, shippingAddress = new { } });

        order.TryGetProperty("billingAddress", out _).ShouldBeFalse();
        order.TryGetProperty("shippingAddress", out _).ShouldBeFalse();
        (await factory.ScalarAsync<long>("SELECT count(*) FROM commerce.sales_orders WHERE id = @id AND billing_street IS NULL AND billing_city IS NULL AND shipping_city IS NULL", ("id", order.Id()))).ShouldBe(1);
    }

    [Theory]
    [InlineData("-94.57", "adjustment")]
    [InlineData("1000000000.01", "adjustment")]
    [InlineData("-1000000000.01", "adjustment")]
    public async Task Adjustment_Validation_ForEveryDocumentKind(string adjustment, string field)
    {
        var admin = (await factory.NewOrgAsync("Parite Adjustment")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var value = decimal.Parse(adjustment, System.Globalization.CultureInfo.InvariantCulture);

        foreach (var path in new[] { QuotesPath, OrdersPath, InvoicesPath })
        {
            var response = await admin.PostAsJsonAsync(path, new { subject = "x", accountId = account.Id(), adjustment = value, lines = DocumentOneAndThree }, Ct);
            await response.ShouldBeValidationErrorAsync(field);
        }
    }

    [Fact]
    public async Task Adjustment_ThreeDecimals_AreRejectedNotRounded_AndEmptyDocumentsCannotCarryOne()
    {
        var admin = (await factory.NewOrgAsync("Parite Adjustment 2")).Admin;
        var account = await admin.CreateAccountAsync("Firma");

        foreach (var path in new[] { QuotesPath, OrdersPath, InvoicesPath })
        {
            var decimals = await admin.PostAsJsonAsync(path, new { subject = "x", accountId = account.Id(), adjustment = 0.005m, lines = DocumentOneAndThree }, Ct);
            await decimals.ShouldBeValidationErrorAsync("adjustment");

            var empty = await admin.PostAsJsonAsync(path, new { subject = "x", accountId = account.Id(), adjustment = 0.01m }, Ct);
            await empty.ShouldBeValidationErrorAsync("adjustment");

            var emptyZero = await admin.PostAsJsonAsync(path, new { subject = "x", accountId = account.Id() }, Ct);
            emptyZero.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await emptyZero.Content.ReadFromJsonAsync<JsonElement>(Ct)).Dec("grandTotal").ShouldBe(0m);
        }

    }

    [Fact]
    public async Task RejectedAdjustment_DoesNotConsumeANumber_AndBoundaryValuesAreAccepted()
    {
        var org = await factory.NewOrgAsync("Parite Numara");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var year = TenantToday().Year;

        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), adjustment = -94.57m, lines = DocumentOneAndThree }, Ct)).ShouldBeValidationErrorAsync("adjustment");
        (await factory.CounterAsync(org.TenantId, "quote", year)).ShouldBe(0, "geçersiz belge numara tüketmez");

        var zero = await admin.CreateQuoteAsync(account.Id(), new { adjustment = -94.56m }, DocumentOneAndThree);
        zero.Dec("grandTotal").ShouldBe(0m, "A3: toplam tam sıfır geçerli");
        zero.Str("number").ShouldBe($"Q-{year}-0001");
        (await admin.CreateQuoteAsync(account.Id(), new { adjustment = 0.44m }, DocumentOneAndThree)).Dec("grandTotal").ShouldBe(95.00m);
        (await admin.CreateQuoteAsync(account.Id(), new { adjustment = -0.76m }, DocumentOneAndThree[0])).Dec("grandTotal").ShouldBe(64.00m);
    }

    [Fact]
    public async Task AddressAndCarrierLengths_AreValidatedPerField()
    {
        var admin = (await factory.NewOrgAsync("Parite Uzunluk")).Admin;
        var account = await admin.CreateAccountAsync("Firma");

        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), billingAddress = new { street = new string('a', 201) } }, Ct)).ShouldBeValidationErrorAsync("billingAddress.street");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), shippingAddress = new { building = new string('a', 101) } }, Ct)).ShouldBeValidationErrorAsync("shippingAddress.building");
        await (await admin.PostAsJsonAsync(InvoicesPath, new { subject = "x", accountId = account.Id(), billingAddress = new { country = new string('a', 101) } }, Ct)).ShouldBeValidationErrorAsync("billingAddress.country");
        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), carrier = new string('a', 65) }, Ct)).ShouldBeValidationErrorAsync("carrier");
        (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), carrier = new string('a', 64), billingAddress = new { street = new string('a', 200), city = new string('b', 100) } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddressColumns_AreAuditedPerField()
    {
        var admin = (await factory.NewOrgAsync("Parite Denetim")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quote = await admin.CreateQuoteAsync(account.Id(), new { billingAddress = new { city = "Ankara" }, carrier = "MNG" });

        await admin.PutJsonAsync(
            $"{QuotesPath}/{quote.Id()}",
            new { subject = "Yeni konu", accountId = account.Id(), billingAddress = new { city = "İzmir" }, adjustment = 0m, carrier = "MNG", lines = new[] { Line() } });

        var audit = await admin.GetJsonAsync($"{Base}/audit?entityType=Quote&entityId={quote.Id()}");
        var updated = audit.GetProperty("items").EnumerateArray().First(i => i.Str("action") == "updated").GetProperty("changes");
        updated.GetProperty("billingCity").GetProperty("old").GetString().ShouldBe("Ankara");
        updated.GetProperty("billingCity").GetProperty("new").GetString().ShouldBe("İzmir");
        updated.TryGetProperty("carrier", out _).ShouldBeFalse("değişmeyen alan fark üretmez");
    }

    // ---- Sipariş alanları ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_ExtraFields_RoundTrip_AreValidated_AndAreReplacedByPut()
    {
        var admin = (await factory.NewOrgAsync("Parite Sipariş")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var today = TenantToday();

        var order = await admin.CreateOrderAsync(
            account.Id(),
            new
            {
                orderDate = today.DateString(),
                dueDate = today.AddDays(15).DateString(),
                customerPoNumber = "PO-77812",
                exciseTax = 12.5m,
                salesCommission = 150m,
                pending = "Onay bekliyor",
                adjustment = -0.56m,
            },
            DocumentOneAndThree);

        order.Str("customerPoNumber").ShouldBe("PO-77812");
        order.Str("dueDate").ShouldBe(today.AddDays(15).DateString());
        order.Dec("exciseTax").ShouldBe(12.5m);
        order.Dec("salesCommission").ShouldBe(150m);
        order.Str("pending").ShouldBe("Onay bekliyor");
        order.Dec("grandTotal").ShouldBe(94.00m, "gider vergisi/komisyon toplama girmez");
        order.TryGetProperty("invoiceId", out _).ShouldBeFalse();

        await (await admin.PutAsJsonAsync($"{OrdersPath}/{order.Id()}", new { subject = "x", accountId = account.Id(), orderDate = today.DateString(), dueDate = today.AddDays(-1).DateString() }, Ct)).ShouldBeValidationErrorAsync("dueDate");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), exciseTax = -1m }, Ct)).ShouldBeValidationErrorAsync("exciseTax");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), salesCommission = 1.005m }, Ct)).ShouldBeValidationErrorAsync("salesCommission");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), customerPoNumber = new string('a', 65) }, Ct)).ShouldBeValidationErrorAsync("customerPoNumber");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), pending = new string('a', 101) }, Ct)).ShouldBeValidationErrorAsync("pending");

        await admin.PutJsonAsync($"{OrdersPath}/{order.Id()}", new { subject = "Yeni", accountId = account.Id(), lines = new[] { Line() } });
        var replaced = await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}");
        foreach (var field in new[] { "customerPoNumber", "dueDate", "exciseTax", "salesCommission", "pending" })
        {
            replaced.TryGetProperty(field, out _).ShouldBeFalse($"{field} tam değiştirmede temizlenir");
        }

        replaced.Str("orderDate").ShouldBe(today.DateString(), "orderDate verilmezse mevcut korunur");
        replaced.Dec("adjustment").ShouldBe(0m);
    }

    [Fact]
    public async Task Order_PriceBook_MustBeEffective_SameCurrency_AndReadable()
    {
        var org = await factory.NewOrgAsync("Parite Liste Kuralı");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var inactive = await admin.CreateInactiveFlatBookAsync("Pasif");
        var euro = await admin.CreatePriceBookAsync("Euro", "flat", new { currency = "EUR" });

        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), priceBookId = inactive.Id() }, Ct)).ShouldBeValidationErrorAsync("priceBookId");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), priceBookId = euro.Id() }, Ct)).ShouldBeValidationErrorAsync("priceBookId");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), priceBookId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), currency = "EUR", priceBookId = euro.Id() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // ---- Teklif → sipariş ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Convert_CopiesCarrierAddressesAdjustmentAndPriceBook_ButNotTheOrderSpecificFields()
    {
        var admin = (await factory.NewOrgAsync("Parite Dönüşüm")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Ürün", new { unitPrice = 100m });
        var book = await admin.CreatePriceBookAsync("Bayi", "flat", new { adjustmentPercent = -10m });
        var quoteId = await admin.AcceptedQuoteAsync(
            account.Id(),
            new { carrier = "Aras", adjustment = -0.56m, billingAddress = Billing, shippingAddress = new { city = "Bursa" }, priceBookId = book.Id() },
            UnpricedLine(product.Id(), 3m, 10m, 20m), Line(2.5m, 10.10m, 0m, 18m, "Danışmanlık"));
        var quote = await admin.GetJsonAsync($"{QuotesPath}/{quoteId}");

        var response = await admin.PostAsync($"{QuotesPath}/{quoteId}/convert", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        order.Str("carrier").ShouldBe("Aras");
        order.GetProperty("billingAddress").GetProperty("city").GetString().ShouldBe("İstanbul");
        order.GetProperty("shippingAddress").GetProperty("city").GetString().ShouldBe("Bursa");
        order.Dec("adjustment").ShouldBe(-0.56m);
        order.GetProperty("priceBookId").GetGuid().ShouldBe(book.Id());
        order.Str("priceBookName").ShouldBe("Bayi");
        order.Dec("grandTotal").ShouldBe(quote.Dec("grandTotal"), "grandTotal tekliften birebir (yuvarlama dahil)");
        foreach (var field in new[] { "customerPoNumber", "dueDate", "exciseTax", "salesCommission", "pending" })
        {
            order.TryGetProperty(field, out _).ShouldBeFalse($"{field} siparişe özgüdür, kopyalanmaz");
        }
    }

    // ---- Müzakere aşaması ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Quote_Negotiation_FullLifecycle()
    {
        var org = await factory.NewOrgAsync("Parite Müzakere");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var today = TenantToday();
        var quote = await admin.CreateQuoteAsync(account.Id(), new { validUntil = today.AddDays(5).DateString() });
        var url = $"{QuotesPath}/{quote.Id()}";

        await (await admin.ActAsync($"{url}/negotiate")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{url}/negotiate").ShouldBeNoContentAsync();
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("negotiation");
        (await admin.ListIdsAsync(QuotesPath, "?status=negotiation")).ShouldBe([quote.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?status=sent")).ShouldBeEmpty();
        var problem = await (await admin.ActAsync($"{url}/negotiate")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        problem.GetProperty("args").GetProperty("from").GetString().ShouldBe("negotiation");
        await (await admin.PutAsJsonAsync(url, new { subject = "x", accountId = account.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_editable");

        await admin.ActAsync($"{url}/extend", new { validUntil = today.AddDays(20).DateString() }).ShouldBeNoContentAsync();
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("negotiation", "extend durumu değiştirmez");
        await admin.ActAsync($"{url}/revert").ShouldBeNoContentAsync();
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("draft");

        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{url}/negotiate").ShouldBeNoContentAsync();
        await admin.ActAsync($"{url}/accept").ShouldBeNoContentAsync();
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("accepted");
        (await factory.OutboxCountAsync(org.TenantId, "QuoteAccepted")).ShouldBe(1);
        (await admin.ActAsync($"{url}/convert")).StatusCode.ShouldBe(HttpStatusCode.Created);

        var rejected = await admin.CreateQuoteAsync(account.Id());
        await admin.ActAsync($"{QuotesPath}/{rejected.Id()}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{QuotesPath}/{rejected.Id()}/negotiate").ShouldBeNoContentAsync();
        await admin.ActAsync($"{QuotesPath}/{rejected.Id()}/reject", new { reason = "pahalı" }).ShouldBeNoContentAsync();
        (await admin.GetJsonAsync($"{QuotesPath}/{rejected.Id()}")).Str("status").ShouldBe("rejected");
    }

    [Fact]
    public async Task Quote_NegotiationThatLapses_IsDerivedAsExpired_AndCannotBeAccepted()
    {
        var admin = (await factory.NewOrgAsync("Parite Müzakere Süre")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quote = await admin.CreateQuoteAsync(account.Id(), new { validUntil = TenantToday().AddDays(3).DateString() });
        var url = $"{QuotesPath}/{quote.Id()}";
        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{url}/negotiate").ShouldBeNoContentAsync();
        await factory.ExecuteAsync("UPDATE commerce.quotes SET valid_until = @d WHERE id = @id", ("d", TenantToday().AddDays(-1)), ("id", quote.Id()));

        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("expired");
        (await admin.ListIdsAsync(QuotesPath, "?status=expired")).ShouldBe([quote.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?status=negotiation")).ShouldBeEmpty();
        await (await admin.ActAsync($"{url}/accept")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.expired");
        await (await admin.ActAsync($"{url}/negotiate")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");

        var report = await admin.GetJsonAsync($"{Base}/reports/commerce/summary");
        report.GetProperty("quotes").GetProperty("byStatus").EnumerateArray().Single(r => r.Str("status") == "expired").GetProperty("count").GetInt32().ShouldBe(1);
        await admin.ActAsync($"{url}/extend", new { validUntil = TenantToday().AddDays(9).DateString() }).ShouldBeNoContentAsync();
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("negotiation");
    }

    [Fact]
    public async Task Report_NegotiationRow_AndConversionRateDenominatorIncludeNegotiation()
    {
        var admin = (await factory.NewOrgAsync("Parite Rapor Müzakere")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        async Task<Guid> Quote(string state)
        {
            var quote = await admin.CreateQuoteAsync(account.Id(), new { validUntil = TenantToday().AddDays(9).DateString() }, Line(1m, 100m, 0m, 0m));
            if (state != "draft")
            {
                await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
            }

            if (state is "negotiation" or "accepted-from-negotiation")
            {
                await admin.ActAsync($"{QuotesPath}/{quote.Id()}/negotiate").ShouldBeNoContentAsync();
            }

            if (state == "accepted-from-negotiation")
            {
                await admin.ActAsync($"{QuotesPath}/{quote.Id()}/accept").ShouldBeNoContentAsync();
            }

            return quote.Id();
        }

        await Quote("draft");
        await Quote("sent");
        await Quote("negotiation");
        await Quote("negotiation");
        await Quote("accepted-from-negotiation");

        var report = await admin.GetJsonAsync($"{Base}/reports/commerce/summary");
        var byStatus = report.GetProperty("quotes").GetProperty("byStatus").EnumerateArray().ToList();
        byStatus.Select(r => r.Str("status")).ShouldBe(["draft", "sent", "negotiation", "accepted", "rejected", "expired"]);
        byStatus.Single(r => r.Str("status") == "negotiation").GetProperty("count").GetInt32().ShouldBe(2);
        report.GetProperty("conversionRate").GetDecimal().ShouldBe(0.25m, "1 kabul / 4 taslak olmayan (sent + negotiation ×2 + accepted)");
    }

    // ---- Göç ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyRows_WithoutTheNewColumns_ReadAsAdjustmentZero_WithNullFields_AndStayUsable()
    {
        var org = await factory.NewOrgAsync("Parite Göç");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Eski firma");
        var owner = org.AdminUserId;
        var quoteId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var year = TenantToday().Year;

        // Göç öncesi şekil: yalnız M6A kolonları (adjustment, adresler, nakliye … hiç yazılmaz → DB varsayılanı/NULL).
        await factory.ExecuteAsync(
            """
            INSERT INTO commerce.quotes (id, tenant_id, number, subject, status, account_id, owner_user_id, currency, subtotal, discount_total, tax_total, grand_total, is_deleted, created_at)
            VALUES (@id, @t, @n, 'Eski teklif', 'Accepted', @acc, @owner, 'TRY', 100, 0, 20, 120, false, now())
            """,
            ("id", quoteId), ("t", org.TenantId), ("n", $"Q-{year}-9001"), ("acc", account.Id()), ("owner", owner));
        await factory.ExecuteAsync(
            """
            INSERT INTO commerce.quote_lines (id, tenant_id, quote_id, position, description, quantity, unit_price, discount_percent, tax_rate, line_subtotal, line_discount, line_tax, line_total, created_at)
            VALUES (@id, @t, @q, 0, 'Eski kalem', 1, 100, 0, 20, 100, 0, 20, 120, now())
            """,
            ("id", Guid.NewGuid()), ("t", org.TenantId), ("q", quoteId));
        await factory.ExecuteAsync(
            """
            INSERT INTO commerce.sales_orders (id, tenant_id, number, subject, status, account_id, owner_user_id, currency, order_date, subtotal, discount_total, tax_total, grand_total, is_deleted, created_at)
            VALUES (@id, @t, @n, 'Eski sipariş', 'Confirmed', @acc, @owner, 'TRY', @d, 100, 0, 20, 120, false, now())
            """,
            ("id", orderId), ("t", org.TenantId), ("n", $"SO-{year}-9001"), ("acc", account.Id()), ("owner", owner), ("d", TenantToday()));
        await factory.ExecuteAsync(
            """
            INSERT INTO commerce.sales_order_lines (id, tenant_id, order_id, position, description, quantity, unit_price, discount_percent, tax_rate, line_subtotal, line_discount, line_tax, line_total, created_at)
            VALUES (@id, @t, @o, 0, 'Eski kalem', 1, 100, 0, 20, 100, 0, 20, 120, now())
            """,
            ("id", Guid.NewGuid()), ("t", org.TenantId), ("o", orderId));

        var quote = await admin.GetJsonAsync($"{QuotesPath}/{quoteId}");
        quote.Dec("adjustment").ShouldBe(0m);
        quote.Dec("grandTotal").ShouldBe(120m, "mevcut toplamlar değişmez");
        foreach (var field in new[] { "carrier", "billingAddress", "shippingAddress", "priceBookId" })
        {
            quote.TryGetProperty(field, out _).ShouldBeFalse();
        }

        var order = await admin.GetJsonAsync($"{OrdersPath}/{orderId}");
        order.Dec("adjustment").ShouldBe(0m);
        order.Dec("grandTotal").ShouldBe(120m);
        foreach (var field in new[] { "carrier", "customerPoNumber", "dueDate", "exciseTax", "salesCommission", "pending", "invoiceId" })
        {
            order.TryGetProperty(field, out _).ShouldBeFalse();
        }

        // Eski satırlar dönüştürülebilir ve faturalanabilir; toplamlar birebir.
        var converted = await admin.PostAsync($"{QuotesPath}/{quoteId}/convert", null, Ct);
        converted.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await converted.Content.ReadFromJsonAsync<JsonElement>(Ct)).Dec("grandTotal").ShouldBe(120m);
        var invoice = await admin.PostJsonAsync($"{OrdersPath}/{orderId}/invoice", null);
        invoice.Dec("grandTotal").ShouldBe(120m);
        invoice.Dec("adjustment").ShouldBe(0m);

        // Güncellenebilir (revert → PUT ile yuvarlama).
        var legacyDraft = await admin.CreateQuoteAsync(account.Id());
        await factory.ExecuteAsync("UPDATE commerce.quotes SET adjustment = 0, carrier = NULL, billing_city = NULL WHERE id = @id", ("id", legacyDraft.Id()));
        await admin.PutJsonAsync($"{QuotesPath}/{legacyDraft.Id()}", new { subject = "Güncel", accountId = account.Id(), adjustment = -0.5m, lines = new[] { Line() } });
        (await admin.GetJsonAsync($"{QuotesPath}/{legacyDraft.Id()}")).Dec("grandTotal").ShouldBe(239.50m);
    }

    // ---- Kalem fiyatı çözümü (fiyat listesi yokken) ---------------------------------------------------------------------------

    [Fact]
    public async Task LineUnitPrice_IsOptional_ResolvedFromTheCatalog_AndAnExplicitPriceIsAnOverride()
    {
        var admin = (await factory.NewOrgAsync("Parite Kalem Fiyatı")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Lisans", new { unitPrice = 19.99m, taxRate = 20m });

        var quote = await admin.CreateQuoteAsync(account.Id(), null, UnpricedLine(product.Id(), 2m, 0m, 20m), Line(1m, 5m, 0m, 0m, "Serbest"));
        var lines = quote.GetProperty("lines").EnumerateArray().ToList();
        lines[0].Dec("unitPrice").ShouldBe(19.99m);
        lines[0].Dec("lineTotal").ShouldBe(47.98m);

        var overridden = await admin.CreateQuoteAsync(account.Id(), null, Line(1m, 12.34m, 0m, 0m, "Özel fiyat", product.Id()));
        overridden.GetProperty("lines")[0].Dec("unitPrice").ShouldBe(12.34m, "verilen fiyat esastır");

        // Anlık görüntü: katalog fiyatı sonradan değişse belge değişmez.
        await admin.PutJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Lisans", unitPrice = 99m, taxRate = 20m });
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(19.99m);
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).Dec("grandTotal").ShouldBe(quote.Dec("grandTotal"));

        var missing = await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), lines = new[] { UnpricedLine(null) } }, Ct);
        await missing.ShouldBeValidationErrorAsync("lines[0].unitPrice");
    }
}

internal static class ParityKit
{
    /// <summary>Etkin olmayan (isActive = false) flat fiyat listesi.</summary>
    public static Task<JsonElement> CreateInactiveFlatBookAsync(this HttpClient client, string name) =>
        client.CreatePriceBookAsync(name, "flat", new { isActive = false });
}
