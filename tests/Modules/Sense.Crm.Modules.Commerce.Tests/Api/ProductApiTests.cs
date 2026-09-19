using System.Net;
using System.Net.Http.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Commerce.Tests.Api;

/// <summary>Ürün kataloğu (<c>/products</c>): CRUD, filtre, sıralama/sayfalama, arama kaçışı, kod benzersizliği, yetki ve doğrulama.</summary>
[Collection(ApiCollection.Name)]
public sealed class ProductApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Crud_RoundTrip_WithLocationHeader_AndSoftDelete()
    {
        var org = await factory.NewOrgAsync("Ürün Org");
        var admin = org.Admin;

        var response = await admin.PostAsJsonAsync(ProductsPath, new { name = "  CRM Pro  ", code = "crm-pro", description = "Yıllık", unitPrice = 1250.5m, currency = "usd", taxRate = 18, unit = "lisans" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        response.Headers.Location!.ToString().ShouldEndWith($"{ProductsPath}/{created.Id()}");
        created.Str("name").ShouldBe("CRM Pro");
        created.Str("code").ShouldBe("crm-pro");
        created.Dec("unitPrice").ShouldBe(1250.5m);
        created.Str("currency").ShouldBe("USD");
        created.Dec("taxRate").ShouldBe(18m);
        created.Str("unit").ShouldBe("lisans");
        created.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        created.TryGetProperty("updatedAt", out _).ShouldBeFalse("null alanlar yazılmaz");

        (await admin.GetJsonAsync($"{ProductsPath}/{created.Id()}")).Str("name").ShouldBe("CRM Pro");

        await admin.PutJsonAsync($"{ProductsPath}/{created.Id()}", new { name = "CRM Pro Plus", unitPrice = 2000m, taxRate = 20 });
        var updated = await admin.GetJsonAsync($"{ProductsPath}/{created.Id()}");
        updated.Str("name").ShouldBe("CRM Pro Plus");
        updated.TryGetProperty("code", out _).ShouldBeFalse("PUT tam değiştirme: gönderilmeyen isteğe bağlı alan temizlenir");
        updated.TryGetProperty("description", out _).ShouldBeFalse();
        updated.TryGetProperty("unit", out _).ShouldBeFalse();
        updated.Str("currency").ShouldBe("TRY");
        updated.GetProperty("isActive").GetBoolean().ShouldBeTrue("isActive verilmezse mevcut korunur");
        updated.TryGetProperty("updatedAt", out _).ShouldBeTrue();

        await admin.DeleteJsonAsync($"{ProductsPath}/{created.Id()}");
        await (await admin.GetAsync($"{ProductsPath}/{created.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.DeleteAsync($"{ProductsPath}/{created.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Create_WithOnlyName_UsesApiDefaults()
    {
        var admin = (await factory.NewOrgAsync("Ürün Varsayılan")).Admin;

        var product = await admin.PostJsonAsync(ProductsPath, new { name = "Danışmanlık" });

        product.Str("currency").ShouldBe("TRY");
        product.Dec("taxRate").ShouldBe(0m, "API varsayılanı 0 (web formu 20 önerir)");
        product.Dec("unitPrice").ShouldBe(0m);
        product.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        product.TryGetProperty("code", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task IsActive_CanBeSetOnCreate_AndPreservedOrToggledOnUpdate()
    {
        var admin = (await factory.NewOrgAsync("Ürün Aktif")).Admin;
        var product = await admin.PostJsonAsync(ProductsPath, new { name = "Pasif", isActive = false });
        product.GetProperty("isActive").GetBoolean().ShouldBeFalse();

        await admin.PutJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Pasif" });
        (await admin.GetJsonAsync($"{ProductsPath}/{product.Id()}")).GetProperty("isActive").GetBoolean().ShouldBeFalse();

        await admin.PutJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Pasif", isActive = true });
        (await admin.GetJsonAsync($"{ProductsPath}/{product.Id()}")).GetProperty("isActive").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("unitPrice", -1)]
    [InlineData("unitPrice", 1_000_000_001)]
    [InlineData("taxRate", 100.01)]
    [InlineData("taxRate", -0.5)]
    [InlineData("taxRate", 18.123)]
    [InlineData("currency", "TL")]
    [InlineData("currency", "XXX")]
    public async Task InvalidFields_AreReportedPerField(string field, object value)
    {
        var admin = (await factory.NewOrgAsync("Ürün Doğrulama")).Admin;
        var body = Merge(new Dictionary<string, object?> { ["name"] = "Geçerli" }, new Dictionary<string, object?> { [field] = value });

        await (await admin.PostAsJsonAsync(ProductsPath, body, Ct)).ShouldBeValidationErrorAsync(field);
    }

    [Fact]
    public async Task Decimals_AndLengths_AreValidated()
    {
        var admin = (await factory.NewOrgAsync("Ürün Uzunluk")).Admin;

        await (await admin.PostAsJsonAsync(ProductsPath, new { name = "A", unitPrice = 1.12345m }, Ct)).ShouldBeValidationErrorAsync("unitPrice");
        await (await admin.PostAsJsonAsync(ProductsPath, new { name = new string('x', 201) }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(ProductsPath, new { name = "A", code = new string('k', 65) }, Ct)).ShouldBeValidationErrorAsync("code");
        await (await admin.PostAsJsonAsync(ProductsPath, new { name = "A", unit = new string('u', 33) }, Ct)).ShouldBeValidationErrorAsync("unit");
        await (await admin.PostAsJsonAsync(ProductsPath, new { name = "A", description = new string('d', 2001) }, Ct)).ShouldBeValidationErrorAsync("description");
        (await admin.PostAsJsonAsync(ProductsPath, new { name = "Sınırda", unitPrice = 1_000_000_000m, taxRate = 100, code = new string('k', 64) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CodeIsUniquePerTenant_CaseInsensitive_AmongNonDeleted()
    {
        var admin = (await factory.NewOrgAsync("Ürün Kod")).Admin;
        var first = await admin.CreateProductAsync("Birinci", new { code = "SKU-1" });

        await (await admin.PostAsJsonAsync(ProductsPath, new { name = "İkinci", code = "sku-1" }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "product.code_taken");
        await (await admin.PostAsJsonAsync(ProductsPath, new { name = "Üçüncü", code = "  SKU-1  " }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "product.code_taken");

        var second = await admin.CreateProductAsync("Diğer", new { code = "SKU-2" });
        await (await admin.PutAsJsonAsync($"{ProductsPath}/{second.Id()}", new { name = "Diğer", code = "Sku-1" }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "product.code_taken");
        await admin.PutJsonAsync($"{ProductsPath}/{first.Id()}", new { name = "Birinci", code = "sku-1" });

        // Boş kod benzersizlik dışıdır.
        await admin.CreateProductAsync("Kodsuz 1");
        await admin.CreateProductAsync("Kodsuz 2", new { code = "   " });

        // Silinen ürünün kodu yeniden kullanılabilir.
        await admin.DeleteJsonAsync($"{ProductsPath}/{first.Id()}");
        var reused = await admin.CreateProductAsync("Yeniden", new { code = "SKU-1" });
        reused.Str("code").ShouldBe("SKU-1");
    }

    [Fact]
    public async Task SameCode_IsAllowedInDifferentTenants()
    {
        var a = (await factory.NewOrgAsync("Kod A")).Admin;
        var b = (await factory.NewOrgAsync("Kod B")).Admin;

        await a.CreateProductAsync("A ürünü", new { code = "ORTAK" });
        await b.CreateProductAsync("B ürünü", new { code = "ORTAK" });
    }

    [Fact]
    public async Task List_FiltersByQuery_IsActive_AndCurrency()
    {
        var admin = (await factory.NewOrgAsync("Ürün Liste")).Admin;
        var crm = await admin.CreateProductAsync("CRM Pro", new { code = "CRM-1", description = "Bulut lisansı" });
        var support = await admin.CreateProductAsync("Destek Paketi", new { code = "SUP-9", currency = "USD", isActive = false });
        var training = await admin.CreateProductAsync("Eğitim", new { description = "crm eğitimi", currency = "EUR" });

        (await admin.ListIdsAsync(ProductsPath, "?q=crm")).ShouldBe([crm.Id(), training.Id()], ignoreOrder: true);
        (await admin.ListIdsAsync(ProductsPath, "?q=sup-9")).ShouldBe([support.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?q=BULUT")).ShouldBe([crm.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?isActive=false")).ShouldBe([support.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?isActive=true")).ShouldBe([crm.Id(), training.Id()], ignoreOrder: true);
        (await admin.ListIdsAsync(ProductsPath, "?currency=usd")).ShouldBe([support.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?currency=EUR&isActive=true&q=eğitim")).ShouldBe([training.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?q=yok-böyle-ürün")).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_SortsAndPages_WithStableDefaultOrder()
    {
        var admin = (await factory.NewOrgAsync("Ürün Sıra")).Admin;
        var c = await admin.CreateProductAsync("Charlie", new { code = "C", unitPrice = 30m });
        var a = await admin.CreateProductAsync("Alpha", new { code = "A", unitPrice = 10m });
        var b = await admin.CreateProductAsync("Bravo", new { unitPrice = 20m });

        (await admin.ListIdsAsync(ProductsPath)).ShouldBe([a.Id(), b.Id(), c.Id()], "varsayılan: ada göre artan");
        (await admin.ListIdsAsync(ProductsPath, "?sort=-name")).ShouldBe([c.Id(), b.Id(), a.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?sort=-unitPrice")).ShouldBe([c.Id(), b.Id(), a.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?sort=unitPrice")).ShouldBe([a.Id(), b.Id(), c.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?sort=code")).ShouldBe([a.Id(), c.Id(), b.Id()], "kodsuzlar sonda");
        (await admin.ListIdsAsync(ProductsPath, "?sort=-code")).ShouldBe([c.Id(), a.Id(), b.Id()], "kodsuzlar her yönde sonda");
        (await admin.ListIdsAsync(ProductsPath, "?sort=-createdAt")).ShouldBe([b.Id(), a.Id(), c.Id()]);
        (await admin.ListIdsAsync(ProductsPath, "?sort=bilinmeyenAlan")).ShouldBe([a.Id(), b.Id(), c.Id()], "bilinmeyen alan yok sayılır");

        var page = await admin.GetJsonAsync($"{ProductsPath}?pageSize=2&page=2");
        page.GetProperty("totalCount").GetInt64().ShouldBe(3);
        page.GetProperty("page").GetInt32().ShouldBe(2);
        page.GetProperty("pageSize").GetInt32().ShouldBe(2);
        page.GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([c.Id()]);
    }

    [Fact]
    public async Task Search_EscapesLikeWildcards()
    {
        var admin = (await factory.NewOrgAsync("Ürün Joker")).Admin;
        var percent = await admin.CreateProductAsync("Yüzde 50% indirimli");
        var underscore = await admin.CreateProductAsync("kod_a");
        var plain = await admin.CreateProductAsync("kodxa");

        (await admin.ListIdsAsync(ProductsPath, "?q=%25")).ShouldBe([percent.Id()], "% joker değil, düz karakter");
        (await admin.ListIdsAsync(ProductsPath, "?q=kod_a")).ShouldBe([underscore.Id()], "_ joker değil");
        (await admin.ListIdsAsync(ProductsPath, "?q=kod")).ShouldBe([underscore.Id(), plain.Id()], ignoreOrder: true);
        (await admin.ListIdsAsync(ProductsPath, "?q=%5C")).ShouldBeEmpty("ters bölü de kaçışlanır");
    }

    [Fact]
    public async Task Permissions_ReadOnlyMemberCannotWrite_AndValidationRunsBeforeAuthorization()
    {
        var org = await factory.NewOrgAsync("Ürün Yetki");
        var product = await org.Admin.CreateProductAsync("Var");
        var (reader, _) = await factory.AddMemberAsync(org, "Okuyucu", "crm.products.read");
        var (writer, _) = await factory.AddMemberAsync(org, "Yazıcı", "crm.products.write");
        var (nobody, _) = await factory.AddMemberAsync(org, "Yetkisiz", "crm.accounts.read");

        (await reader.GetJsonAsync(ProductsPath)).GetProperty("totalCount").GetInt64().ShouldBe(1);
        (await reader.GetJsonAsync($"{ProductsPath}/{product.Id()}")).Str("name").ShouldBe("Var");
        await (await reader.PostAsJsonAsync(ProductsPath, new { name = "Yeni" }, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PutAsJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Yeni" }, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.DeleteAsync($"{ProductsPath}/{product.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        await (await writer.GetAsync(ProductsPath, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var written = await writer.PostAsJsonAsync(ProductsPath, new { name = "Yazılan" }, Ct);
        written.StatusCode.ShouldBe(HttpStatusCode.Created, "yazma izni yeterli: yanıt gövdesi okuma izni gerektirmez");

        await (await nobody.GetAsync(ProductsPath, Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Doğrulama yetkiden önce: geçersiz gövdeli yetkisiz istek 400 alır.
        await (await reader.PostAsJsonAsync(ProductsPath, new { name = "" }, Ct)).ShouldBeValidationErrorAsync("name");
    }

    [Fact]
    public async Task Isolation_OtherTenantsProductIsInvisible()
    {
        var a = (await factory.NewOrgAsync("Ürün İzole A")).Admin;
        var b = (await factory.NewOrgAsync("Ürün İzole B")).Admin;
        var product = await a.CreateProductAsync("Sadece A", new { code = "GIZLI" });

        await (await b.GetAsync($"{ProductsPath}/{product.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.PutAsJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Ele geçirildi" }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.DeleteAsync($"{ProductsPath}/{product.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await b.ListIdsAsync(ProductsPath)).ShouldBeEmpty();
        (await b.ListIdsAsync(ProductsPath, "?q=gizli")).ShouldBeEmpty();
        (await a.GetJsonAsync($"{ProductsPath}/{product.Id()}")).Str("name").ShouldBe("Sadece A");
    }

    [Fact]
    public async Task Audit_RecordsProductChanges_ViaRecordEndpoint_WithReadPermission()
    {
        var org = await factory.NewOrgAsync("Ürün Denetim");
        var product = await org.Admin.CreateProductAsync("Denetimli", new { code = "D-1" });
        await org.Admin.PutJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Denetimli 2", unitPrice = 5m, code = "D-1" });
        await org.Admin.DeleteJsonAsync($"{ProductsPath}/{product.Id()}");
        var (reader, _) = await factory.AddMemberAsync(org, "Okuyucu", "crm.products.read");
        var (nobody, _) = await factory.AddMemberAsync(org, "Yetkisiz", "crm.quotes.read");

        var audit = await reader.GetJsonAsync($"{Base}/audit?entityType=Product&entityId={product.Id()}");

        audit.GetProperty("items").EnumerateArray().Select(i => i.Str("action")).ShouldBe(["deleted", "updated", "created"]);
        await (await nobody.GetAsync($"{Base}/audit?entityType=Product&entityId={product.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }
}
