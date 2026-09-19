using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Modules.Identity.Infrastructure.Security;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Commerce.Tests.Api;

/// <summary>Kiracı izolasyonu (çapraz kiracı), izin eşlemesi (403), Standard rol senkronu ve izin kataloğu.</summary>
[Collection(ApiCollection.Name)]
public sealed class IsolationAndPermissionApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task CrossTenant_AllTenantEntities_AreInvisibleAndImmutable()
    {
        var a = await factory.NewOrgAsync("İzole A");
        var b = await factory.NewOrgAsync("İzole B");
        var accountA = await a.Admin.CreateAccountAsync("A firma");
        var contactA = await a.Admin.PostJsonAsync($"{Base}/contacts", new { lastName = "A kişi", accountId = accountA.Id() });
        var dealA = await a.Admin.PostJsonAsync($"{Base}/deals", new { name = "A fırsat", accountId = accountA.Id() });
        var productA = await a.Admin.CreateProductAsync("A ürünü");
        var quoteA = await a.Admin.CreateQuoteAsync(accountA.Id(), null, Line(productId: productA.Id()));
        var draftOrderA = await a.Admin.CreateOrderAsync(accountA.Id());
        var acceptedA = await a.Admin.AcceptedQuoteAsync(accountA.Id());
        var accountB = await b.Admin.CreateAccountAsync("B firma");

        // Ürün
        await (await b.Admin.GetAsync($"{ProductsPath}/{productA.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PutAsJsonAsync($"{ProductsPath}/{productA.Id()}", new { name = "x" }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.DeleteAsync($"{ProductsPath}/{productA.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Teklif: GET/PUT/DELETE + tüm geçişler + dönüşüm.
        var quoteUrl = $"{QuotesPath}/{quoteA.Id()}";
        await (await b.Admin.GetAsync(quoteUrl, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PutAsJsonAsync(quoteUrl, new { subject = "x", accountId = accountB.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.DeleteAsync(quoteUrl, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        foreach (var action in new[] { "send", "accept", "revert", "convert" })
        {
            await (await b.Admin.ActAsync($"{quoteUrl}/{action}", new { })).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        await (await b.Admin.ActAsync($"{quoteUrl}/reject", new { reason = "x" })).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.ActAsync($"{quoteUrl}/extend", new { validUntil = TenantToday().AddDays(3).DateString() })).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.ActAsync($"{QuotesPath}/{acceptedA}/convert")).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Sipariş
        var orderUrl = $"{OrdersPath}/{draftOrderA.Id()}";
        await (await b.Admin.GetAsync(orderUrl, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PutAsJsonAsync(orderUrl, new { subject = "x", accountId = accountB.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.DeleteAsync(orderUrl, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        foreach (var action in new[] { "confirm", "fulfill", "cancel" })
        {
            await (await b.Admin.ActAsync($"{orderUrl}/{action}", new { })).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        // B'nin belgesinde A'nın ürün/firma/kişi/fırsatı kullanılamaz.
        await (await b.Admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = accountB.Id(), lines = new[] { Line(productId: productA.Id()) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].productId");
        await (await b.Admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = accountB.Id(), lines = new[] { Line(productId: productA.Id()) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].productId");
        await (await b.Admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = accountA.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await b.Admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = accountB.Id(), contactId = contactA.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await b.Admin.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = accountB.Id(), dealId = dealA.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");

        // Listeler, raporlar, denetim sızdırmaz.
        (await b.Admin.ListIdsAsync(ProductsPath)).ShouldBeEmpty();
        (await b.Admin.ListIdsAsync(QuotesPath)).ShouldBeEmpty();
        (await b.Admin.ListIdsAsync(OrdersPath)).ShouldBeEmpty();
        (await b.Admin.ListIdsAsync(QuotesPath, $"?accountId={accountA.Id()}")).ShouldBeEmpty();
        (await b.Admin.GetJsonAsync($"{Base}/reports/commerce/summary")).GetProperty("quotes").GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await b.Admin.GetJsonAsync($"{Base}/audit?entityType=Quote&entityId={quoteA.Id()}")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await b.Admin.GetJsonAsync($"{Base}/audit?entityType=Product&entityId={productA.Id()}")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await b.Admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100")).GetProperty("items").EnumerateArray()
            .Select(i => i.Str("entityId")).ShouldNotContain(quoteA.Id().ToString());

        // A'nın verisi bozulmadı; sayaçlar kiracı bazlıdır.
        (await a.Admin.GetJsonAsync(quoteUrl)).Str("status").ShouldBe("draft");
        (await a.Admin.GetJsonAsync($"{QuotesPath}/{acceptedA}")).Str("status").ShouldBe("accepted");
        (await factory.CountAsync("document_counters", a.TenantId)).ShouldBeGreaterThan(0);
        (await factory.CountAsync("document_counters", b.TenantId)).ShouldBe(0, "B hiç belge açmadı: sayaç satırı yok");
        (await factory.CountAsync("quotes", b.TenantId)).ShouldBe(0);
    }

    [Fact]
    public async Task Permissions_EveryEndpoint_RequiresItsReadOrWriteKey()
    {
        var org = await factory.NewOrgAsync("Yetki Matrisi");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Ürün");
        var quote = await admin.CreateQuoteAsync(account.Id());
        var order = await admin.CreateOrderAsync(account.Id());
        var (readOnly, _) = await factory.AddMemberAsync(org, "Salt okur", "crm.products.read", "crm.quotes.read", "crm.orders.read", "crm.reports.read");
        var (writeOnly, _) = await factory.AddMemberAsync(org, "Salt yazar", "crm.products.write", "crm.quotes.write", "crm.orders.write");
        var (nothing, _) = await factory.AddMemberAsync(org, "Hiçbiri", "crm.accounts.read");
        var validQuote = new { subject = "x", accountId = account.Id(), lines = new[] { Line() } };
        var extend = new { validUntil = TenantToday().AddDays(3).DateString() };

        var reads = new[]
        {
            ProductsPath, $"{ProductsPath}/{product.Id()}", QuotesPath, $"{QuotesPath}/{quote.Id()}", OrdersPath, $"{OrdersPath}/{order.Id()}",
            $"{Base}/audit?entityType=Quote&entityId={quote.Id()}",
        };
        foreach (var url in reads)
        {
            (await readOnly.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, $"okuma izniyle GET {url}");
            await (await writeOnly.GetAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            await (await nothing.GetAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        var writes = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, ProductsPath, new { name = "Yeni" }),
            (HttpMethod.Put, $"{ProductsPath}/{product.Id()}", new { name = "Yeni" }),
            (HttpMethod.Delete, $"{ProductsPath}/{product.Id()}", null),
            (HttpMethod.Post, QuotesPath, validQuote),
            (HttpMethod.Put, $"{QuotesPath}/{quote.Id()}", validQuote),
            (HttpMethod.Delete, $"{QuotesPath}/{quote.Id()}", null),
            (HttpMethod.Post, $"{QuotesPath}/{quote.Id()}/send", new { }),
            (HttpMethod.Post, $"{QuotesPath}/{quote.Id()}/accept", new { }),
            (HttpMethod.Post, $"{QuotesPath}/{quote.Id()}/reject", new { }),
            (HttpMethod.Post, $"{QuotesPath}/{quote.Id()}/revert", new { }),
            (HttpMethod.Post, $"{QuotesPath}/{quote.Id()}/extend", extend),
            (HttpMethod.Post, $"{QuotesPath}/{quote.Id()}/convert", new { }),
            (HttpMethod.Post, OrdersPath, validQuote),
            (HttpMethod.Put, $"{OrdersPath}/{order.Id()}", validQuote),
            (HttpMethod.Delete, $"{OrdersPath}/{order.Id()}", null),
            (HttpMethod.Post, $"{OrdersPath}/{order.Id()}/confirm", new { }),
            (HttpMethod.Post, $"{OrdersPath}/{order.Id()}/fulfill", new { }),
            (HttpMethod.Post, $"{OrdersPath}/{order.Id()}/cancel", new { }),
        };
        foreach (var (method, url, body) in writes)
        {
            var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            await (await readOnly.SendAsync(request, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            var nothingRequest = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            await (await nothing.SendAsync(nothingRequest, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // Hiçbir şey değişmedi (yetkisiz istekler yazmadı).
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).Str("status").ShouldBe("draft");
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).Str("status").ShouldBe("draft");
        (await factory.CountAsync("products", org.TenantId)).ShouldBe(1);

        // Yazma izni olanlar yazabilir (durum geçişi dahil).
        (await writeOnly.ActAsync($"{QuotesPath}/{quote.Id()}/send", new { })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await writeOnly.ActAsync($"{OrdersPath}/{order.Id()}/confirm", new { })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await writeOnly.PostAsJsonAsync(QuotesPath, validQuote, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ValidationRunsBeforeAuthorization_ForEveryCreateEndpoint()
    {
        var org = await factory.NewOrgAsync("Yetki Doğrulama");
        var (readOnly, _) = await factory.AddMemberAsync(org, "Salt okur", "crm.products.read", "crm.quotes.read", "crm.orders.read");

        await (await readOnly.PostAsJsonAsync(ProductsPath, new { name = "" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await readOnly.PostAsJsonAsync(QuotesPath, new { subject = "", accountId = Guid.NewGuid() }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await readOnly.PostAsJsonAsync(OrdersPath, new { subject = "x", accountId = Guid.NewGuid(), lines = new[] { Line(0m) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].quantity");
        await (await readOnly.PostAsJsonAsync(QuotesPath, new { subject = "geçerli", accountId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task PermissionCatalog_ContainsTheSixCommerceKeys_InTheCrmGroup_AndAdminHasThem()
    {
        var org = await factory.NewOrgAsync("Yetki Katalog");

        var catalog = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => (Key: p.Str("key"), Group: p.Str("group"))).ToList();
        foreach (var key in AllCommercePermissions)
        {
            catalog.ShouldContain((key, "crm"));
        }

        var me = await org.Admin.GetJsonAsync($"{Base}/me");
        var mine = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        AllCommercePermissions.ShouldAllBe(key => mine.Contains(key), "Administrator kataloğun tümüne sahip");
    }

    [Fact]
    public async Task StandardRole_CarriesTheSixCommerceKeys_AlsoForAnOlderTenant_AfterSync()
    {
        var org = await factory.NewOrgAsync("Yetki Standart");
        var roles = await org.Admin.GetJsonAsync($"{Base}/organization/roles");
        var standard = roles.EnumerateArray().Single(r => r.GetProperty("isSystem").GetBoolean() && !r.GetProperty("permissions").EnumerateArray().Any(p => p.GetString() == "org.roles.manage"));
        var permissions = standard.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        AllCommercePermissions.ShouldAllBe(key => permissions.Contains(key), "yeni kiracının Standard rolü altı anahtarı taşır");

        // Eski kiracıyı taklit et: altı anahtarı Standard rolden çıkar, sonra API açılışındaki senkronizasyonu çalıştır.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            using var system = CurrentUserAccessor.UseSystem();
            var setter = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>();
            using (setter.BeginScope(org.TenantId))
            {
                var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                var role = db.Roles.Single(r => r.Id == standard.Id());
                role.SyncPermissionsUnchecked(role.Permissions.Where(p => !AllCommercePermissions.Contains(p)));
                await db.SaveChangesAsync(Ct);
            }
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            using var system = CurrentUserAccessor.UseSystem();
            var changed = await scope.ServiceProvider.GetRequiredService<SystemRolePermissionSynchronizer>().SyncTenantAsync(org.TenantId, Ct);
            changed.ShouldBeGreaterThan(0);
        }

        var after = (await org.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Id() == standard.Id());
        var restored = after.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        AllCommercePermissions.ShouldAllBe(key => restored.Contains(key), "senkronizasyon eski kiracıya da yeni anahtarları yayar");
        restored.ShouldNotContain("crm.approvals.decide", "Standard'ın bilinçli dışlaması korunur");
    }
}
