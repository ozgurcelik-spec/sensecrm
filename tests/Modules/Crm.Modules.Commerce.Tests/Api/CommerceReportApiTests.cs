using System.Net;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Commerce.Tests.Api;

/// <summary>Ticaret özeti (<c>GET /reports/commerce/summary</c>): durum/tutar toplamları, iptaller, dönüşüm oranı, aralık kuralları, izin, izolasyon.</summary>
[Collection(ApiCollection.Name)]
public sealed class CommerceReportApiTests(CrmApiFactory factory)
{
    private const string SummaryPath = $"{Base}/reports/commerce/summary";

    private static readonly string[] QuoteStatuses = ["draft", "sent", "accepted", "rejected", "expired"];
    private static readonly string[] OrderStatuses = ["draft", "confirmed", "fulfilled", "cancelled"];

    private static (int Count, decimal Amount) Row(JsonElement group, string status)
    {
        var row = group.GetProperty("byStatus").EnumerateArray().Single(r => r.Str("status") == status);
        return (row.GetProperty("count").GetInt32(), row.Dec("amount"));
    }

    [Fact]
    public async Task Summary_WithKnownData_ReportsEveryStatusInFixedOrder_ExcludingCancelledOrdersFromTotals()
    {
        var org = await factory.NewOrgAsync("Rapor Doğruluk");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var today = TenantToday();

        async Task<Guid> Quote(decimal amount, string? state, string? subject = null)
        {
            var quote = await admin.CreateQuoteAsync(account.Id(), new { subject = subject ?? $"{state} {amount}", validUntil = today.AddDays(5).DateString() }, Line(1m, amount, 0m, 0m));
            if (state is "sent" or "accepted" or "rejected" or "expired")
            {
                await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
            }

            if (state == "accepted")
            {
                await admin.ActAsync($"{QuotesPath}/{quote.Id()}/accept").ShouldBeNoContentAsync();
            }

            if (state == "rejected")
            {
                await admin.ActAsync($"{QuotesPath}/{quote.Id()}/reject").ShouldBeNoContentAsync();
            }

            if (state == "expired")
            {
                await factory.ExecuteAsync("UPDATE commerce.quotes SET valid_until = @d WHERE id = @id", ("d", today.AddDays(-1)), ("id", quote.Id()));
            }

            return quote.Id();
        }

        await Quote(100m, "draft");
        await Quote(100m, "draft");
        await Quote(200m, "sent");
        await Quote(200m, "sent");
        await Quote(300m, "accepted");
        await Quote(300m, "accepted");
        await Quote(300m, "accepted");
        await Quote(400m, "rejected");
        await Quote(500m, "expired");
        await Quote(500m, "expired");
        await Quote(500m, "expired");

        var draftOrder = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 600m, 0m, 0m));
        var confirmedA = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 700m, 0m, 0m));
        var confirmedB = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 700m, 0m, 0m));
        var fulfilled = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 800m, 0m, 0m));
        var cancelled = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 900m, 0m, 0m));
        foreach (var order in new[] { confirmedA, confirmedB, fulfilled })
        {
            await admin.ActAsync($"{OrdersPath}/{order.Id()}/confirm").ShouldBeNoContentAsync();
        }

        await admin.ActAsync($"{OrdersPath}/{fulfilled.Id()}/fulfill").ShouldBeNoContentAsync();
        await admin.ActAsync($"{OrdersPath}/{cancelled.Id()}/cancel").ShouldBeNoContentAsync();
        _ = draftOrder;

        var report = await admin.GetJsonAsync(SummaryPath);

        report.GetProperty("currencies").EnumerateArray().Select(c => c.GetString()).ShouldBe(["TRY"]);
        var quotes = report.GetProperty("quotes");
        quotes.GetProperty("byStatus").EnumerateArray().Select(r => r.Str("status")).ShouldBe(QuoteStatuses, "tüm durumlar sabit sırayla");
        Row(quotes, "draft").ShouldBe((2, 200m));
        Row(quotes, "sent").ShouldBe((2, 400m));
        Row(quotes, "accepted").ShouldBe((3, 900m));
        Row(quotes, "rejected").ShouldBe((1, 400m));
        Row(quotes, "expired").ShouldBe((3, 1500m), "expired bugünün kiracı tarihine göre türetilir");
        quotes.GetProperty("totalCount").GetInt32().ShouldBe(11);
        quotes.Dec("totalAmount").ShouldBe(3400m);

        var orders = report.GetProperty("orders");
        orders.GetProperty("byStatus").EnumerateArray().Select(r => r.Str("status")).ShouldBe(OrderStatuses);
        Row(orders, "draft").ShouldBe((1, 600m));
        Row(orders, "confirmed").ShouldBe((2, 1400m));
        Row(orders, "fulfilled").ShouldBe((1, 800m));
        Row(orders, "cancelled").ShouldBe((1, 900m));
        orders.GetProperty("totalCount").GetInt32().ShouldBe(4, "iptaller hariç");
        orders.Dec("totalAmount").ShouldBe(2800m, "iptaller hariç");
        report.GetProperty("conversionRate").GetDecimal().ShouldBe(0.3333m, "3 kabul / 9 taslak olmayan");
    }

    [Fact]
    public async Task Summary_OrderTotals_ExcludeCancelled_AndConversionRateUsesNonDraftQuotes()
    {
        var org = await factory.NewOrgAsync("Rapor Oran");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var today = TenantToday();

        // 9 taslak olmayan teklif, 3 kabul: 3/9 = 0.3333. 2 taslak paydaya girmez.
        var ids = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            var quote = await admin.CreateQuoteAsync(account.Id(), new { validUntil = today.AddDays(9).DateString() }, Line(1m, 100m, 0m, 0m));
            await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
            ids.Add(quote.Id());
        }

        for (var i = 0; i < 3; i++)
        {
            await admin.ActAsync($"{QuotesPath}/{ids[i]}/accept").ShouldBeNoContentAsync();
        }

        await admin.ActAsync($"{QuotesPath}/{ids[3]}/reject").ShouldBeNoContentAsync();
        await factory.ExecuteAsync("UPDATE commerce.quotes SET valid_until = @d WHERE id = @id", ("d", today.AddDays(-1)), ("id", ids[4]));
        await admin.CreateQuoteAsync(account.Id());
        await admin.CreateQuoteAsync(account.Id());

        var d = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 10m, 0m, 0m));
        var c1 = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 20m, 0m, 0m));
        var c2 = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 20m, 0m, 0m));
        var f = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 30m, 0m, 0m));
        var x = await admin.CreateOrderAsync(account.Id(), null, Line(1m, 1000m, 0m, 0m));
        foreach (var order in new[] { c1, c2, f })
        {
            await admin.ActAsync($"{OrdersPath}/{order.Id()}/confirm").ShouldBeNoContentAsync();
        }

        await admin.ActAsync($"{OrdersPath}/{f.Id()}/fulfill").ShouldBeNoContentAsync();
        await admin.ActAsync($"{OrdersPath}/{x.Id()}/cancel").ShouldBeNoContentAsync();
        _ = d;

        var report = await admin.GetJsonAsync(SummaryPath);

        report.GetProperty("conversionRate").GetDecimal().ShouldBe(0.3333m);
        var orders = report.GetProperty("orders");
        Row(orders, "draft").ShouldBe((1, 10m));
        Row(orders, "confirmed").ShouldBe((2, 40m));
        Row(orders, "fulfilled").ShouldBe((1, 30m));
        Row(orders, "cancelled").ShouldBe((1, 1000m), "iptaller yalnız byStatus'ta görünür");
        orders.GetProperty("totalCount").GetInt32().ShouldBe(4);
        orders.Dec("totalAmount").ShouldBe(80m, "iptaller hariç: draft + confirmed + fulfilled");
    }

    [Fact]
    public async Task Summary_WithNoQuotes_HasZeroRows_AndNoConversionRateField()
    {
        var admin = (await factory.NewOrgAsync("Rapor Boş")).Admin;

        var report = await admin.GetJsonAsync(SummaryPath);

        report.TryGetProperty("conversionRate", out _).ShouldBeFalse("payda 0 → alan yazılmaz");
        report.GetProperty("currencies").GetArrayLength().ShouldBe(0);
        report.GetProperty("quotes").GetProperty("totalCount").GetInt32().ShouldBe(0);
        report.GetProperty("quotes").Dec("totalAmount").ShouldBe(0m);
        foreach (var status in QuoteStatuses)
        {
            Row(report.GetProperty("quotes"), status).ShouldBe((0, 0m));
        }

        foreach (var status in OrderStatuses)
        {
            Row(report.GetProperty("orders"), status).ShouldBe((0, 0m));
        }
    }

    [Fact]
    public async Task Summary_DraftsOnly_HasNoConversionRate_WhileDenominatorIsZero()
    {
        var admin = (await factory.NewOrgAsync("Rapor Yalnız Taslak")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        await admin.CreateQuoteAsync(account.Id());

        var report = await admin.GetJsonAsync(SummaryPath);

        report.TryGetProperty("conversionRate", out _).ShouldBeFalse();
        Row(report.GetProperty("quotes"), "draft").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Summary_RangeEdges_UseTheTenantTimeZone_ForQuotes_AndOrderDateForOrders()
    {
        var org = await factory.NewOrgAsync("Rapor Aralık");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        // Istanbul (UTC+3): 2026-03-31T21:30Z = 2026-04-01 00:30 yerel.
        var quote = await admin.CreateQuoteAsync(account.Id(), null, Line(1m, 100m, 0m, 0m));
        await factory.ExecuteAsync("UPDATE commerce.quotes SET created_at = @c WHERE id = @id", ("c", new DateTime(2026, 3, 31, 21, 30, 0, DateTimeKind.Utc)), ("id", quote.Id()));
        await admin.CreateOrderAsync(account.Id(), new { orderDate = "2026-04-01" }, Line(1m, 50m, 0m, 0m));
        await admin.CreateOrderAsync(account.Id(), new { orderDate = "2026-03-31" }, Line(1m, 70m, 0m, 0m));

        var april = await admin.GetJsonAsync($"{SummaryPath}?from=2026-04-01&to=2026-04-01");
        var march = await admin.GetJsonAsync($"{SummaryPath}?from=2026-03-31&to=2026-03-31");

        Row(april.GetProperty("quotes"), "draft").ShouldBe((1, 100m), "31 Mart 21:30Z yerel 1 Nisan'dır");
        Row(march.GetProperty("quotes"), "draft").ShouldBe((0, 0m));
        Row(april.GetProperty("orders"), "draft").ShouldBe((1, 50m));
        Row(march.GetProperty("orders"), "draft").ShouldBe((1, 70m), "orderDate uçları dahil");
        april.GetProperty("orders").GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Summary_DefaultsToTheLastTwelveMonths_AndValidatesTheRange()
    {
        var admin = (await factory.NewOrgAsync("Rapor Varsayılan")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var old = await admin.CreateQuoteAsync(account.Id(), null, Line(1m, 100m, 0m, 0m));
        await factory.ExecuteAsync("UPDATE commerce.quotes SET created_at = @c WHERE id = @id", ("c", DateTime.UtcNow.AddMonths(-14)), ("id", old.Id()));
        await admin.CreateQuoteAsync(account.Id(), null, Line(1m, 10m, 0m, 0m));

        var report = await admin.GetJsonAsync(SummaryPath);

        report.GetProperty("quotes").GetProperty("totalCount").GetInt32().ShouldBe(1, "14 ay önceki teklif son 12 ay dışındadır");
        await (await admin.GetAsync($"{SummaryPath}?from=2026-05-02&to=2026-05-01", Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "validation");
        await (await admin.GetAsync($"{SummaryPath}?from=2010-01-01&to=2026-05-01", Ct)).ShouldBeValidationErrorAsync("to");
        (await admin.GetAsync($"{SummaryPath}?from=2010-01-01&to=2019-12-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Summary_ListsDistinctCurrencies_Sorted()
    {
        var admin = (await factory.NewOrgAsync("Rapor Para Birimi")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        await admin.CreateQuoteAsync(account.Id(), new { currency = "USD" });
        await admin.CreateQuoteAsync(account.Id(), new { currency = "USD" });
        await admin.CreateQuoteAsync(account.Id(), new { currency = "EUR" });
        await admin.CreateOrderAsync(account.Id(), new { currency = "TRY" });

        var report = await admin.GetJsonAsync(SummaryPath);

        report.GetProperty("currencies").EnumerateArray().Select(c => c.GetString()).ShouldBe(["EUR", "TRY", "USD"]);
    }

    [Fact]
    public async Task Summary_NeedsReportsRead_AndDoesNotLeakOtherTenants()
    {
        var org = await factory.NewOrgAsync("Rapor Yetki");
        var other = await factory.NewOrgAsync("Rapor Diğer");
        var account = await org.Admin.CreateAccountAsync("Firma");
        await org.Admin.CreateQuoteAsync(account.Id(), null, Line(1m, 100m, 0m, 0m));
        await org.Admin.CreateOrderAsync(account.Id(), null, Line(1m, 100m, 0m, 0m));
        var (commerceOnly, _) = await factory.AddMemberAsync(org, "Yalnız ticaret", AllCommercePermissions);
        var (reports, _) = await factory.AddMemberAsync(org, "Rapor", "crm.reports.read");

        await (await commerceOnly.GetAsync(SummaryPath, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await reports.GetJsonAsync(SummaryPath)).GetProperty("quotes").GetProperty("totalCount").GetInt32().ShouldBe(1);

        var foreign = await other.Admin.GetJsonAsync(SummaryPath);
        foreign.GetProperty("quotes").GetProperty("totalCount").GetInt32().ShouldBe(0);
        foreign.GetProperty("orders").GetProperty("totalCount").GetInt32().ShouldBe(0);
        foreign.GetProperty("currencies").GetArrayLength().ShouldBe(0);
    }
}
