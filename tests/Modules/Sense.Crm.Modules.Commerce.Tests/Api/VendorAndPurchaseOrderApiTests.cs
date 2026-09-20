using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Commerce.Tests.Api;

/// <summary>Tedarikçi (CRUD, süzgeç, denetim maskesi, ürün bağlantısı) ve satın alma emri (tedarikçi tarafı fiyat, durum makinesi, numara, olaylar, rapor).</summary>
[Collection(ApiCollection.Name)]
public sealed class VendorAndPurchaseOrderApiTests(CrmApiFactory factory)
{
    private static int TenantYear() => TenantToday().Year;

    // ---- Tedarikçi -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Vendor_Create_ReturnsTheFullShape_WithNormalizedFields()
    {
        var org = await factory.NewOrgAsync("Tedarikçi Oluştur");
        var admin = org.Admin;

        var vendor = await admin.CreateVendorAsync(
            "  Acme Tedarik ",
            new
            {
                phone = " 0532 000 00 00 ",
                email = "  INFO@Acme.COM ",
                website = "https://acme.example",
                category = "Yedek parça",
                glAccount = "Cost of Goods Sold",
                address = new { street = "Sanayi sk. 4", building = "Blok B", city = "Bursa" },
                description = "Ana tedarikçi",
                emailOptOut = true,
            });

        vendor.Str("name").ShouldBe("Acme Tedarik");
        vendor.Str("email").ShouldBe("info@acme.com");
        vendor.Str("phone").ShouldBe("0532 000 00 00");
        vendor.Str("website").ShouldBe("https://acme.example");
        vendor.Str("category").ShouldBe("Yedek parça");
        vendor.Str("glAccount").ShouldBe("Cost of Goods Sold");
        vendor.GetProperty("address").GetProperty("building").GetString().ShouldBe("Blok B");
        vendor.GetProperty("emailOptOut").GetBoolean().ShouldBeTrue();
        vendor.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        vendor.Str("ownerName").ShouldBe(org.AdminName);
        (vendor.GetProperty("productCount").GetInt32(), vendor.GetProperty("purchaseOrderCount").GetInt32()).ShouldBe((0, 0));
        (await admin.CreateVendorAsync("Basit")).GetProperty("emailOptOut").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Vendor_Validation()
    {
        var admin = (await factory.NewOrgAsync("Tedarikçi Doğrulama")).Admin;

        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = new string('a', 201) }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", email = "yanlış" }, Ct)).ShouldBeValidationErrorAsync("email");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", phone = new string('1', 33) }, Ct)).ShouldBeValidationErrorAsync("phone");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", website = "javascript:alert(1)" }, Ct)).ShouldBeValidationErrorAsync("website");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", website = "acme.example" }, Ct)).ShouldBeValidationErrorAsync("website");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", category = new string('a', 101) }, Ct)).ShouldBeValidationErrorAsync("category");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", glAccount = new string('a', 101) }, Ct)).ShouldBeValidationErrorAsync("glAccount");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", description = new string('a', 2001) }, Ct)).ShouldBeValidationErrorAsync("description");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", address = new { street = new string('a', 201) } }, Ct)).ShouldBeValidationErrorAsync("address.street");
        await (await admin.PostAsJsonAsync(VendorsPath, new { name = "x", ownerUserId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        (await admin.ListIdsAsync(VendorsPath)).ShouldBeEmpty();
        (await admin.PostAsJsonAsync(VendorsPath, new { name = "Aynı ad" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync(VendorsPath, new { name = "Aynı ad" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created, "ad benzersiz değildir");
    }

    [Fact]
    public async Task Vendor_Update_IsAFullReplacement_KeepsEmailOptOutAndOwnerWhenOmitted()
    {
        var org = await factory.NewOrgAsync("Tedarikçi Güncelle");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.vendors.read");
        var vendor = await admin.CreateVendorAsync("Eski", new { phone = "1", email = "a@b.co", category = "K", emailOptOut = true, ownerUserId = memberId, address = new { city = "X" } });
        var url = $"{VendorsPath}/{vendor.Id()}";

        await admin.PutJsonAsync(url, new { name = "Yeni" });

        var replaced = await admin.GetJsonAsync(url);
        replaced.Str("name").ShouldBe("Yeni");
        replaced.GetProperty("emailOptOut").GetBoolean().ShouldBeTrue("verilmeyen emailOptOut korunur");
        replaced.GetProperty("ownerUserId").GetGuid().ShouldBe(memberId, "verilmeyen sahip korunur");
        foreach (var field in new[] { "phone", "email", "category", "address" })
        {
            replaced.TryGetProperty(field, out _).ShouldBeFalse($"{field} tam değiştirmede temizlenir");
        }

        await admin.PutJsonAsync(url, new { name = "Yeni", emailOptOut = false });
        (await admin.GetJsonAsync(url)).GetProperty("emailOptOut").GetBoolean().ShouldBeFalse();
        await (await admin.PutAsJsonAsync($"{VendorsPath}/{Guid.NewGuid()}", new { name = "x" }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PutAsJsonAsync(url, new { name = "" }, Ct)).ShouldBeValidationErrorAsync("name");
    }

    [Fact]
    public async Task Vendor_List_FiltersSortsAndEscapesWildcards()
    {
        var org = await factory.NewOrgAsync("Tedarikçi Liste");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.vendors.read");
        var alfa = await admin.CreateVendorAsync("Alfa", new { category = "Yedek parça", email = "sales@alfa.io", phone = "5551112233" });
        var beta = await admin.CreateVendorAsync("Beta %", new { category = "Hizmet", ownerUserId = memberId, emailOptOut = true });
        var gama = await admin.CreateVendorAsync("Gama_x");

        (await admin.ListIdsAsync(VendorsPath)).ShouldBe([alfa.Id(), beta.Id(), gama.Id()], "varsayılan ad artan");
        (await admin.ListIdsAsync(VendorsPath, "?sort=-name")).ShouldBe([gama.Id(), beta.Id(), alfa.Id()]);
        (await admin.ListIdsAsync(VendorsPath, "?category=hizmet")).ShouldBe([beta.Id()]);
        (await admin.ListIdsAsync(VendorsPath, $"?ownerUserId={memberId}")).ShouldBe([beta.Id()]);
        (await admin.ListIdsAsync(VendorsPath, "?emailOptOut=true")).ShouldBe([beta.Id()]);
        (await admin.ListIdsAsync(VendorsPath, "?q=ALFA.io")).ShouldBe([alfa.Id()], "e-posta");
        (await admin.ListIdsAsync(VendorsPath, "?q=1112233")).ShouldBe([alfa.Id()], "telefon");
        (await admin.ListIdsAsync(VendorsPath, "?q=yedek")).ShouldBe([alfa.Id()], "kategori");
        (await admin.ListIdsAsync(VendorsPath, "?q=%25")).ShouldBe([beta.Id()], "joker kaçışlanır");
        (await admin.ListIdsAsync(VendorsPath, "?q=_")).ShouldBe([gama.Id()]);
        (await admin.ListIdsAsync(VendorsPath, "?sort=category")).ShouldBe([beta.Id(), alfa.Id(), gama.Id()], "kategorisiz sonda");
        (await admin.ListIdsAsync(VendorsPath, "?sort=-category")).ShouldBe([alfa.Id(), beta.Id(), gama.Id()], "kategorisiz her yönde sonda");
        (await admin.ListIdsAsync(VendorsPath, "?sort=createdAt")).ShouldBe([alfa.Id(), beta.Id(), gama.Id()]);
        (await admin.ListIdsAsync(VendorsPath, "?pageSize=2&page=2")).ShouldBe([gama.Id()]);
    }

    [Fact]
    public async Task Vendor_AuditMasksEmailAndPhone_ButShowsOtherFieldChanges()
    {
        var admin = (await factory.NewOrgAsync("Tedarikçi Denetim")).Admin;
        var vendor = await admin.CreateVendorAsync("Acme", new { phone = "5550001111", email = "gizli@acme.io", category = "A" });
        await admin.PutJsonAsync($"{VendorsPath}/{vendor.Id()}", new { name = "Acme", phone = "5550002222", email = "yeni@acme.io", category = "B" });

        var audit = await admin.GetJsonAsync($"{Base}/audit?entityType=Vendor&entityId={vendor.Id()}");

        var items = audit.GetProperty("items").EnumerateArray().ToList();
        var updated = items.Single(i => i.Str("action") == "updated").GetProperty("changes");
        updated.GetProperty("email").GetProperty("old").GetString().ShouldBe("***");
        updated.GetProperty("email").GetProperty("new").GetString().ShouldBe("***");
        updated.GetProperty("phone").GetProperty("new").GetString().ShouldBe("***");
        updated.GetProperty("category").GetProperty("old").GetString().ShouldBe("A");
        updated.GetProperty("category").GetProperty("new").GetString().ShouldBe("B");
        var raw = audit.ToString();
        raw.ShouldNotContain("gizli@acme.io");
        raw.ShouldNotContain("5550001111");
        raw.ShouldNotContain("yeni@acme.io");
    }

    [Fact]
    public async Task Vendor_Delete_IsBlockedByLivePurchaseOrders_AndClearsTheProductLinks()
    {
        var org = await factory.NewOrgAsync("Tedarikçi Sil");
        var admin = org.Admin;
        var vendor = await admin.CreateVendorAsync("Acme");
        var product = await admin.CreateProductAsync("Bağlı ürün", new { vendorId = vendor.Id(), purchasePrice = 70m });
        var order = await admin.CreatePurchaseOrderAsync(vendor.Id());
        var url = $"{VendorsPath}/{vendor.Id()}";
        (await admin.GetJsonAsync(url)).GetProperty("productCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync(url)).GetProperty("purchaseOrderCount").GetInt32().ShouldBe(1);

        await (await admin.DeleteAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "vendor.in_use");
        (await admin.GetJsonAsync($"{ProductsPath}/{product.Id()}")).GetProperty("vendorId").GetGuid().ShouldBe(vendor.Id(), "engellenen silme ürünleri etkilemez");

        await admin.DeleteJsonAsync($"{PurchaseOrdersPath}/{order.Id()}");
        await admin.DeleteJsonAsync(url);

        await (await admin.GetAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        var cleared = await admin.GetJsonAsync($"{ProductsPath}/{product.Id()}");
        cleared.TryGetProperty("vendorId", out _).ShouldBeFalse("ürün bağı aynı transaction'da temizlenir");
        cleared.TryGetProperty("vendorName", out _).ShouldBeFalse();
        cleared.Dec("purchasePrice").ShouldBe(70m, "satın alma fiyatı korunur");
        await (await admin.DeleteAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Product_VendorLink_PurchasePrice_AndTheVendorFilter()
    {
        var a = await factory.NewOrgAsync("Ürün Tedarikçi A");
        var b = await factory.NewOrgAsync("Ürün Tedarikçi B");
        var acme = await a.Admin.CreateVendorAsync("Acme");
        var other = await a.Admin.CreateVendorAsync("Diğer");
        var foreign = await b.Admin.CreateVendorAsync("Yabancı");

        var linked = await a.Admin.CreateProductAsync("Bağlı", new { vendorId = acme.Id(), purchasePrice = 12.3456m });
        var unlinked = await a.Admin.CreateProductAsync("Bağsız");

        linked.GetProperty("vendorId").GetGuid().ShouldBe(acme.Id());
        linked.Str("vendorName").ShouldBe("Acme");
        linked.Dec("purchasePrice").ShouldBe(12.3456m);
        unlinked.TryGetProperty("vendorId", out _).ShouldBeFalse();
        (await a.Admin.ListIdsAsync(ProductsPath, $"?vendorId={acme.Id()}")).ShouldBe([linked.Id()]);
        (await a.Admin.ListIdsAsync(ProductsPath, $"?vendorId={other.Id()}")).ShouldBeEmpty();
        var listed = (await a.Admin.GetJsonAsync($"{ProductsPath}?vendorId={acme.Id()}")).GetProperty("items")[0];
        listed.Str("vendorName").ShouldBe("Acme");

        await (await a.Admin.PostAsJsonAsync(ProductsPath, new { name = "x", vendorId = foreign.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PostAsJsonAsync(ProductsPath, new { name = "x", vendorId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PostAsJsonAsync(ProductsPath, new { name = "x", purchasePrice = -1m }, Ct)).ShouldBeValidationErrorAsync("purchasePrice");
        await (await a.Admin.PostAsJsonAsync(ProductsPath, new { name = "x", purchasePrice = 1_000_000_001m }, Ct)).ShouldBeValidationErrorAsync("purchasePrice");
        await (await a.Admin.PostAsJsonAsync(ProductsPath, new { name = "x", purchasePrice = 1.00001m }, Ct)).ShouldBeValidationErrorAsync("purchasePrice");
        await (await a.Admin.PutAsJsonAsync($"{ProductsPath}/{linked.Id()}", new { name = "Bağlı", vendorId = foreign.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");

        // Ürün PUT'u tam değiştirmedir: gönderilmeyen vendorId/purchasePrice temizlenir.
        await a.Admin.PutJsonAsync($"{ProductsPath}/{linked.Id()}", new { name = "Bağlı" });
        var replaced = await a.Admin.GetJsonAsync($"{ProductsPath}/{linked.Id()}");
        replaced.TryGetProperty("vendorId", out _).ShouldBeFalse();
        replaced.TryGetProperty("purchasePrice", out _).ShouldBeFalse();
    }

    // ---- Satın alma emri --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PurchaseOrder_Create_HasAServerNumber_DefaultsAndTheFullShape()
    {
        var org = await factory.NewOrgAsync("PO Oluştur");
        var admin = org.Admin;
        var vendor = await admin.CreateVendorAsync("Acme");
        var account = await admin.CreateAccountAsync("Firma");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = account.Id() });
        var today = TenantToday();

        var order = await admin.CreatePurchaseOrderAsync(
            vendor.Id(),
            new
            {
                contactId = contact.Id(),
                dueDate = today.AddDays(15).DateString(),
                carrier = "MNG",
                adjustment = -0.44m,
                exciseTax = 5m,
                salesCommission = 10m,
                terms = "Peşin",
                notes = "Not",
                billingAddress = new { city = "Bursa" },
                shippingAddress = new { city = "İzmir", building = "Depo 2" },
                number = "PO-9999",
                status = "received",
                grandTotal = 1m,
            },
            DocumentOneAndThree);

        order.Str("number").ShouldBe($"PO-{TenantYear()}-0001");
        order.Str("status").ShouldBe("draft");
        order.GetProperty("vendorId").GetGuid().ShouldBe(vendor.Id());
        order.Str("vendorName").ShouldBe("Acme");
        order.GetProperty("contactId").GetGuid().ShouldBe(contact.Id());
        order.Str("contactName").ShouldBe("Kaya");
        order.Str("poDate").ShouldBe(today.DateString(), "poDate verilmezse bugün");
        order.Str("dueDate").ShouldBe(today.AddDays(15).DateString());
        order.Dec("grandTotal").ShouldBe(94.12m, "94.56 − 0.44; gider vergisi/komisyon toplama girmez");
        order.Dec("adjustment").ShouldBe(-0.44m);
        order.Str("carrier").ShouldBe("MNG");
        order.Dec("exciseTax").ShouldBe(5m);
        order.GetProperty("shippingAddress").GetProperty("building").GetString().ShouldBe("Depo 2");
        order.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        order.GetProperty("lines").GetArrayLength().ShouldBe(2);
        (await factory.OutboxCountAsync(org.TenantId, "PurchaseOrderCreated")).ShouldBe(1);
        var payload = JsonDocument.Parse(await factory.ScalarAsync<string>("SELECT payload::text FROM commerce.outbox_messages WHERE tenant_id = @t AND type ILIKE '%PurchaseOrderCreated%'", ("t", org.TenantId))).RootElement;
        payload.GetProperty("vendorId").GetGuid().ShouldBe(vendor.Id());
        payload.GetProperty("purchaseOrderId").GetGuid().ShouldBe(order.Id());
        payload.GetProperty("grandTotal").GetDecimal().ShouldBe(94.12m);
    }

    private static readonly object[] DocumentOneAndThree = [Line(3m, 19.99m, 10m, 20m, "Lisans"), Line(2.5m, 10.10m, 0m, 18m, "Danışmanlık")];

    [Fact]
    public async Task PurchaseOrder_Validation_AndRelatedRecords()
    {
        var a = await factory.NewOrgAsync("PO Kural A");
        var b = await factory.NewOrgAsync("PO Kural B");
        var vendor = await a.Admin.CreateVendorAsync("Acme");
        var foreignVendor = await b.Admin.CreateVendorAsync("Yabancı");
        var foreignAccount = await b.Admin.CreateAccountAsync("Yabancı firma");
        var foreignContact = await b.Admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Y", accountId = foreignAccount.Id() });
        var today = TenantToday();

        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = foreignVendor.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), contactId = foreignContact.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x" }, Ct)).ShouldBeValidationErrorAsync("vendorId");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "", vendorId = vendor.Id() }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), dueDate = today.AddDays(-1).DateString(), poDate = today.DateString() }, Ct)).ShouldBeValidationErrorAsync("dueDate");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), adjustment = 0.01m }, Ct)).ShouldBeValidationErrorAsync("adjustment");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), exciseTax = -1m }, Ct)).ShouldBeValidationErrorAsync("exciseTax");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), lines = new[] { Line(0m) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].quantity");
        await (await a.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), currency = "XX9" }, Ct)).ShouldBeValidationErrorAsync("currency");
        (await a.Admin.ListIdsAsync(PurchaseOrdersPath)).ShouldBeEmpty();
        (await factory.CounterAsync(a.TenantId, "purchaseOrder", TenantYear())).ShouldBe(0, "geçersiz PO numara tüketmez");
    }

    [Fact]
    public async Task PurchaseOrder_LinePrice_ComesFromThePurchasePrice_AndOverridesAreKept()
    {
        var admin = (await factory.NewOrgAsync("PO Fiyat")).Admin;
        var vendor = await admin.CreateVendorAsync("Acme");
        var priced = await admin.CreateProductAsync("Fiyatlı", new { unitPrice = 100m, purchasePrice = 60.5m, vendorId = vendor.Id() });
        var unpriced = await admin.CreateProductAsync("Fiyatsız", new { unitPrice = 100m });

        var order = await admin.CreatePurchaseOrderAsync(vendor.Id(), null, UnpricedLine(priced.Id(), 2m, 0m, 20m), Line(1m, 10m, 0m, 0m, "Serbest kalem"), Line(1m, 7m, 0m, 0m, "Override", priced.Id()));

        var lines = order.GetProperty("lines").EnumerateArray().ToList();
        lines[0].Dec("unitPrice").ShouldBe(60.5m, "satış fiyatı değil, tedarikçi tarafı satın alma fiyatı");
        lines[0].Dec("lineTotal").ShouldBe(145.2m);
        lines[1].Dec("unitPrice").ShouldBe(10m);
        lines[2].Dec("unitPrice").ShouldBe(7m, "istek fiyatı override");

        var unresolved = await admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), lines = new[] { UnpricedLine(unpriced.Id(), 1m) } }, Ct);
        await unresolved.ShouldBeValidationErrorAsync("lines[0].unitPrice");
        await (await admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), lines = new[] { UnpricedLine(null, 1m) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].unitPrice");
        await (await admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), lines = new[] { Line(productId: Guid.NewGuid()) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].productId");
        await (await admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), currency = "USD", lines = new[] { Line(productId: priced.Id()) } }, Ct)).ShouldBeValidationErrorAsync("lines[0].productId");

        // Anlık görüntü.
        await admin.PutJsonAsync($"{ProductsPath}/{priced.Id()}", new { name = "Fiyatlı", unitPrice = 100m, purchasePrice = 99m });
        (await admin.GetJsonAsync($"{PurchaseOrdersPath}/{order.Id()}")).GetProperty("lines")[0].Dec("unitPrice").ShouldBe(60.5m);
    }

    [Fact]
    public async Task PurchaseOrder_StateMachine_WithEventsAndTransitionErrors()
    {
        var org = await factory.NewOrgAsync("PO Durum");
        var admin = org.Admin;
        var vendor = await admin.CreateVendorAsync("Acme");
        var empty = await admin.PostJsonAsync(PurchaseOrdersPath, new { subject = "Boş", vendorId = vendor.Id() });
        var order = await admin.CreatePurchaseOrderAsync(vendor.Id());
        var url = $"{PurchaseOrdersPath}/{order.Id()}";

        await (await admin.ActAsync($"{PurchaseOrdersPath}/{empty.Id()}/confirm")).ReadProblemAsync(HttpStatusCode.UnprocessableEntity, "purchase_order.no_lines");
        await (await admin.ActAsync($"{url}/receive")).ReadProblemAsync(HttpStatusCode.Conflict, "purchase_order.invalid_transition");
        await admin.ActAsync($"{url}/confirm").ShouldBeNoContentAsync();
        var confirmed = await admin.GetJsonAsync(url);
        confirmed.Str("status").ShouldBe("confirmed");
        confirmed.TryGetProperty("confirmedAt", out _).ShouldBeTrue();
        var problem = await (await admin.ActAsync($"{url}/confirm")).ReadProblemAsync(HttpStatusCode.Conflict, "purchase_order.invalid_transition");
        problem.GetProperty("args").GetProperty("from").GetString().ShouldBe("confirmed");
        await (await admin.PutAsJsonAsync(url, new { subject = "x", vendorId = vendor.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "purchase_order.not_editable");
        await (await admin.DeleteAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "purchase_order.not_editable");

        await admin.ActAsync($"{url}/receive").ShouldBeNoContentAsync();
        var received = await admin.GetJsonAsync(url);
        received.Str("status").ShouldBe("received");
        received.TryGetProperty("receivedAt", out _).ShouldBeTrue();
        (await factory.OutboxCountAsync(org.TenantId, "PurchaseOrderReceived")).ShouldBe(1);
        foreach (var action in new[] { "confirm", "receive", "cancel" })
        {
            await (await admin.ActAsync($"{url}/{action}", new { })).ReadProblemAsync(HttpStatusCode.Conflict, "purchase_order.invalid_transition");
        }

        var draft = await admin.CreatePurchaseOrderAsync(vendor.Id());
        await admin.ActAsync($"{PurchaseOrdersPath}/{draft.Id()}/cancel", new { reason = "  vazgeçildi " }).ShouldBeNoContentAsync();
        var cancelled = await admin.GetJsonAsync($"{PurchaseOrdersPath}/{draft.Id()}");
        (cancelled.Str("status"), cancelled.Str("cancelReason")).ShouldBe(("cancelled", "vazgeçildi"));
        var confirmedThenCancelled = await admin.CreatePurchaseOrderAsync(vendor.Id());
        await admin.ActAsync($"{PurchaseOrdersPath}/{confirmedThenCancelled.Id()}/confirm").ShouldBeNoContentAsync();
        await admin.ActAsync($"{PurchaseOrdersPath}/{confirmedThenCancelled.Id()}/cancel").ShouldBeNoContentAsync();
        await (await admin.PostAsJsonAsync($"{url}/cancel", new { reason = new string('x', 1001) }, Ct)).ShouldBeValidationErrorAsync("reason");
        await (await admin.ActAsync($"{PurchaseOrdersPath}/{Guid.NewGuid()}/confirm")).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task PurchaseOrder_Update_IsAFullReplacement_OnlyForDrafts_AndDeleteIsSoft()
    {
        var admin = (await factory.NewOrgAsync("PO Güncelle")).Admin;
        var vendor = await admin.CreateVendorAsync("Acme");
        var other = await admin.CreateVendorAsync("Diğer");
        var order = await admin.CreatePurchaseOrderAsync(vendor.Id(), new { carrier = "MNG", exciseTax = 5m, adjustment = 1m });
        var url = $"{PurchaseOrdersPath}/{order.Id()}";

        await admin.PutJsonAsync(url, new { subject = "Yeni", vendorId = other.Id(), lines = new[] { Line(1m, 30m, 0m, 0m) } });

        var replaced = await admin.GetJsonAsync(url);
        replaced.Str("subject").ShouldBe("Yeni");
        replaced.GetProperty("vendorId").GetGuid().ShouldBe(other.Id());
        replaced.Str("number").ShouldBe(order.Str("number"));
        replaced.Dec("grandTotal").ShouldBe(30m);
        replaced.Dec("adjustment").ShouldBe(0m);
        replaced.TryGetProperty("carrier", out _).ShouldBeFalse();
        replaced.TryGetProperty("exciseTax", out _).ShouldBeFalse();
        replaced.Str("poDate").ShouldBe(TenantToday().DateString(), "poDate verilmezse mevcut korunur");

        await admin.DeleteJsonAsync(url);
        await (await admin.GetAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.CreatePurchaseOrderAsync(vendor.Id())).Str("number").ShouldBe($"PO-{TenantYear()}-0002", "silinen numara yeniden kullanılmaz");
    }

    [Fact]
    public async Task PurchaseOrder_List_FiltersAndSorts()
    {
        var admin = (await factory.NewOrgAsync("PO Liste")).Admin;
        var acme = await admin.CreateVendorAsync("Acme");
        var beta = await admin.CreateVendorAsync("Beta");
        var today = TenantToday();
        var a = await admin.CreatePurchaseOrderAsync(acme.Id(), new { subject = "Alfa %", poDate = today.AddDays(-3).DateString(), dueDate = today.AddDays(10).DateString() }, Line(1m, 300m, 0m, 0m));
        var b = await admin.CreatePurchaseOrderAsync(beta.Id(), new { subject = "Beta", poDate = today.AddDays(-2).DateString() }, Line(1m, 100m, 0m, 0m));
        var c = await admin.CreatePurchaseOrderAsync(acme.Id(), new { subject = "Gama_x", poDate = today.AddDays(-1).DateString(), dueDate = today.AddDays(5).DateString() }, Line(1m, 200m, 0m, 0m));
        await admin.ActAsync($"{PurchaseOrdersPath}/{a.Id()}/confirm").ShouldBeNoContentAsync();

        (await admin.ListIdsAsync(PurchaseOrdersPath, $"?vendorId={acme.Id()}&sort=number")).ShouldBe([a.Id(), c.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, "?status=confirmed")).ShouldBe([a.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, "?status=draft&sort=number")).ShouldBe([b.Id(), c.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, $"?q={b.Str("number")}")).ShouldBe([b.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, "?q=%25")).ShouldBe([a.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, "?q=_")).ShouldBe([c.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, $"?poFrom={today.AddDays(-2).DateString()}&poTo={today.AddDays(-1).DateString()}&sort=number")).ShouldBe([b.Id(), c.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, "?sort=dueDate")).ShouldBe([c.Id(), a.Id(), b.Id()], "boş vade sonda");
        (await admin.ListIdsAsync(PurchaseOrdersPath, "?sort=-dueDate")).ShouldBe([a.Id(), c.Id(), b.Id()]);
        (await admin.ListIdsAsync(PurchaseOrdersPath, "?sort=grandTotal")).ShouldBe([b.Id(), c.Id(), a.Id()]);
        var summary = (await admin.GetJsonAsync($"{PurchaseOrdersPath}?q=Beta")).GetProperty("items")[0];
        (summary.Str("vendorName"), summary.Dec("grandTotal"), summary.Str("status")).ShouldBe(("Beta", 100m, "draft"));
    }

    [Fact]
    public async Task PurchaseOrder_Numbers_AreGaplessUnderConcurrency_AndAFailedSaveRollsTheCounterBack()
    {
        var clock = new TestClock();
        await using var derived = factory.Derive(clock);
        var org = await NewOrgAsync(derived.CreateClient, "PO Numara");
        var vendor = await org.Admin.CreateVendorAsync("Acme");
        var year = TenantYear();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            org.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = $"Paralel {i}", vendorId = vendor.Id(), lines = new[] { Line() } }, Ct)));
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        (await org.Admin.ListNumbersAsync(PurchaseOrdersPath, "?sort=number")).ShouldBe(Enumerable.Range(1, 10).Select(n => $"PO-{year}-{n:D4}"));

        Faults.Mode = Faults.FailOnPurchaseOrderInsert;
        try
        {
            (await org.Admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "Patlayan", vendorId = vendor.Id(), lines = new[] { Line() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            Faults.Mode = null;
        }

        (await factory.CounterAsync(org.TenantId, "purchaseOrder", year)).ShouldBe(10);
        (await org.Admin.CreatePurchaseOrderAsync(vendor.Id())).Str("number").ShouldBe($"PO-{year}-0011");
        clock.Override = new DateTimeOffset(2026, 12, 31, 21, 30, 0, TimeSpan.Zero);
        (await org.Admin.CreatePurchaseOrderAsync(vendor.Id())).Str("number").ShouldBe("PO-2027-0001", "yıl kiracı saat diliminde");
        clock.Override = null;
    }

    [Fact]
    public async Task ConcurrentVendorDeleteAndPurchaseOrderCreate_NeverLeaveALiveOrderOnADeletedVendor()
    {
        var org = await factory.NewOrgAsync("PO Tedarikçi Yarış");
        var admin = org.Admin;

        for (var round = 0; round < 6; round++)
        {
            var vendor = await admin.CreateVendorAsync($"Yarış {round}");

            var responses = await Task.WhenAll(
                admin.DeleteAsync($"{VendorsPath}/{vendor.Id()}", Ct),
                admin.PostAsJsonAsync(PurchaseOrdersPath, new { subject = "x", vendorId = vendor.Id(), lines = new[] { Line() } }, Ct));

            var vendorDeleted = responses[0].StatusCode == HttpStatusCode.NoContent;
            var orderCreated = responses[1].StatusCode == HttpStatusCode.Created;
            (vendorDeleted && orderCreated).ShouldBeFalse($"tur {round}: silinen tedarikçiye canlı PO bağlanamaz");
            (vendorDeleted || orderCreated).ShouldBeTrue($"tur {round}: biri kazanır");
            if (!vendorDeleted)
            {
                await responses[0].ReadProblemAsync(HttpStatusCode.Conflict, "vendor.in_use");
            }
            else
            {
                await responses[1].ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
            }
        }
    }

    [Fact]
    public async Task Report_PurchaseOrderSection_AndCurrencies()
    {
        var admin = (await factory.NewOrgAsync("PO Rapor")).Admin;
        var vendor = await admin.CreateVendorAsync("Acme");
        var account = await admin.CreateAccountAsync("Firma");
        var draft = await admin.CreatePurchaseOrderAsync(vendor.Id(), null, Line(1m, 200m, 0m, 0m));
        var confirmed = await admin.CreatePurchaseOrderAsync(vendor.Id(), null, Line(1m, 1000m, 0m, 0m));
        var received = await admin.CreatePurchaseOrderAsync(vendor.Id(), null, Line(1m, 1000m, 0m, 0m));
        var cancelled = await admin.CreatePurchaseOrderAsync(vendor.Id(), null, Line(1m, 5000m, 0m, 0m));
        await admin.CreatePurchaseOrderAsync(vendor.Id(), new { currency = "EUR" }, Line(1m, 30m, 0m, 0m));
        foreach (var order in new[] { confirmed, received })
        {
            await admin.ActAsync($"{PurchaseOrdersPath}/{order.Id()}/confirm").ShouldBeNoContentAsync();
        }

        await admin.ActAsync($"{PurchaseOrdersPath}/{received.Id()}/receive").ShouldBeNoContentAsync();
        await admin.ActAsync($"{PurchaseOrdersPath}/{cancelled.Id()}/cancel").ShouldBeNoContentAsync();
        _ = draft;
        _ = account;

        var report = await admin.GetJsonAsync($"{Base}/reports/commerce/summary");

        var section = report.GetProperty("purchaseOrders");
        section.GetProperty("byStatus").EnumerateArray().Select(r => r.Str("status")).ShouldBe(["draft", "confirmed", "received", "cancelled"]);
        (int, decimal) Row(string status) => (section.GetProperty("byStatus").EnumerateArray().Single(r => r.Str("status") == status).GetProperty("count").GetInt32(), section.GetProperty("byStatus").EnumerateArray().Single(r => r.Str("status") == status).Dec("amount"));
        Row("draft").ShouldBe((2, 230m));
        Row("confirmed").ShouldBe((1, 1000m));
        Row("received").ShouldBe((1, 1000m));
        Row("cancelled").ShouldBe((1, 5000m));
        section.GetProperty("totalCount").GetInt32().ShouldBe(4, "iptaller hariç");
        section.Dec("totalAmount").ShouldBe(2230m);
        report.GetProperty("currencies").EnumerateArray().Select(c => c.GetString()).ShouldBe(["EUR", "TRY"]);
        report.GetProperty("invoices").GetProperty("totalCount").GetInt32().ShouldBe(0);
    }
}
