using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Commerce.Tests.Api;

/// <summary>
/// Teklif → sipariş dönüşümü: tek transaction (numara sayacı + sipariş + kalemler + outbox olayı), çift dönüşüm engeli (benzersiz indeks),
/// hata enjeksiyonunda tam geri alma, eşzamanlı kabul/dönüşüm yarışları, olaylar.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ConversionApiTests(CrmApiFactory factory)
{
    private static int TenantYear() => TenantToday().Year;

    private static readonly object[] VectorLines =
    [
        Line(3m, 19.99m, 10m, 20m, "Lisans"),
        Line(2.5m, 10.10m, 0m, 18m, "Danışmanlık"),
        Line(1m, 0.05m, 50m, 20m, "Küçük kalem"),
    ];

    [Fact]
    public async Task Convert_CreatesADraftOrder_CopyingEverything_AndRecomputingIdenticalTotals()
    {
        var org = await factory.NewOrgAsync("Dönüşüm Ana");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.quotes.read");
        var account = await admin.CreateAccountAsync("Acme");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = account.Id() });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Fırsat", accountId = account.Id() });
        var quoteId = await admin.AcceptedQuoteAsync(
            account.Id(),
            new { contactId = contact.Id(), dealId = deal.Id(), ownerUserId = memberId, currency = "EUR", terms = "Peşin", notes = "Not", validUntil = TenantToday().AddDays(20).DateString() },
            VectorLines);
        var quote = await admin.GetJsonAsync($"{QuotesPath}/{quoteId}");
        var year = TenantYear();

        var response = await admin.PostAsJsonAsync($"{QuotesPath}/{quoteId}/convert", new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.Headers.Location!.ToString().ShouldEndWith($"{OrdersPath}/{order.Id()}");
        order.Str("number").ShouldBe($"SO-{year}-0001");
        order.Str("status").ShouldBe("draft");
        order.GetProperty("quoteId").GetGuid().ShouldBe(quoteId);
        order.Str("quoteNumber").ShouldBe(quote.Str("number"));
        order.Str("orderDate").ShouldBe(TenantToday().DateString());
        order.Str("subject").ShouldBe(quote.Str("subject"));
        order.GetProperty("accountId").GetGuid().ShouldBe(account.Id());
        order.GetProperty("contactId").GetGuid().ShouldBe(contact.Id());
        order.GetProperty("dealId").GetGuid().ShouldBe(deal.Id());
        order.GetProperty("ownerUserId").GetGuid().ShouldBe(memberId);
        order.Str("currency").ShouldBe("EUR");
        order.Str("terms").ShouldBe("Peşin");
        order.Str("notes").ShouldBe("Not");
        order.TryGetProperty("validUntil", out _).ShouldBeFalse("siparişte validUntil yok");

        foreach (var total in new[] { "subtotal", "discountTotal", "taxTotal", "grandTotal" })
        {
            order.Dec(total).ShouldBe(quote.Dec(total), $"{total} tekliften birebir");
        }

        var quoteLines = quote.GetProperty("lines").EnumerateArray().ToList();
        var orderLines = order.GetProperty("lines").EnumerateArray().ToList();
        orderLines.Count.ShouldBe(3);
        for (var i = 0; i < 3; i++)
        {
            orderLines[i].Id().ShouldNotBe(quoteLines[i].Id(), "yeni kimliklerle kopyalanır");
            orderLines[i].GetProperty("position").GetInt32().ShouldBe(i);
            foreach (var field in new[] { "quantity", "unitPrice", "discountPercent", "taxRate", "lineSubtotal", "lineDiscount", "lineTax", "lineTotal" })
            {
                orderLines[i].Dec(field).ShouldBe(quoteLines[i].Dec(field), $"lines[{i}].{field}");
            }

            orderLines[i].Str("description").ShouldBe(quoteLines[i].Str("description"));
        }

        // Teklif accepted kalır ve dönüştürüldüğü siparişi gösterir.
        var after = await admin.GetJsonAsync($"{QuotesPath}/{quoteId}");
        after.Str("status").ShouldBe("accepted");
        after.GetProperty("convertedOrderId").GetGuid().ShouldBe(order.Id());
        after.Str("convertedOrderNumber").ShouldBe(order.Str("number"));
        (await admin.ListIdsAsync(QuotesPath, "?converted=true")).ShouldBe([quoteId]);
        (await admin.ListIdsAsync(OrdersPath, $"?quoteId={quoteId}")).ShouldBe([order.Id()]);
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).Str("quoteNumber").ShouldBe(quote.Str("number"));

        // Olaylar aynı transaction'da outbox'a yazıldı.
        (await factory.OutboxCountAsync(org.TenantId, "SalesOrderCreated")).ShouldBe(1);
        (await factory.OutboxCountAsync(org.TenantId, "QuoteAccepted")).ShouldBe(1);
        var payload = await factory.ScalarAsync<string>("SELECT payload::text FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE '%SalesOrderCreated%'", ("t", org.TenantId));
        using var json = JsonDocument.Parse(payload);
        json.RootElement.GetProperty("source").GetString().ShouldBe("quote");
        json.RootElement.GetProperty("orderId").GetGuid().ShouldBe(order.Id());
        json.RootElement.GetProperty("quoteId").GetGuid().ShouldBe(quoteId);
        json.RootElement.GetProperty("number").GetString().ShouldBe(order.Str("number"));
        json.RootElement.GetProperty("grandTotal").GetDecimal().ShouldBe(quote.Dec("grandTotal"));
        json.RootElement.GetProperty("currency").GetString().ShouldBe("EUR");
        var accepted = JsonDocument.Parse(await factory.ScalarAsync<string>("SELECT payload::text FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE '%QuoteAccepted%'", ("t", org.TenantId)));
        accepted.RootElement.GetProperty("quoteId").GetGuid().ShouldBe(quoteId);
        accepted.RootElement.GetProperty("dealId").GetGuid().ShouldBe(deal.Id());
    }

    [Fact]
    public async Task Convert_RequiresAnAcceptedQuote()
    {
        var admin = (await factory.NewOrgAsync("Dönüşüm Durum")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var draft = await admin.CreateQuoteAsync(account.Id());
        var sent = await admin.CreateQuoteAsync(account.Id());
        await admin.ActAsync($"{QuotesPath}/{sent.Id()}/send").ShouldBeNoContentAsync();
        var rejected = await admin.CreateQuoteAsync(account.Id());
        await admin.ActAsync($"{QuotesPath}/{rejected.Id()}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{QuotesPath}/{rejected.Id()}/reject").ShouldBeNoContentAsync();

        foreach (var quote in new[] { draft, sent, rejected })
        {
            await (await admin.ActAsync($"{QuotesPath}/{quote.Id()}/convert")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_accepted");
        }

        await (await admin.ActAsync($"{QuotesPath}/{Guid.NewGuid()}/convert")).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.GetJsonAsync(OrdersPath)).GetProperty("totalCount").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task SecondConvert_IsAlreadyConverted_AndTheCounterDoesNotAdvance()
    {
        var org = await factory.NewOrgAsync("Dönüşüm İkinci");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quoteId = await admin.AcceptedQuoteAsync(account.Id());
        (await admin.ActAsync($"{QuotesPath}/{quoteId}/convert")).StatusCode.ShouldBe(HttpStatusCode.Created);

        await (await admin.ActAsync($"{QuotesPath}/{quoteId}/convert")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.already_converted");

        (await factory.CountAsync("sales_orders", org.TenantId)).ShouldBe(1);
        (await factory.CounterAsync(org.TenantId, "order", TenantYear())).ShouldBe(1);
        (await factory.OutboxCountAsync(org.TenantId, "SalesOrderCreated")).ShouldBe(1);
    }

    [Fact]
    public async Task TwoConcurrentConverts_OneWins201_OtherIs409_SingleOrder_CounterAdvancesOnce()
    {
        var org = await factory.NewOrgAsync("Dönüşüm Yarış");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");

        for (var round = 0; round < 5; round++)
        {
            var quoteId = await admin.AcceptedQuoteAsync(account.Id(), new { subject = $"Yarış {round}" });

            var responses = await Task.WhenAll(
                admin.ActAsync($"{QuotesPath}/{quoteId}/convert"),
                admin.ActAsync($"{QuotesPath}/{quoteId}/convert"));

            var statuses = responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
            statuses.ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict], $"tur {round}");
            var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            await loser.ReadProblemAsync(HttpStatusCode.Conflict, "quote.already_converted");
            (await admin.ListIdsAsync(OrdersPath, $"?quoteId={quoteId}")).Count.ShouldBe(1, "asla iki sipariş");
        }

        (await factory.CountAsync("sales_orders", org.TenantId)).ShouldBe(5);
        (await factory.CounterAsync(org.TenantId, "order", TenantYear())).ShouldBe(5, "sayaç yalnız kazanan kadar ilerler: numara boşluğu yok");
        (await factory.OutboxCountAsync(org.TenantId, "SalesOrderCreated")).ShouldBe(5);
        (await admin.ListNumbersAsync(OrdersPath, "?sort=number")).ShouldBe(Enumerable.Range(1, 5).Select(n => $"SO-{TenantYear()}-{n:D4}"));
    }

    [Fact]
    public async Task InjectedFailureWhileWritingLines_LeavesNothingBehind()
    {
        var clock = new TestClock();
        await using var derived = factory.Derive(clock);
        var org = await NewOrgAsync(derived.CreateClient, "Dönüşüm Atomik");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quoteId = await admin.AcceptedQuoteAsync(account.Id(), null, VectorLines);
        var year = TenantYear();

        Faults.Mode = Faults.FailOnOrderLineInsert;
        try
        {
            (await admin.ActAsync($"{QuotesPath}/{quoteId}/convert")).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            Faults.Mode = null;
        }

        (await factory.CountAsync("sales_orders", org.TenantId)).ShouldBe(0, "sipariş kalıcı değil");
        (await factory.CountAsync("sales_order_lines", org.TenantId)).ShouldBe(0, "kalemler kalıcı değil");
        (await factory.CounterAsync(org.TenantId, "order", year)).ShouldBe(0, "sayaç geri döndü");
        (await factory.OutboxCountAsync(org.TenantId, "SalesOrderCreated")).ShouldBe(0, "olay yazılmadı");
        (await admin.GetJsonAsync($"{QuotesPath}/{quoteId}")).TryGetProperty("convertedOrderId", out _).ShouldBeFalse();

        var retried = await admin.PostAsync($"{QuotesPath}/{quoteId}/convert", null, Ct);
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await retried.Content.ReadFromJsonAsync<JsonElement>(Ct)).Str("number").ShouldBe($"SO-{year}-0001");
    }

    [Fact]
    public async Task DeletingTheDraftOrder_MakesTheQuoteConvertibleAgain_ButCancelledOrdersDoNot()
    {
        var org = await factory.NewOrgAsync("Dönüşüm Yeniden");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quoteId = await admin.AcceptedQuoteAsync(account.Id());
        var first = await (await admin.ActAsync($"{QuotesPath}/{quoteId}/convert")).Content.ReadFromJsonAsync<JsonElement>(Ct);

        // Taslak sipariş düzenlenebilir ve silinebilir; silinince teklif yeniden dönüştürülebilir.
        await admin.PutJsonAsync($"{OrdersPath}/{first.Id()}", new { subject = "Düzenlendi", accountId = account.Id(), lines = new[] { Line() } });
        await admin.DeleteJsonAsync($"{OrdersPath}/{first.Id()}");
        (await admin.GetJsonAsync($"{QuotesPath}/{quoteId}")).TryGetProperty("convertedOrderId", out _).ShouldBeFalse();
        var second = await (await admin.ActAsync($"{QuotesPath}/{quoteId}/convert")).Content.ReadFromJsonAsync<JsonElement>(Ct);
        second.Str("number").ShouldBe($"SO-{TenantYear()}-0002", "silinmiş siparişin numarası yeniden kullanılmaz");
        second.GetProperty("quoteId").GetGuid().ShouldBe(quoteId);

        // İptal edilmiş sipariş silinemez ve teklifi yeniden dönüştürülemez kılar.
        await admin.ActAsync($"{OrdersPath}/{second.Id()}/cancel", new { reason = "vazgeçildi" }).ShouldBeNoContentAsync();
        await (await admin.DeleteAsync($"{OrdersPath}/{second.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "order.not_editable");
        await (await admin.ActAsync($"{QuotesPath}/{quoteId}/convert")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.already_converted");
        (await admin.GetJsonAsync($"{QuotesPath}/{quoteId}")).GetProperty("convertedOrderId").GetGuid().ShouldBe(second.Id());
    }

    [Fact]
    public async Task Convert_RevalidatesTheRelatedRecords()
    {
        var admin = (await factory.NewOrgAsync("Dönüşüm Bağlı")).Admin;
        var account = await admin.CreateAccountAsync("Silinecek firma");
        var keep = await admin.CreateAccountAsync("Kalan firma");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Geçici", accountId = keep.Id() });
        var orphanedAccount = await admin.AcceptedQuoteAsync(account.Id());
        var orphanedContact = await admin.AcceptedQuoteAsync(keep.Id(), new { contactId = contact.Id() });
        await admin.DeleteJsonAsync($"{Base}/accounts/{account.Id()}");
        await admin.DeleteJsonAsync($"{Base}/contacts/{contact.Id()}");

        await (await admin.ActAsync($"{QuotesPath}/{orphanedAccount}/convert")).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await admin.ActAsync($"{QuotesPath}/{orphanedContact}/convert")).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        (await admin.GetJsonAsync(OrdersPath)).GetProperty("totalCount").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task QuoteAccepted_IsPublishedExactlyOnce_UnderARacingDoubleAccept()
    {
        var org = await factory.NewOrgAsync("Kabul Yarış");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");

        for (var round = 0; round < 5; round++)
        {
            var quote = await admin.CreateQuoteAsync(account.Id(), new { subject = $"Kabul yarışı {round}" });
            await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();

            var responses = await Task.WhenAll(
                admin.ActAsync($"{QuotesPath}/{quote.Id()}/accept"),
                admin.ActAsync($"{QuotesPath}/{quote.Id()}/accept"));

            responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).ShouldBe(1, $"tur {round}");
            var loser = responses.Single(r => r.StatusCode != HttpStatusCode.NoContent);
            loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var code = (await loser.Content.ReadFromJsonAsync<JsonElement>(Ct)).Str("code");
            code.ShouldBeOneOf("commerce.concurrent_update", "quote.invalid_transition");
        }

        (await factory.OutboxCountAsync(org.TenantId, "QuoteAccepted")).ShouldBe(5, "kabul başına tek olay");
    }

    [Fact]
    public async Task Convert_NeedsQuotesRead_AndOrdersWrite_ButNotOrdersRead()
    {
        var org = await factory.NewOrgAsync("Dönüşüm Yetki");
        var account = await org.Admin.CreateAccountAsync("Firma");
        var (ordersWriteOnly, _) = await factory.AddMemberAsync(org, "Yalnız sipariş", "crm.orders.write");
        var (quotesReadOnly, _) = await factory.AddMemberAsync(org, "Yalnız teklif", "crm.quotes.read");
        var (both, _) = await factory.AddMemberAsync(org, "İkisi", "crm.quotes.read", "crm.orders.write");
        var quoteId = await org.Admin.AcceptedQuoteAsync(account.Id());

        await (await ordersWriteOnly.ActAsync($"{QuotesPath}/{quoteId}/convert")).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await quotesReadOnly.ActAsync($"{QuotesPath}/{quoteId}/convert")).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await factory.CountAsync("sales_orders", org.TenantId)).ShouldBe(0);

        var response = await both.ActAsync($"{QuotesPath}/{quoteId}/convert");
        response.StatusCode.ShouldBe(HttpStatusCode.Created, "sipariş detayı dönüşüm yanıtındadır; crm.orders.read gerekmez");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).Str("status").ShouldBe("draft");
        await (await both.GetAsync(OrdersPath, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }
}
