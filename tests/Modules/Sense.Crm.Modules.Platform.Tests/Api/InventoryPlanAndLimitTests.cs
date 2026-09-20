using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// M9C: <c>commerce</c> plan bayrağı ve <c>maxRecords.commerce</c> yeni varlıkları da kapsar — fatura, satın alma emri, tedarikçi, fiyat listesi ve sipariş → fatura dönüşümü
/// kaydı sayılır ve N+1. oluşturma 402 olur; kalem, girdi, tahsilat ve firma varsayılanı sayılmaz; bayrak kapalıysa okuma dahil 403, salt okunur kiracıda yazma 403.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InventoryPlanAndLimitTests(CrmApiFactory factory)
{
    private static readonly object Line = new { description = "Lisans", quantity = 1m, unitPrice = 100m, discountPercent = 0m, taxRate = 20m };

    private static Task<HttpResponseMessage> PostVendorAsync(HttpClient client) => client.PostAsJsonAsync($"{Base}/vendors", new { name = "Tedarikci " + Guid.NewGuid().ToString("N")[..4] }, Ct);

    private static Task<HttpResponseMessage> PostPriceBookAsync(HttpClient client) =>
        client.PostAsJsonAsync($"{Base}/pricebooks", new { name = "Liste " + Guid.NewGuid().ToString("N")[..6], pricingModel = "perProduct" }, Ct);

    private static Task<HttpResponseMessage> PostInvoiceAsync(HttpClient client, Guid accountId) =>
        client.PostAsJsonAsync($"{Base}/invoices", new { subject = "Fatura", accountId, lines = new[] { Line } }, Ct);

    private static Task<HttpResponseMessage> PostPurchaseOrderAsync(HttpClient client, Guid vendorId) =>
        client.PostAsJsonAsync($"{Base}/purchase-orders", new { subject = "PO", vendorId, lines = new[] { Line } }, Ct);

    [Fact]
    public async Task CommerceRecordLimit_CoversTheNewEntities_AndConversionCounts_ButChildRowsDoNot()
    {
        var tight = await factory.PlanAsync("m9c6", maxRecordsJson: """{"commerce":6}""");
        var roomy = await factory.PlanAsync("m9c7", maxRecordsJson: """{"commerce":7}""");
        using var host = factory.WithUncachedRecordCounts();
        var (org, platform) = await host.OrgOnPlanAsync("Envanter Limit", tight);
        var client = org.Admin;
        var account = await client.PostAccountAsync().CreatedIdAsync();

        var vendor = await PostVendorAsync(client).CreatedIdAsync(); // 1/6
        var book = await PostPriceBookAsync(client).CreatedIdAsync(); // 2/6
        var product = await client.PostAsJsonAsync($"{Base}/products", new { name = "Urun", unitPrice = 100m, taxRate = 20m }, Ct).CreatedIdAsync(); // 3/6
        var order = await client.PostAsJsonAsync($"{Base}/orders", new { subject = "Siparis", accountId = account, lines = new[] { Line } }, Ct).CreatedIdAsync(); // 4/6
        (await client.PostAsync($"{Base}/orders/{order}/confirm", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var invoice = await PostInvoiceAsync(client, account).CreatedIdAsync(); // 5/6

        // Kalem, girdi, tahsilat ve firma varsayılanı kayıt sayılmaz.
        (await client.PutAsJsonAsync($"{Base}/pricebooks/{book}/entries/{product}", new { unitPrice = 90m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync($"{Base}/pricebooks/accounts/{account}/default", new { priceBookId = book }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync($"{Base}/invoices/{invoice}/send", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync($"{Base}/invoices/{invoice}/payments", new { amount = 10m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var draftOrder = await client.PostAsJsonAsync($"{Base}/orders", new { subject = "Siparis 2", accountId = account, lines = new[] { Line } }, Ct).CreatedIdAsync(); // 6/6
        (await client.PostAsync($"{Base}/orders/{draftOrder}/confirm", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // N+1: her yeni oluşturma 402; sipariş → fatura dönüşümü de sayılır.
        await (await PostVendorAsync(client)).ShouldBeLimitExceededAsync("records", "commerce", max: 6, used: 6);
        await (await PostPriceBookAsync(client)).ShouldBeLimitExceededAsync("records", "commerce", max: 6, used: 6);
        await (await PostInvoiceAsync(client, account)).ShouldBeLimitExceededAsync("records", "commerce", max: 6, used: 6);
        await (await PostPurchaseOrderAsync(client, vendor)).ShouldBeLimitExceededAsync("records", "commerce", max: 6, used: 6);
        await (await client.PostAsync($"{Base}/orders/{order}/invoice", null, Ct)).ShouldBeLimitExceededAsync("records", "commerce", max: 6, used: 6);

        // Veri silinmez; mevcut kayıtlar üzerinde işlemler (girdi, tahsilat, geçiş) sürer.
        (await client.GetAsync($"{Base}/invoices/{invoice}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsJsonAsync($"{Base}/pricebooks/{book}/entries/{product}", new { unitPrice = 80m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync($"{Base}/invoices/{invoice}/payments", new { amount = 5m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Plan yükselince tüketim yeniden açılır: dönüşüm (7/7) geçer, ardından her şey yine 402.
        await platform.PutSubscriptionAsync(org.TenantId, roomy);
        (await client.PostAsync($"{Base}/orders/{order}/invoice", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await (await PostPurchaseOrderAsync(client, vendor)).ShouldBeLimitExceededAsync("records", "commerce", max: 7, used: 7);
    }

    [Fact]
    public async Task ModuleFlag_Off_BlocksEveryNewEndpoint_ReadsIncluded_AndOnReopensThem()
    {
        var off = await factory.PlanAsync("m9coff", modulesJson: AllModulesOff);
        var on = await factory.PlanAsync("m9con", modulesJson: AllModulesOn);
        using var host = factory.WithUncachedRecordCounts();
        var (org, platform) = await host.OrgOnPlanAsync("Envanter Bayrak", off);
        var client = org.Admin;
        var missing = Guid.NewGuid();

        var gets = new[]
        {
            $"{Base}/invoices", $"{Base}/invoices/{missing}", $"{Base}/purchase-orders", $"{Base}/purchase-orders/{missing}", $"{Base}/vendors", $"{Base}/vendors/{missing}",
            $"{Base}/pricebooks", $"{Base}/pricebooks/{missing}", $"{Base}/pricebooks/{missing}/entries", $"{Base}/pricebooks/accounts/{missing}/default",
        };
        foreach (var url in gets)
        {
            await (await client.GetAsync(url, Ct)).ShouldBeModuleDisabledAsync("commerce");
        }

        await (await PostVendorAsync(client)).ShouldBeModuleDisabledAsync("commerce");
        await (await PostPriceBookAsync(client)).ShouldBeModuleDisabledAsync("commerce");
        await (await PostInvoiceAsync(client, missing)).ShouldBeModuleDisabledAsync("commerce");
        await (await PostPurchaseOrderAsync(client, missing)).ShouldBeModuleDisabledAsync("commerce");
        await (await client.PostAsync($"{Base}/orders/{missing}/invoice", null, Ct)).ShouldBeModuleDisabledAsync("commerce");
        await (await client.PostAsync($"{Base}/quotes/{missing}/negotiate", null, Ct)).ShouldBeModuleDisabledAsync("commerce");
        await (await client.PostAsJsonAsync($"{Base}/pricebooks/{missing}/resolve", new { productIds = new[] { missing } }, Ct)).ShouldBeModuleDisabledAsync("commerce");
        await (await client.PostAsJsonAsync($"{Base}/invoices/{missing}/payments", new { amount = 1m }, Ct)).ShouldBeModuleDisabledAsync("commerce");

        await platform.PutSubscriptionAsync(org.TenantId, on);
        (await client.GetAsync($"{Base}/invoices", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"{Base}/vendors", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostVendorAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostPriceBookAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ReadOnlySuspension_BlocksNewWrites_KeepsReads_AndPaymentsAreWritesToo()
    {
        using var host = factory.WithUncachedRecordCounts();
        var org = await host.NewOrgAsync("Envanter Askı");
        await host.DrainOutboxesAsync();
        var platform = await host.PlatformAdminAsync();
        var client = org.Admin;
        var account = await client.PostAccountAsync().CreatedIdAsync();
        var vendor = await PostVendorAsync(client).CreatedIdAsync();
        var invoice = await PostInvoiceAsync(client, account).CreatedIdAsync();
        (await client.PostAsync($"{Base}/invoices/{invoice}/send", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await platform.SuspendAsync(org.TenantId);

        foreach (var url in new[] { $"{Base}/invoices", $"{Base}/invoices/{invoice}", $"{Base}/vendors", $"{Base}/purchase-orders", $"{Base}/pricebooks" })
        {
            (await client.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, $"okuma sürer: {url}");
        }

        await (await PostVendorAsync(client)).ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        await (await PostInvoiceAsync(client, account)).ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        await (await PostPurchaseOrderAsync(client, vendor)).ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        await (await PostPriceBookAsync(client)).ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        await (await client.PostAsJsonAsync($"{Base}/invoices/{invoice}/payments", new { amount = 1m }, Ct)).ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        await (await client.PostAsync($"{Base}/invoices/{invoice}/cancel", null, Ct)).ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
        await (await client.DeleteAsync($"{Base}/vendors/{vendor}", Ct)).ProblemAsync(HttpStatusCode.Forbidden, "tenant.suspended");
    }
}
