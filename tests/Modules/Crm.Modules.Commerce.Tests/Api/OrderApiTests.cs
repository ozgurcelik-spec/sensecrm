using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Commerce.Tests.Api;

/// <summary>Satış siparişleri (<c>/orders</c>): doğrudan oluşturma, CRUD, durum makinesi ve kilitler, filtre/sıralama, denetim.</summary>
[Collection(ApiCollection.Name)]
public sealed class OrderApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task DirectCreate_StartsAsDraft_WithTotals_DefaultOrderDate_AndDirectEvent()
    {
        var org = await factory.NewOrgAsync("Sipariş Doğrudan");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Acme");

        var response = await admin.PostAsJsonAsync(
            OrdersPath,
            new { subject = "  Doğrudan sipariş ", accountId = account.Id(), terms = "Şart", lines = new object[] { Line(3m, 19.99m, 10m, 20m, "Lisans"), Line(2.5m, 10.10m, 0m, 18m, "Hizmet") }, grandTotal = 1m, status = "fulfilled" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.Headers.Location!.ToString().ShouldEndWith($"{OrdersPath}/{order.Id()}");
        order.Str("status").ShouldBe("draft");
        order.Str("subject").ShouldBe("Doğrudan sipariş");
        order.Str("orderDate").ShouldBe(TenantToday().DateString());
        order.TryGetProperty("quoteId", out _).ShouldBeFalse();
        order.Dec("grandTotal").ShouldBe(94.56m);
        order.Dec("taxTotal").ShouldBe(15.34m);
        order.Str("accountName").ShouldBe("Acme");
        order.Str("ownerName").ShouldBe(org.AdminName);

        var payload = await factory.ScalarAsync<string>("SELECT payload::text FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE '%SalesOrderCreated%'", ("t", org.TenantId));
        using var json = JsonDocument.Parse(payload);
        json.RootElement.GetProperty("source").GetString().ShouldBe("direct");
        json.RootElement.TryGetProperty("quoteId", out var quoteId).ShouldBeTrue();
        quoteId.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ExplicitOrderDate_IsKept_AndUpdateKeepsItWhenOmitted()
    {
        var admin = (await factory.NewOrgAsync("Sipariş Tarih")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.CreateOrderAsync(account.Id(), new { orderDate = "2026-01-15" });
        order.Str("orderDate").ShouldBe("2026-01-15");

        await admin.PutJsonAsync($"{OrdersPath}/{order.Id()}", new { subject = "Yeni", accountId = account.Id(), lines = new[] { Line() } });
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).Str("orderDate").ShouldBe("2026-01-15");
        await admin.PutJsonAsync($"{OrdersPath}/{order.Id()}", new { subject = "Yeni", accountId = account.Id(), orderDate = "2026-02-01", lines = new[] { Line() } });
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).Str("orderDate").ShouldBe("2026-02-01");
    }

    [Fact]
    public async Task Update_ReplacesLines_KeepsNumber_AndOnlyDraftIsEditableOrDeletable()
    {
        var org = await factory.NewOrgAsync("Sipariş Düzenle");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.CreateOrderAsync(account.Id(), new { terms = "eski" });

        await admin.PutJsonAsync($"{OrdersPath}/{order.Id()}", new { subject = "Güncel", accountId = account.Id(), currency = "EUR", lines = new[] { Line(1m, 10m, 0m, 0m), Line(2m, 5m, 0m, 0m) } });

        var read = await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}");
        read.Str("number").ShouldBe(order.Str("number"));
        read.Str("subject").ShouldBe("Güncel");
        read.Str("currency").ShouldBe("EUR");
        read.Dec("grandTotal").ShouldBe(20m);
        read.TryGetProperty("terms", out _).ShouldBeFalse("PUT tam değiştirme");
        (await factory.CountAsync("sales_order_lines", org.TenantId)).ShouldBe(2);

        await admin.ActAsync($"{OrdersPath}/{order.Id()}/confirm").ShouldBeNoContentAsync();
        await (await admin.PutAsJsonAsync($"{OrdersPath}/{order.Id()}", new { subject = "x", accountId = account.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "order.not_editable");
        await (await admin.DeleteAsync($"{OrdersPath}/{order.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "order.not_editable");

        var draft = await admin.CreateOrderAsync(account.Id());
        await admin.DeleteJsonAsync($"{OrdersPath}/{draft.Id()}");
        await (await admin.GetAsync($"{OrdersPath}/{draft.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task StateMachine_Endpoints_FollowThePlanTable()
    {
        var admin = (await factory.NewOrgAsync("Sipariş Durum")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var empty = await admin.PostJsonAsync(OrdersPath, new { subject = "Boş", accountId = account.Id() });
        await (await admin.ActAsync($"{OrdersPath}/{empty.Id()}/confirm")).ReadProblemAsync(HttpStatusCode.UnprocessableEntity, "order.no_lines");

        var order = await admin.CreateOrderAsync(account.Id());
        var url = $"{OrdersPath}/{order.Id()}";
        var fulfillDraft = await (await admin.ActAsync($"{url}/fulfill")).ReadProblemAsync(HttpStatusCode.Conflict, "order.invalid_transition");
        fulfillDraft.GetProperty("args").GetProperty("from").GetString().ShouldBe("draft");
        fulfillDraft.GetProperty("args").GetProperty("to").GetString().ShouldBe("fulfilled");

        await admin.ActAsync($"{url}/confirm", new { }).ShouldBeNoContentAsync();
        (await admin.GetJsonAsync(url)).Str("status").ShouldBe("confirmed");
        await (await admin.ActAsync($"{url}/confirm")).ReadProblemAsync(HttpStatusCode.Conflict, "order.invalid_transition");

        await admin.ActAsync($"{url}/fulfill", new { }).ShouldBeNoContentAsync();
        var fulfilled = await admin.GetJsonAsync(url);
        fulfilled.Str("status").ShouldBe("fulfilled");
        fulfilled.TryGetProperty("fulfilledAt", out _).ShouldBeTrue();
        foreach (var action in new[] { "confirm", "fulfill", "cancel" })
        {
            await (await admin.ActAsync($"{url}/{action}", new { })).ReadProblemAsync(HttpStatusCode.Conflict, "order.invalid_transition");
        }

        // İptal: draft ve confirmed'dan; neden isteğe bağlı; uçtur.
        var toCancelDraft = await admin.CreateOrderAsync(account.Id());
        await admin.ActAsync($"{OrdersPath}/{toCancelDraft.Id()}/cancel", new { reason = "  Müşteri vazgeçti " }).ShouldBeNoContentAsync();
        var cancelled = await admin.GetJsonAsync($"{OrdersPath}/{toCancelDraft.Id()}");
        cancelled.Str("status").ShouldBe("cancelled");
        cancelled.Str("cancelReason").ShouldBe("Müşteri vazgeçti");
        cancelled.TryGetProperty("cancelledAt", out _).ShouldBeTrue();
        await (await admin.ActAsync($"{OrdersPath}/{toCancelDraft.Id()}/confirm")).ReadProblemAsync(HttpStatusCode.Conflict, "order.invalid_transition");

        var toCancelConfirmed = await admin.CreateOrderAsync(account.Id());
        await admin.ActAsync($"{OrdersPath}/{toCancelConfirmed.Id()}/confirm").ShouldBeNoContentAsync();
        await admin.ActAsync($"{OrdersPath}/{toCancelConfirmed.Id()}/cancel").ShouldBeNoContentAsync();
        (await admin.GetJsonAsync($"{OrdersPath}/{toCancelConfirmed.Id()}")).TryGetProperty("cancelReason", out _).ShouldBeFalse();
        await (await admin.PostAsJsonAsync($"{OrdersPath}/{toCancelConfirmed.Id()}/cancel", new { reason = new string('r', 1001) }, Ct)).ShouldBeValidationErrorAsync("reason");
    }

    [Fact]
    public async Task Validation_ReusesTheDocumentRules_WithIndexedLineKeys()
    {
        var admin = (await factory.NewOrgAsync("Sipariş Doğrulama")).Admin;
        var account = await admin.CreateAccountAsync("Firma");

        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "", accountId = account.Id() }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), lines = new object[] { Line(), new { description = "y", quantity = 0, unitPrice = 1, discountPercent = 0, taxRate = 0 } } }, Ct))
            .ShouldBeValidationErrorAsync("lines[1].quantity");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), lines = Enumerable.Repeat(Line(), 101).ToArray() }, Ct)).ShouldBeValidationErrorAsync("lines");
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        var inactive = await admin.CreateProductAsync("Pasif", new { isActive = false });
        await (await admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = account.Id(), lines = new[] { Line(productId: inactive.Id()) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].productId");
    }

    [Fact]
    public async Task List_FiltersAndSorts()
    {
        var org = await factory.NewOrgAsync("Sipariş Liste");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.orders.read");
        var acme = await admin.CreateAccountAsync("Acme");
        var beta = await admin.CreateAccountAsync("Beta");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = acme.Id() });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "F", accountId = acme.Id() });
        var jan = await admin.CreateOrderAsync(acme.Id(), new { subject = "Ocak siparişi", orderDate = "2026-01-10", contactId = contact.Id(), dealId = deal.Id() }, Line(1m, 300m, 0m, 0m));
        var feb = await admin.CreateOrderAsync(beta.Id(), new { subject = "Şubat siparişi", orderDate = "2026-02-10", ownerUserId = memberId }, Line(1m, 100m, 0m, 0m));
        var mar = await admin.CreateOrderAsync(beta.Id(), new { subject = "Mart siparişi", orderDate = "2026-03-10" }, Line(1m, 200m, 0m, 0m));
        await admin.ActAsync($"{OrdersPath}/{feb.Id()}/confirm").ShouldBeNoContentAsync();
        await admin.ActAsync($"{OrdersPath}/{mar.Id()}/cancel").ShouldBeNoContentAsync();

        async Task<List<Guid>> Ids(string query) => await admin.ListIdsAsync(OrdersPath, query);

        (await Ids("?status=draft")).ShouldBe([jan.Id()]);
        (await Ids("?status=confirmed")).ShouldBe([feb.Id()]);
        (await Ids("?status=cancelled")).ShouldBe([mar.Id()]);
        (await Ids($"?accountId={beta.Id()}")).ShouldBe([feb.Id(), mar.Id()], ignoreOrder: true);
        (await Ids($"?contactId={contact.Id()}")).ShouldBe([jan.Id()]);
        (await Ids($"?dealId={deal.Id()}")).ShouldBe([jan.Id()]);
        (await Ids($"?ownerUserId={memberId}")).ShouldBe([feb.Id()]);
        (await Ids("?orderFrom=2026-02-10&orderTo=2026-03-10")).ShouldBe([feb.Id(), mar.Id()], ignoreOrder: true);
        (await Ids("?orderFrom=2026-01-11&orderTo=2026-02-09")).ShouldBeEmpty();
        (await Ids("?q=şubat")).ShouldBe([feb.Id()]);
        (await Ids($"?q={jan.Str("number")}")).ShouldBe([jan.Id()]);
        (await Ids("?sort=orderDate")).ShouldBe([jan.Id(), feb.Id(), mar.Id()]);
        (await Ids("?sort=-orderDate")).ShouldBe([mar.Id(), feb.Id(), jan.Id()]);
        (await Ids("?sort=-grandTotal")).ShouldBe([jan.Id(), mar.Id(), feb.Id()]);
        (await Ids("?sort=number")).ShouldBe([jan.Id(), feb.Id(), mar.Id()]);
        (await Ids("?sort=subject")).ShouldBe([mar.Id(), jan.Id(), feb.Id()], "Mart < Ocak < Şubat");
        (await Ids("")).ShouldBe([mar.Id(), feb.Id(), jan.Id()], "varsayılan -createdAt");

        var summary = (await admin.GetJsonAsync($"{OrdersPath}?status=draft")).GetProperty("items")[0];
        summary.Str("accountName").ShouldBe("Acme");
        summary.Str("orderDate").ShouldBe("2026-01-10");
        summary.TryGetProperty("lines", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Audit_RecordsOrderChanges_WithCamelCaseEnums()
    {
        var org = await factory.NewOrgAsync("Sipariş Denetim");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var order = await admin.CreateOrderAsync(account.Id());
        await admin.ActAsync($"{OrdersPath}/{order.Id()}/confirm").ShouldBeNoContentAsync();
        await admin.ActAsync($"{OrdersPath}/{order.Id()}/cancel", new { reason = "x" }).ShouldBeNoContentAsync();
        var (reader, _) = await factory.AddMemberAsync(org, "Okuyucu", "crm.orders.read");
        var (quotesOnly, _) = await factory.AddMemberAsync(org, "Teklifçi", "crm.quotes.read");

        var audit = await reader.GetJsonAsync($"{Base}/audit?entityType=SalesOrder&entityId={order.Id()}");
        var items = audit.GetProperty("items").EnumerateArray().ToList();

        items.Select(i => i.Str("action")).ShouldBe(["updated", "updated", "created"]);
        items[1].GetProperty("changes").GetProperty("status").GetProperty("new").GetString().ShouldBe("confirmed");
        items[0].GetProperty("changes").GetProperty("status").GetProperty("new").GetString().ShouldBe("cancelled");
        items[0].GetProperty("changes").GetProperty("cancelReason").GetProperty("new").GetString().ShouldBe("x");
        audit.GetProperty("total").GetInt64().ShouldBe(3);
        await (await quotesOnly.GetAsync($"{Base}/audit?entityType=SalesOrder&entityId={order.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }
}
