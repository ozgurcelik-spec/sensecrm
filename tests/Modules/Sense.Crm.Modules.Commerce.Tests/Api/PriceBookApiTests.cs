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
/// Fiyat listeleri: CRUD ve benzersiz ad, model/para birimi değişmezliği, etkinlik, girdiler (limit, para birimi, audit), sunucu tarafı fiyat çözümü (<c>resolve</c> ve belge yazımı),
/// firma varsayılanı.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PriceBookApiTests(CrmApiFactory factory)
{
    private static string Url(JsonElement book) => $"{PriceBooksPath}/{book.Id()}";

    private static async Task<Dictionary<Guid, (decimal Price, string Source)>> ResolveAsync(HttpClient client, JsonElement book, params Guid[] productIds)
    {
        var result = await client.PostJsonAsync($"{Url(book)}/resolve", new { productIds }, HttpStatusCode.OK);
        return result.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("productId").GetGuid(), i => (i.Dec("unitPrice"), i.Str("source")));
    }

    // ---- CRUD ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_PerProductAndFlatBooks_ReturnTheFullShape()
    {
        var org = await factory.NewOrgAsync("Liste Oluştur");
        var admin = org.Admin;
        var today = TenantToday();

        var perProduct = await admin.CreatePriceBookAsync("Bayi Listesi", "perProduct", new { description = "  Zoho fiyat listesi ", validFrom = today.AddDays(-1).DateString(), validTo = today.AddDays(30).DateString() });
        var flat = await admin.CreatePriceBookAsync("Kampanya", "flat", new { adjustmentPercent = -12.5m, currency = "eur" });

        perProduct.Str("name").ShouldBe("Bayi Listesi");
        perProduct.Str("pricingModel").ShouldBe("perProduct");
        perProduct.Str("currency").ShouldBe("TRY");
        perProduct.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        perProduct.GetProperty("isEffective").GetBoolean().ShouldBeTrue();
        perProduct.GetProperty("entryCount").GetInt32().ShouldBe(0);
        perProduct.Str("description").ShouldBe("Zoho fiyat listesi");
        perProduct.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        perProduct.Str("ownerName").ShouldBe(org.AdminName);
        perProduct.TryGetProperty("adjustmentPercent", out _).ShouldBeFalse();
        flat.Str("pricingModel").ShouldBe("flat");
        flat.Dec("adjustmentPercent").ShouldBe(-12.5m);
        flat.Str("currency").ShouldBe("EUR");
        (await admin.GetJsonAsync(Url(flat))).Str("name").ShouldBe("Kampanya");
    }

    [Fact]
    public async Task Validation_ModelPercentDatesAndCurrency()
    {
        var admin = (await factory.NewOrgAsync("Liste Doğrulama")).Admin;
        var today = TenantToday();

        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "", pricingModel = "flat", adjustmentPercent = 1m }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Modelsiz" }, Ct)).ShouldBeValidationErrorAsync("pricingModel");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Yüzdesiz", pricingModel = "flat" }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Yasak yüzde", pricingModel = "perProduct", adjustmentPercent = 5m }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Alt sınır", pricingModel = "flat", adjustmentPercent = -100m }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Üst sınır", pricingModel = "flat", adjustmentPercent = 1000.01m }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Ondalık", pricingModel = "flat", adjustmentPercent = 1.005m }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Tarih", pricingModel = "perProduct", validFrom = today.DateString(), validTo = today.AddDays(-1).DateString() }, Ct)).ShouldBeValidationErrorAsync("validTo");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Para", pricingModel = "perProduct", currency = "XX9" }, Ct)).ShouldBeValidationErrorAsync("currency");
        (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Model", pricingModel = "tiered" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = new string('a', 201), pricingModel = "perProduct" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Sahip", pricingModel = "perProduct", ownerUserId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Sınırlar", pricingModel = "flat", adjustmentPercent = -99.99m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync(PriceBooksPath, new { name = "Sınırlar 2", pricingModel = "flat", adjustmentPercent = 1000m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.ListIdsAsync(PriceBooksPath)).Count.ShouldBe(2, "geçersiz istekler kayıt bırakmaz");
    }

    [Fact]
    public async Task Name_IsUniquePerTenant_CaseInsensitively_AmongNonDeleted()
    {
        var a = await factory.NewOrgAsync("Liste Ad A");
        var b = await factory.NewOrgAsync("Liste Ad B");
        var first = await a.Admin.CreatePriceBookAsync("Bayi Listesi");

        await (await a.Admin.PostAsJsonAsync(PriceBooksPath, new { name = "bayi listesi", pricingModel = "perProduct" }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.name_taken");
        await (await a.Admin.PostAsJsonAsync(PriceBooksPath, new { name = "  BAYI LISTESI  ", pricingModel = "perProduct" }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.name_taken");
        await (await a.Admin.PostAsJsonAsync(PriceBooksPath, new { name = "Bayi Listesi", pricingModel = "flat", adjustmentPercent = 1m }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.name_taken");
        (await b.Admin.PostAsJsonAsync(PriceBooksPath, new { name = "Bayi Listesi", pricingModel = "perProduct" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Güncellemede kendi adı serbest, başkasınınki çakışır.
        var other = await a.Admin.CreatePriceBookAsync("Diğer");
        await a.Admin.PutJsonAsync(Url(other), new { name = "Diğer", pricingModel = "perProduct" });
        await (await a.Admin.PutAsJsonAsync(Url(other), new { name = "BAYI LISTESI", pricingModel = "perProduct" }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.name_taken");

        // Silinen listenin adı yeniden kullanılabilir.
        await a.Admin.DeleteJsonAsync(Url(first));
        (await a.Admin.PostAsJsonAsync(PriceBooksPath, new { name = "BAYI listesi", pricingModel = "perProduct" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Update_ModelAndCurrencyAreImmutable_OtherFieldsReplace_IsActiveIsKeptWhenOmitted()
    {
        var admin = (await factory.NewOrgAsync("Liste Güncelle")).Admin;
        var book = await admin.CreatePriceBookAsync("Kampanya", "flat", new { adjustmentPercent = -10m, description = "eski" });

        var model = await (await admin.PutAsJsonAsync(Url(book), new { name = "Kampanya", pricingModel = "perProduct" }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.model_immutable");
        model.GetProperty("args").GetProperty("property").GetString().ShouldBe("pricingModel");
        var currency = await (await admin.PutAsJsonAsync(Url(book), new { name = "Kampanya", pricingModel = "flat", adjustmentPercent = -10m, currency = "USD" }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.model_immutable");
        currency.GetProperty("args").GetProperty("property").GetString().ShouldBe("currency");

        // Model/para birimi gönderilmezse değişmemiş sayılır; yüzde yine zorunludur.
        await (await admin.PutAsJsonAsync(Url(book), new { name = "Kampanya" }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
        await admin.PutJsonAsync(Url(book), new { name = "Kampanya 2", adjustmentPercent = 15m, validTo = TenantToday().AddDays(9).DateString() });
        var updated = await admin.GetJsonAsync(Url(book));
        updated.Str("name").ShouldBe("Kampanya 2");
        updated.Dec("adjustmentPercent").ShouldBe(15m);
        updated.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        updated.TryGetProperty("description", out _).ShouldBeFalse("tam değiştirme: gönderilmeyen alan temizlenir");
        updated.Str("pricingModel").ShouldBe("flat");
        updated.Str("currency").ShouldBe("TRY");

        await admin.PutJsonAsync(Url(book), new { name = "Kampanya 2", pricingModel = "flat", adjustmentPercent = 15m, currency = "try", isActive = false });
        (await admin.GetJsonAsync(Url(book))).GetProperty("isActive").GetBoolean().ShouldBeFalse();
        await (await admin.PutAsJsonAsync($"{PriceBooksPath}/{Guid.NewGuid()}", new { name = "x", adjustmentPercent = 1m }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");

        var perProduct = await admin.CreatePriceBookAsync("Girdili");
        await (await admin.PutAsJsonAsync(Url(perProduct), new { name = "Girdili", pricingModel = "flat", adjustmentPercent = 5m }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.model_immutable");
        await (await admin.PutAsJsonAsync(Url(perProduct), new { name = "Girdili", adjustmentPercent = 5m }, Ct)).ShouldBeValidationErrorAsync("adjustmentPercent");
    }

    [Fact]
    public async Task Effectiveness_IsDerived_FromActiveFlagAndDateRange_AndFiltersTheList()
    {
        var admin = (await factory.NewOrgAsync("Liste Etkinlik")).Admin;
        var today = TenantToday();
        var always = await admin.CreatePriceBookAsync("Her zaman");
        var inactive = await admin.CreatePriceBookAsync("Pasif", "perProduct", new { isActive = false });
        var future = await admin.CreatePriceBookAsync("Gelecek", "perProduct", new { validFrom = today.AddDays(1).DateString() });
        var expired = await admin.CreatePriceBookAsync("Süresi doldu", "perProduct", new { validTo = today.AddDays(-1).DateString() });
        var endsToday = await admin.CreatePriceBookAsync("Bugün biter", "perProduct", new { validTo = today.DateString() });
        var euro = await admin.CreatePriceBookAsync("Euro", "flat", new { currency = "EUR" });

        always.GetProperty("isEffective").GetBoolean().ShouldBeTrue();
        inactive.GetProperty("isEffective").GetBoolean().ShouldBeFalse();
        future.GetProperty("isEffective").GetBoolean().ShouldBeFalse();
        expired.GetProperty("isEffective").GetBoolean().ShouldBeFalse();
        endsToday.GetProperty("isEffective").GetBoolean().ShouldBeTrue("son gün dahil");

        (await admin.ListIdsAsync(PriceBooksPath, "?effective=true")).ShouldBe(new[] { always.Id(), endsToday.Id(), euro.Id() }, ignoreOrder: true);
        (await admin.ListIdsAsync(PriceBooksPath, "?effective=false")).ShouldBe(new[] { inactive.Id(), future.Id(), expired.Id() }, ignoreOrder: true);
        (await admin.ListIdsAsync(PriceBooksPath, "?effective=true&currency=eur")).ShouldBe([euro.Id()]);
        (await admin.ListIdsAsync(PriceBooksPath, "?isActive=false")).ShouldBe([inactive.Id()]);
        (await admin.ListIdsAsync(PriceBooksPath, $"?ownerUserId={Guid.NewGuid()}")).ShouldBeEmpty();
        (await admin.ListIdsAsync(PriceBooksPath, "?sort=-validTo")).Take(2).ShouldBe([endsToday.Id(), expired.Id()], "boş validTo her yönde sonda");
        (await admin.ListIdsAsync(PriceBooksPath, "?sort=validTo")).Take(2).ShouldBe([expired.Id(), endsToday.Id()]);
        (await admin.ListIdsAsync(PriceBooksPath, "?q=süresi")).ShouldBe([expired.Id()]);
        (await admin.ListIdsAsync(PriceBooksPath, "?q=%25")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_IsSoft_RemovesEntriesAndAccountDefaults_AndDocumentsKeepTheReference()
    {
        var org = await factory.NewOrgAsync("Liste Sil");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Ürün");
        var book = await admin.CreatePriceBookAsync("Silinecek");
        await admin.PutJsonAsync($"{Url(book)}/entries/{product.Id()}", new { unitPrice = 80m });
        await admin.PutJsonAsync($"{PriceBooksPath}/accounts/{account.Id()}/default", new { priceBookId = book.Id() });
        var quote = await admin.CreateQuoteAsync(account.Id(), new { priceBookId = book.Id() });
        quote.Str("priceBookName").ShouldBe("Silinecek");

        await admin.DeleteJsonAsync(Url(book));

        await (await admin.GetAsync(Url(book), Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM commerce.price_book_entries WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(0, "girdiler aynı transaction'da silinir");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM commerce.account_price_books WHERE tenant_id = @t", ("t", org.TenantId))).ShouldBe(0, "firma varsayılanı silinir");
        (await admin.GetAsync($"{PriceBooksPath}/accounts/{account.Id()}/default", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var kept = await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}");
        kept.GetProperty("priceBookId").GetGuid().ShouldBe(book.Id(), "belge priceBookId'yi korur");
        kept.TryGetProperty("priceBookName", out _).ShouldBeFalse("ad boş döner");
        (await factory.ScalarAsync<bool>("SELECT is_deleted FROM commerce.price_books WHERE id = @i", ("i", book.Id()))).ShouldBeTrue();
        await (await admin.DeleteAsync(Url(book), Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Silinmiş listeyle belge güncellenirken bağ değişmediği sürece etkilenmez.
        await admin.PutJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "Güncel", accountId = account.Id(), priceBookId = book.Id(), lines = new[] { Line() } });
    }

    // ---- Girdiler ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Entries_UpsertIsIdempotent_ListsWithCatalogPrice_AndDeleteIsIdempotent()
    {
        var admin = (await factory.NewOrgAsync("Liste Girdi")).Admin;
        var a = await admin.CreateProductAsync("Alfa ürün", new { code = "A-1", unitPrice = 19.99m });
        var b = await admin.CreateProductAsync("Beta ürün", new { code = "B-1", unitPrice = 50m });
        var book = await admin.CreatePriceBookAsync("Bayi");
        var before = (await admin.GetJsonAsync(Url(book))).TryGetProperty("updatedAt", out _);

        await admin.PutJsonAsync($"{Url(book)}/entries/{a.Id()}", new { unitPrice = 12.5m });
        await admin.PutJsonAsync($"{Url(book)}/entries/{a.Id()}", new { unitPrice = 12.5m });
        await admin.PutJsonAsync($"{Url(book)}/entries/{b.Id()}", new { unitPrice = 40.1234m });

        before.ShouldBeFalse();
        var detail = await admin.GetJsonAsync(Url(book));
        detail.GetProperty("entryCount").GetInt32().ShouldBe(2);
        detail.TryGetProperty("updatedAt", out _).ShouldBeTrue("girdi yazımı başlığın updatedAt'ini ilerletir");
        var entries = await admin.GetJsonAsync($"{Url(book)}/entries");
        entries.GetProperty("totalCount").GetInt32().ShouldBe(2);
        var items = entries.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.Str("productName")).ShouldBe(["Alfa ürün", "Beta ürün"]);
        (items[0].Dec("unitPrice"), items[0].Dec("catalogPrice"), items[0].Str("productCode")).ShouldBe((12.5m, 19.99m, "A-1"));
        items[1].Dec("unitPrice").ShouldBe(40.1234m);
        items[0].TryGetProperty("updatedAt", out _).ShouldBeTrue();

        (await admin.GetJsonAsync($"{Url(book)}/entries?q=b-1")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await admin.GetJsonAsync($"{Url(book)}/entries?q=%25")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await admin.GetJsonAsync($"{Url(book)}/entries?sort=-unitPrice")).GetProperty("items")[0].Str("productName").ShouldBe("Beta ürün");
        (await admin.GetJsonAsync($"{Url(book)}/entries?pageSize=1&page=2&sort=productName")).GetProperty("items")[0].Str("productName").ShouldBe("Beta ürün");

        await admin.DeleteJsonAsync($"{Url(book)}/entries/{a.Id()}");
        await admin.DeleteJsonAsync($"{Url(book)}/entries/{a.Id()}");
        await admin.DeleteJsonAsync($"{Url(book)}/entries/{Guid.NewGuid()}");
        (await admin.GetJsonAsync(Url(book))).GetProperty("entryCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Entries_Validation_CurrencyForeignProductsAndModel()
    {
        var a = await factory.NewOrgAsync("Liste Girdi Kural A");
        var b = await factory.NewOrgAsync("Liste Girdi Kural B");
        var tryProduct = await a.Admin.CreateProductAsync("TL ürün");
        var usdProduct = await a.Admin.CreateProductAsync("USD ürün", new { currency = "USD" });
        var inactive = await a.Admin.CreateProductAsync("Pasif ürün", new { isActive = false });
        var foreign = await b.Admin.CreateProductAsync("Yabancı ürün");
        var book = await a.Admin.CreatePriceBookAsync("TL liste");
        var flat = await a.Admin.CreatePriceBookAsync("Yüzde", "flat");

        await (await a.Admin.PutAsJsonAsync($"{Url(book)}/entries/{usdProduct.Id()}", new { unitPrice = 1m }, Ct)).ShouldBeValidationErrorAsync("productId");
        (await a.Admin.PutAsJsonAsync($"{Url(book)}/entries/{inactive.Id()}", new { unitPrice = 1m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await a.Admin.PutAsJsonAsync($"{Url(book)}/entries/{foreign.Id()}", new { unitPrice = 1m }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PutAsJsonAsync($"{Url(book)}/entries/{Guid.NewGuid()}", new { unitPrice = 1m }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PutAsJsonAsync($"{Url(book)}/entries/{tryProduct.Id()}", new { unitPrice = -1m }, Ct)).ShouldBeValidationErrorAsync("unitPrice");
        await (await a.Admin.PutAsJsonAsync($"{Url(book)}/entries/{tryProduct.Id()}", new { unitPrice = 1_000_000_001m }, Ct)).ShouldBeValidationErrorAsync("unitPrice");
        await (await a.Admin.PutAsJsonAsync($"{Url(book)}/entries/{tryProduct.Id()}", new { unitPrice = 1.00001m }, Ct)).ShouldBeValidationErrorAsync("unitPrice");
        await (await a.Admin.PutAsJsonAsync($"{PriceBooksPath}/{Guid.NewGuid()}/entries/{tryProduct.Id()}", new { unitPrice = 1m }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PutAsJsonAsync($"{Url(book)}/entries/{foreign.Id()}", new { unitPrice = 1m }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");

        foreach (var request in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            var message = new HttpRequestMessage(request, $"{Url(flat)}/entries{(request == HttpMethod.Get ? string.Empty : $"/{tryProduct.Id()}")}");
            if (request == HttpMethod.Put)
            {
                message.Content = JsonContent.Create(new { unitPrice = 1m });
            }

            await (await a.Admin.SendAsync(message, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.model_mismatch");
        }
    }

    [Fact]
    public async Task Entries_AreLimitedTo2000PerBook_ButExistingOnesStayEditable()
    {
        var org = await factory.NewOrgAsync("Liste Girdi Limit");
        var admin = org.Admin;
        var book = await admin.CreatePriceBookAsync("Dolu liste");
        var first = await admin.CreateProductAsync("İlk");
        var extra = await admin.CreateProductAsync("Fazla");
        await admin.PutJsonAsync($"{Url(book)}/entries/{first.Id()}", new { unitPrice = 5m });

        // 1999 sahte girdi (aynı ürün kimliğine değil, farklı kimliklere) doğrudan eklenir → toplam 2000.
        await factory.ExecuteAsync(
            """
            INSERT INTO commerce.price_book_entries (id, tenant_id, price_book_id, product_id, unit_price, created_at)
            SELECT gen_random_uuid(), @t, @b, gen_random_uuid(), 1, now() FROM generate_series(1, 1999)
            """,
            ("t", org.TenantId), ("b", book.Id()));
        (await admin.GetJsonAsync(Url(book))).GetProperty("entryCount").GetInt32().ShouldBe(2000);

        var problem = await (await admin.PutAsJsonAsync($"{Url(book)}/entries/{extra.Id()}", new { unitPrice = 9m }, Ct)).ReadProblemAsync(HttpStatusCode.UnprocessableEntity, "pricebook.entry_limit");
        problem.GetProperty("args").GetProperty("max").GetInt32().ShouldBe(2000);
        (await admin.PutAsJsonAsync($"{Url(book)}/entries/{first.Id()}", new { unitPrice = 6m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await admin.DeleteJsonAsync($"{Url(book)}/entries/{first.Id()}");
        (await admin.PutAsJsonAsync($"{Url(book)}/entries/{extra.Id()}", new { unitPrice = 9m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeletingAProduct_RemovesItsEntriesInTheSameTransaction()
    {
        var org = await factory.NewOrgAsync("Liste Ürün Sil");
        var admin = org.Admin;
        var product = await admin.CreateProductAsync("Silinecek ürün");
        var keep = await admin.CreateProductAsync("Kalan ürün");
        var one = await admin.CreatePriceBookAsync("Bir");
        var two = await admin.CreatePriceBookAsync("İki");
        foreach (var book in new[] { one, two })
        {
            await admin.PutJsonAsync($"{Url(book)}/entries/{product.Id()}", new { unitPrice = 1m });
            await admin.PutJsonAsync($"{Url(book)}/entries/{keep.Id()}", new { unitPrice = 2m });
        }

        await admin.DeleteJsonAsync($"{ProductsPath}/{product.Id()}");

        (await factory.ScalarAsync<long>("SELECT count(*) FROM commerce.price_book_entries WHERE tenant_id = @t AND product_id = @p", ("t", org.TenantId), ("p", product.Id()))).ShouldBe(0);
        (await admin.GetJsonAsync($"{Url(one)}/entries")).GetProperty("items").EnumerateArray().Select(i => i.Str("productName")).ShouldBe(["Kalan ürün"]);
        (await admin.GetJsonAsync(Url(one))).GetProperty("entryCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task EntryPriceChanges_AreAudited()
    {
        var admin = (await factory.NewOrgAsync("Liste Girdi Denetim")).Admin;
        var product = await admin.CreateProductAsync("Ürün");
        var book = await admin.CreatePriceBookAsync("Bayi");
        await admin.PutJsonAsync($"{Url(book)}/entries/{product.Id()}", new { unitPrice = 10m });
        await admin.PutJsonAsync($"{Url(book)}/entries/{product.Id()}", new { unitPrice = 12m });
        var entryId = await factory.ScalarAsync<Guid>("SELECT id FROM commerce.price_book_entries WHERE price_book_id = @b", ("b", book.Id()));

        var audit = await admin.GetJsonAsync($"{Base}/audit?entityType=PriceBookEntry&entityId={entryId}");

        var updated = audit.GetProperty("items").EnumerateArray().Single(i => i.Str("action") == "updated").GetProperty("changes").GetProperty("unitPrice");
        (updated.Dec("old"), updated.Dec("new")).ShouldBe((10m, 12m));
        audit.GetProperty("items").EnumerateArray().Select(i => i.Str("action")).ShouldContain("created");
        (await admin.GetJsonAsync($"{Base}/audit?entityType=PriceBook&entityId={book.Id()}")).GetProperty("items").EnumerateArray().Select(i => i.Str("action")).ShouldContain("created");
    }

    // ---- resolve --------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("19.99", "-10", "17.991")]
    [InlineData("10.10", "-33.33", "6.7337")]
    [InlineData("0.0001", "50", "0.0002")]
    [InlineData("100.00", "5.25", "105.25")]
    public async Task Resolve_Flat_ReturnsTheFourDecimalRoundedVector(string catalog, string percent, string expected)
    {
        var admin = (await factory.NewOrgAsync("Liste Çöz Flat")).Admin;
        var product = await admin.CreateProductAsync("Ürün", new { unitPrice = decimal.Parse(catalog, System.Globalization.CultureInfo.InvariantCulture) });
        var book = await admin.CreatePriceBookAsync("Yüzde", "flat", new { adjustmentPercent = decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture) });

        var resolved = await ResolveAsync(admin, book, product.Id());

        resolved[product.Id()].ShouldBe((decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), "flat"));
    }

    [Fact]
    public async Task Resolve_PerProduct_UsesEntryElseCatalog_InRequestOrder_AndOmitsUnknownProducts()
    {
        var a = await factory.NewOrgAsync("Liste Çöz Girdi A");
        var b = await factory.NewOrgAsync("Liste Çöz Girdi B");
        var withEntry = await a.Admin.CreateProductAsync("Girdili", new { unitPrice = 19.99m });
        var withoutEntry = await a.Admin.CreateProductAsync("Girdisiz", new { unitPrice = 19.99m });
        var deleted = await a.Admin.CreateProductAsync("Silinmiş");
        var usd = await a.Admin.CreateProductAsync("USD", new { unitPrice = 7m, currency = "USD" });
        var foreign = await b.Admin.CreateProductAsync("Yabancı");
        var book = await a.Admin.CreatePriceBookAsync("Bayi");
        await a.Admin.PutJsonAsync($"{Url(book)}/entries/{withEntry.Id()}", new { unitPrice = 12.50m });
        await a.Admin.DeleteJsonAsync($"{ProductsPath}/{deleted.Id()}");

        var response = await a.Admin.PostJsonAsync($"{Url(book)}/resolve", new { productIds = new[] { withoutEntry.Id(), foreign.Id(), withEntry.Id(), deleted.Id(), Guid.NewGuid(), usd.Id() } }, HttpStatusCode.OK);

        var items = response.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("productId").GetGuid()).ShouldBe([withoutEntry.Id(), withEntry.Id(), usd.Id()], "istek sırası; bulunamayan/silinmiş/başka kiracı ürün listede yok");
        (items[0].Dec("unitPrice"), items[0].Str("source")).ShouldBe((19.99m, "catalog"));
        (items[1].Dec("unitPrice"), items[1].Str("source")).ShouldBe((12.50m, "entry"));
        (items[2].Dec("unitPrice"), items[2].Str("source")).ShouldBe((7m, "catalog"), "para birimi uyuşmayan ürün katalog fiyatıyla döner");
        (await ResolveAsync(b.Admin, await b.Admin.CreatePriceBookAsync("B liste"), withEntry.Id())).ShouldBeEmpty("yanıt kiracı-içidir");
        await (await b.Admin.PostAsJsonAsync($"{Url(book)}/resolve", new { productIds = new[] { withEntry.Id() } }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Resolve_Validation_AndNotEffective()
    {
        var admin = (await factory.NewOrgAsync("Liste Çöz Kural")).Admin;
        var product = await admin.CreateProductAsync("Ürün");
        var book = await admin.CreatePriceBookAsync("Bayi");
        var inactive = await admin.CreatePriceBookAsync("Pasif", "perProduct", new { isActive = false });
        var future = await admin.CreatePriceBookAsync("Gelecek", "flat", new { validFrom = TenantToday().AddDays(1).DateString() });

        await (await admin.PostAsJsonAsync($"{Url(book)}/resolve", new { productIds = Array.Empty<Guid>() }, Ct)).ShouldBeValidationErrorAsync("productIds");
        await (await admin.PostAsJsonAsync($"{Url(book)}/resolve", new { }, Ct)).ShouldBeValidationErrorAsync("productIds");
        await (await admin.PostAsJsonAsync($"{Url(book)}/resolve", new { productIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray() }, Ct)).ShouldBeValidationErrorAsync("productIds");
        await (await admin.PostAsJsonAsync($"{Url(book)}/resolve", new { productIds = new[] { product.Id(), product.Id() } }, Ct)).ShouldBeValidationErrorAsync("productIds");
        (await admin.PostAsJsonAsync($"{Url(book)}/resolve", new { productIds = new[] { Guid.Empty } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"{Url(book)}/resolve", new { productIds = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await admin.PostAsJsonAsync($"{Url(inactive)}/resolve", new { productIds = new[] { product.Id() } }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.not_effective");
        await (await admin.PostAsJsonAsync($"{Url(future)}/resolve", new { productIds = new[] { product.Id() } }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "pricebook.not_effective");
    }

    // ---- Belge yazımında sunucu çözümü --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DocumentWrite_ResolvesFromTheBook_KeepsOverrides_AndStoresASnapshot()
    {
        var admin = (await factory.NewOrgAsync("Liste Belge Çözüm")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var flatProduct = await admin.CreateProductAsync("Flat", new { unitPrice = 19.99m });
        var entryProduct = await admin.CreateProductAsync("Girdili", new { unitPrice = 50m });
        var noEntry = await admin.CreateProductAsync("Girdisiz", new { unitPrice = 30m });
        var flat = await admin.CreatePriceBookAsync("Yüzde", "flat", new { adjustmentPercent = -10m });
        var perProduct = await admin.CreatePriceBookAsync("Girdi");
        await admin.PutJsonAsync($"{Url(perProduct)}/entries/{entryProduct.Id()}", new { unitPrice = 12.5m });

        var quote = await admin.CreateQuoteAsync(account.Id(), new { priceBookId = flat.Id() }, UnpricedLine(flatProduct.Id(), 10m), Line(1m, 3m, 0m, 0m, "Override", flatProduct.Id()));
        var lines = quote.GetProperty("lines").EnumerateArray().ToList();
        lines[0].Dec("unitPrice").ShouldBe(17.991m);
        lines[0].Dec("lineSubtotal").ShouldBe(179.91m);
        lines[1].Dec("unitPrice").ShouldBe(3m, "istek fiyatı override'dır");

        var order = await admin.CreateOrderAsync(account.Id(), new { priceBookId = perProduct.Id() }, UnpricedLine(entryProduct.Id(), 2m), UnpricedLine(noEntry.Id(), 1m));
        var orderLines = order.GetProperty("lines").EnumerateArray().ToList();
        orderLines[0].Dec("unitPrice").ShouldBe(12.5m);
        orderLines[1].Dec("unitPrice").ShouldBe(30m, "girdisiz ürün katalog fiyatıyla");

        var invoice = await admin.CreateInvoiceAsync(account.Id(), new { priceBookId = flat.Id() }, UnpricedLine(noEntry.Id(), 1m));
        invoice.GetProperty("lines")[0].Dec("unitPrice").ShouldBe(27m);
        (await admin.CreateQuoteAsync(account.Id(), null, UnpricedLine(noEntry.Id(), 1m))).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(30m, "liste yoksa katalog");

        // Anlık görüntü: liste ve katalog sonradan değişse belge değişmez.
        await admin.PutJsonAsync($"{Url(flat)}", new { name = "Yüzde", adjustmentPercent = -50m });
        await admin.PutJsonAsync($"{Url(perProduct)}/entries/{entryProduct.Id()}", new { unitPrice = 1m });
        await admin.PutJsonAsync($"{ProductsPath}/{flatProduct.Id()}", new { name = "Flat", unitPrice = 999m });
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(17.991m);
        (await admin.GetJsonAsync($"{OrdersPath}/{order.Id()}")).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(12.5m);

        // Güncelleme: verilmeyen fiyat yeniden çözülür (yeni liste değeriyle), verilen kalır.
        await admin.PutJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "x", accountId = account.Id(), priceBookId = flat.Id(), lines = new object[] { UnpricedLine(flatProduct.Id(), 1m) } });
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(499.5m, "999 × 0.5");
    }

    [Fact]
    public async Task DocumentBook_MustBeEffectiveAndInTheDocumentCurrency_ButAnExistingBindingSurvivesLosingEffectiveness()
    {
        var admin = (await factory.NewOrgAsync("Liste Belge Kural")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Ürün", new { unitPrice = 100m });
        var book = await admin.CreatePriceBookAsync("Bayi", "flat", new { adjustmentPercent = -10m });
        var expiring = await admin.CreatePriceBookAsync("Geçici", "flat", new { adjustmentPercent = -20m });
        var quote = await admin.CreateQuoteAsync(account.Id(), new { priceBookId = book.Id() }, UnpricedLine(product.Id(), 1m));

        await admin.PutJsonAsync(Url(book), new { name = "Bayi", adjustmentPercent = -10m, isActive = false });

        // Mevcut (değişmeyen) bağ geçerliliğini yitirse belge etkilenmez: güncelleme ve yeni kalemler sürer.
        await admin.PutJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "Güncel", accountId = account.Id(), priceBookId = book.Id(), lines = new object[] { UnpricedLine(product.Id(), 2m) } });
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(90m);

        // Etkin olmayan listeye geçiş reddedilir; etkin listeye geçilebilir; para birimi uyuşmazlığı reddedilir.
        await (await admin.PutAsJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "x", accountId = account.Id(), priceBookId = book.Id(), currency = "USD", lines = new[] { Line() } }, Ct)).ShouldBeValidationErrorAsync("priceBookId");
        var another = await admin.CreateQuoteAsync(account.Id());
        await (await admin.PutAsJsonAsync($"{QuotesPath}/{another.Id()}", new { subject = "x", accountId = account.Id(), priceBookId = book.Id() }, Ct)).ShouldBeValidationErrorAsync("priceBookId");
        await admin.PutJsonAsync($"{QuotesPath}/{another.Id()}", new { subject = "x", accountId = account.Id(), priceBookId = expiring.Id(), lines = new object[] { UnpricedLine(product.Id(), 1m) } });
        (await admin.GetJsonAsync($"{QuotesPath}/{another.Id()}")).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(80m);
    }

    [Fact]
    public async Task ReferencingABook_RequiresPriceBookReadPermission_WhenGivenOrChanged()
    {
        var org = await factory.NewOrgAsync("Liste Belge İzin");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Ürün", new { unitPrice = 100m });
        var book = await admin.CreatePriceBookAsync("Bayi", "flat", new { adjustmentPercent = -10m });
        var other = await admin.CreatePriceBookAsync("Diğer", "flat", new { adjustmentPercent = -20m });
        var (writer, _) = await factory.AddMemberAsync(org, "Yazar", "crm.quotes.write", "crm.orders.write", "crm.invoices.write", "crm.quotes.read");
        var withBook = await admin.CreateQuoteAsync(account.Id(), new { priceBookId = book.Id() }, UnpricedLine(product.Id(), 1m));

        foreach (var path in new[] { QuotesPath, OrdersPath, InvoicesPath })
        {
            await (await writer.PostAsJsonAsync(path, new { subject = "x", accountId = account.Id(), priceBookId = book.Id(), lines = new object[] { UnpricedLine(product.Id(), 1m) } }, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        var body = new { subject = "x", accountId = account.Id(), priceBookId = book.Id(), lines = new[] { Line() } };
        (await writer.PutAsJsonAsync($"{QuotesPath}/{withBook.Id()}", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent, "değişmeyen bağ yeniden denetlenmez");
        await (await writer.PutAsJsonAsync($"{QuotesPath}/{withBook.Id()}", body with { priceBookId = other.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await writer.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), lines = new[] { Line() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created, "liste vermeyen belge serbest");
        (await factory.CountAsync("quotes", org.TenantId)).ShouldBe(2);
    }

    // ---- Firma varsayılanı ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AccountDefault_PutGetDelete_ReplacesAndReportsEffectiveness_AndGuardsForeignRecords()
    {
        var a = await factory.NewOrgAsync("Liste Varsayılan A");
        var b = await factory.NewOrgAsync("Liste Varsayılan B");
        var account = await a.Admin.CreateAccountAsync("Firma");
        var foreignAccount = await b.Admin.CreateAccountAsync("Yabancı");
        var one = await a.Admin.CreatePriceBookAsync("Bir");
        var two = await a.Admin.CreatePriceBookAsync("İki", "flat", new { isActive = false });
        var url = $"{PriceBooksPath}/accounts/{account.Id()}/default";

        (await a.Admin.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await a.Admin.PutJsonAsync(url, new { priceBookId = one.Id() });
        var current = await a.Admin.GetJsonAsync(url);
        (current.GetProperty("priceBookId").GetGuid(), current.Str("priceBookName"), current.GetProperty("isEffective").GetBoolean()).ShouldBe((one.Id(), "Bir", true));

        await a.Admin.PutJsonAsync(url, new { priceBookId = two.Id() });
        var replaced = await a.Admin.GetJsonAsync(url);
        (replaced.GetProperty("priceBookId").GetGuid(), replaced.GetProperty("isEffective").GetBoolean()).ShouldBe((two.Id(), false), "etkin olmayan varsayılan da saklanır, isEffective false");
        (await factory.CountAsync("account_price_books", a.TenantId)).ShouldBe(1, "firma başına tek satır");

        await (await a.Admin.PutAsJsonAsync(url, new { priceBookId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.PutAsJsonAsync(url, new { priceBookId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("priceBookId");
        await (await a.Admin.PutAsJsonAsync($"{PriceBooksPath}/accounts/{foreignAccount.Id()}/default", new { priceBookId = one.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PutAsJsonAsync($"{PriceBooksPath}/accounts/{Guid.NewGuid()}/default", new { priceBookId = one.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.GetAsync($"{PriceBooksPath}/accounts/{foreignAccount.Id()}/default", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await b.Admin.PutAsJsonAsync($"{PriceBooksPath}/accounts/{foreignAccount.Id()}/default", new { priceBookId = one.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PutAsJsonAsync(url, new { priceBookId = one.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");

        await a.Admin.DeleteJsonAsync(url);
        await a.Admin.DeleteJsonAsync(url);
        (await a.Admin.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Sunucu belgeye otomatik uygulamaz.
        await a.Admin.PutJsonAsync(url, new { priceBookId = one.Id() });
        (await a.Admin.CreateQuoteAsync(account.Id())).TryGetProperty("priceBookId", out _).ShouldBeFalse();
    }
}
