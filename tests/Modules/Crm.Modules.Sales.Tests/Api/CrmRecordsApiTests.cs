using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Sales.Tests.Api.SalesApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Sales.Tests.Api;

/// <summary>Firma, kişi ve potansiyel müşteri (lead): CRUD, doğrulama, sahip kuralı, liste (arama/filtre/sıralama/sayfalama), yetki.</summary>
[Collection(ApiCollection.Name)]
public sealed class CrmRecordsApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Account_Crud_RoundTrip_WithLocationHeaderAndDetailCounts()
    {
        var org = await factory.NewOrgAsync("Acc Crud");
        var admin = org.Admin;

        var response = await admin.PostAsJsonAsync($"{Base}/accounts", new
        {
            name = "  Acme A.Ş. ",
            industry = "Yazılım",
            email = "Info@Acme.com",
            phone = "0212 000 00 00",
            billingAddress = new { city = "İstanbul", country = "TR" },
            description = "Test firması",
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.Location.ShouldNotBeNull();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = created.Id();

        created.GetProperty("name").GetString().ShouldBe("Acme A.Ş.");
        created.GetProperty("email").GetString().ShouldBe("info@acme.com");
        created.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        created.GetProperty("ownerName").GetString().ShouldBe(org.AdminName);
        created.GetProperty("billingAddress").GetProperty("city").GetString().ShouldBe("İstanbul");
        created.GetProperty("billingAddress").TryGetProperty("street", out _).ShouldBeFalse("null alanlar yazılmaz");
        created.TryGetProperty("website", out _).ShouldBeFalse();
        created.TryGetProperty("updatedAt", out _).ShouldBeFalse();
        created.GetProperty("createdAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        var detail = await admin.GetJsonAsync($"{Base}/accounts/{id}");
        detail.GetProperty("contactCount").GetInt32().ShouldBe(0);
        detail.GetProperty("dealCount").GetInt32().ShouldBe(0);

        await admin.PutJsonAsync($"{Base}/accounts/{id}", new { name = "Acme Holding", industry = "Finans", website = "https://acme.example" });
        var updated = await admin.GetJsonAsync($"{Base}/accounts/{id}");
        updated.GetProperty("name").GetString().ShouldBe("Acme Holding");
        updated.GetProperty("industry").GetString().ShouldBe("Finans");
        updated.TryGetProperty("email", out _).ShouldBeFalse("PUT tam değiştirmedir: gönderilmeyen alan temizlenir");
        updated.GetProperty("updatedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        updated.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId, "ownerUserId verilmezse mevcut sahip korunur");

        var list = await admin.GetJsonAsync($"{Base}/accounts");
        list.GetProperty("items").EnumerateArray().Single().TryGetProperty("contactCount", out _).ShouldBeFalse("sayılar yalnız detayda");

        await admin.DeleteJsonAsync($"{Base}/accounts/{id}");
        await (await admin.GetAsync($"{Base}/accounts/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        await (await admin.DeleteAsync($"{Base}/accounts/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Account_Validation_ReturnsFieldErrors()
    {
        var admin = (await factory.NewOrgAsync("Acc Validation")).Admin;

        await (await admin.PostAsJsonAsync($"{Base}/accounts", new { name = "  " }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync($"{Base}/accounts", new { name = "A", email = "not-an-email" }, Ct)).ShouldBeValidationErrorAsync("email");
        await (await admin.PostAsJsonAsync($"{Base}/accounts", new { name = new string('x', 201) }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync($"{Base}/accounts", new { name = "A", billingAddress = new { city = new string('c', 101) } }, Ct))
            .ShouldBeValidationErrorAsync("billingAddress.city");
    }

    [Fact]
    public async Task Account_WithDependents_CannotBeDeleted_UntilTheyAreRemoved()
    {
        var admin = (await factory.NewOrgAsync("Acc Dependents")).Admin;
        var account = await admin.CreateAccountAsync("Bağlı Firma");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = account.Id() });

        await (await admin.DeleteAsync($"{Base}/accounts/{account.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "account.has_dependents");
        (await admin.GetJsonAsync($"{Base}/accounts/{account.Id()}")).GetProperty("contactCount").GetInt32().ShouldBe(1);

        var related = await admin.GetJsonAsync($"{Base}/accounts/{account.Id()}/contacts");
        related.EnumerateArray().Single().Id().ShouldBe(contact.Id());
        (await admin.GetJsonAsync($"{Base}/accounts/{account.Id()}/deals")).GetArrayLength().ShouldBe(0);

        await admin.DeleteJsonAsync($"{Base}/contacts/{contact.Id()}");
        await admin.DeleteJsonAsync($"{Base}/accounts/{account.Id()}");
    }

    [Fact]
    public async Task Contact_Crud_LinksToAccount_AndFiltersByIt()
    {
        var admin = (await factory.NewOrgAsync("Contact Crud")).Admin;
        var acme = await admin.CreateAccountAsync("Acme");
        var beta = await admin.CreateAccountAsync("Beta");

        var created = await admin.PostJsonAsync($"{Base}/contacts", new
        {
            firstName = "Ayşe",
            lastName = "Yılmaz",
            email = "AYSE@acme.com",
            mobile = "0532 111 22 33",
            title = "Satın alma",
            accountId = acme.Id(),
            mailingAddress = new { city = "Ankara" },
        });
        var id = created.Id();
        created.GetProperty("fullName").GetString().ShouldBe("Ayşe Yılmaz");
        created.GetProperty("accountName").GetString().ShouldBe("Acme");
        created.GetProperty("email").GetString().ShouldBe("ayse@acme.com");
        created.GetProperty("mailingAddress").GetProperty("city").GetString().ShouldBe("Ankara");

        await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Demir", firstName = "Ali", accountId = beta.Id() });
        await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Solo" });

        (await admin.GetJsonAsync($"{Base}/contacts")).GetProperty("totalCount").GetInt32().ShouldBe(3);
        var byAccount = await admin.GetJsonAsync($"{Base}/contacts?accountId={acme.Id()}");
        byAccount.GetProperty("items").EnumerateArray().Single().Id().ShouldBe(id);

        var bySearch = await admin.GetJsonAsync($"{Base}/contacts?q=ali%20dem");
        bySearch.GetProperty("items").EnumerateArray().Single().GetProperty("fullName").GetString().ShouldBe("Ali Demir");

        await admin.PutJsonAsync($"{Base}/contacts/{id}", new { firstName = "Ayşe", lastName = "Yılmaz-Kara", accountId = beta.Id() });
        var updated = await admin.GetJsonAsync($"{Base}/contacts/{id}");
        updated.GetProperty("lastName").GetString().ShouldBe("Yılmaz-Kara");
        updated.GetProperty("accountName").GetString().ShouldBe("Beta");

        await (await admin.PostAsJsonAsync($"{Base}/contacts", new { lastName = "" }, Ct)).ShouldBeValidationErrorAsync("lastName");
        await (await admin.PostAsJsonAsync($"{Base}/contacts", new { lastName = "X", accountId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        await admin.DeleteJsonAsync($"{Base}/contacts/{id}");
        await (await admin.GetAsync($"{Base}/contacts/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Lead_Crud_DefaultsAndStatusRules()
    {
        var admin = (await factory.NewOrgAsync("Lead Crud")).Admin;

        var lead = await admin.CreateLeadAsync("Şahin", "Acme", new { FirstName = "Mert", Email = "mert@acme.com" });
        var id = lead.Id();
        lead.GetProperty("status").GetString().ShouldBe("new");
        lead.GetProperty("source").GetString().ShouldBe("other");
        lead.GetProperty("fullName").GetString().ShouldBe("Mert Şahin");
        lead.GetProperty("company").GetString().ShouldBe("Acme");
        lead.TryGetProperty("rating", out _).ShouldBeFalse();
        lead.TryGetProperty("convertedAt", out _).ShouldBeFalse();

        await admin.PutJsonAsync($"{Base}/leads/{id}", new { firstName = "Mert", lastName = "Şahin", company = "Acme Ltd", source = "referral", status = "qualified", rating = "hot" });
        var updated = await admin.GetJsonAsync($"{Base}/leads/{id}");
        (updated.GetProperty("status").GetString(), updated.GetProperty("source").GetString(), updated.GetProperty("rating").GetString(), updated.GetProperty("company").GetString())
            .ShouldBe(("qualified", "referral", "hot", "Acme Ltd"));

        await (await admin.PutAsJsonAsync($"{Base}/leads/{id}", new { lastName = "Şahin", company = "Acme", status = "converted" }, Ct)).ShouldBeValidationErrorAsync("status");
        await (await admin.PostAsJsonAsync($"{Base}/leads", new { lastName = "X" }, Ct)).ShouldBeValidationErrorAsync("company");
        await (await admin.PostAsJsonAsync($"{Base}/leads", new { lastName = "X", company = "Y", source = "smoke-signal" }, Ct)).ShouldBeValidationErrorAsync("source");

        await admin.DeleteJsonAsync($"{Base}/leads/{id}");
        await (await admin.GetAsync($"{Base}/leads/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Lists_SupportPaging_Sorting_Search_AndFilters()
    {
        var org = await factory.NewOrgAsync("Lists Org");
        var admin = org.Admin;
        foreach (var (name, industry) in new[] { ("Alfa", "Software"), ("Bravo", "Food"), ("Charlie", "Software"), ("Delta", "Food"), ("Echo", "Software") })
        {
            await admin.CreateAccountAsync(name, new { Industry = industry });
        }

        var page2 = await admin.GetJsonAsync($"{Base}/accounts?pageSize=2&page=2&sort=name");
        page2.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ShouldBe(["Charlie", "Delta"]);
        (page2.GetProperty("page").GetInt32(), page2.GetProperty("pageSize").GetInt32(), page2.GetProperty("totalCount").GetInt32()).ShouldBe((2, 2, 5));

        var descending = await admin.GetJsonAsync($"{Base}/accounts?sort=-name&pageSize=1");
        descending.GetProperty("items").EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("Echo");

        var byIndustry = await admin.GetJsonAsync($"{Base}/accounts?industry=food&sort=name");
        byIndustry.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ShouldBe(["Bravo", "Delta"]);
        (await admin.GetJsonAsync($"{Base}/accounts?q=brav")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync($"{Base}/accounts?ownerUserId={org.AdminUserId}")).GetProperty("totalCount").GetInt32().ShouldBe(5);
        (await admin.GetJsonAsync($"{Base}/accounts?ownerUserId={Guid.NewGuid()}")).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // Arama parametrelidir ve joker karakterler kaçışlanır: "%" ve "_" düz metin olarak aranır.
        (await admin.GetJsonAsync($"{Base}/accounts?q=%25")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await admin.GetJsonAsync($"{Base}/accounts?q=_ravo")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await admin.GetJsonAsync($"{Base}/accounts?q=x'%3BDROP%20TABLE%20sales.accounts%3B--")).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // Beyaz liste dışı sıralama alanı yok sayılır (hata değil); sayfa boyutu üst sınırı 100.
        (await admin.GetJsonAsync($"{Base}/accounts?sort=password_hash,-secret")).GetProperty("totalCount").GetInt32().ShouldBe(5);
        (await admin.GetJsonAsync($"{Base}/accounts?pageSize=1000")).GetProperty("pageSize").GetInt32().ShouldBe(100);
        (await admin.GetJsonAsync($"{Base}/accounts")).GetProperty("pageSize").GetInt32().ShouldBe(25);

        await admin.CreateLeadAsync("Bir", "Web Co", new { Source = "web" });
        await admin.CreateLeadAsync("Iki", "Ref Co", new { Source = "referral" });
        var third = await admin.CreateLeadAsync("Uc", "Aaa Co");
        (await admin.GetJsonAsync($"{Base}/leads?source=referral")).GetProperty("items").EnumerateArray().Single().GetProperty("company").GetString().ShouldBe("Ref Co");
        (await admin.GetJsonAsync($"{Base}/leads?status=new")).GetProperty("totalCount").GetInt32().ShouldBe(3);
        (await admin.GetJsonAsync($"{Base}/leads?status=qualified")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        await admin.PutJsonAsync($"{Base}/leads/{third.Id()}", new { lastName = "Uc", company = "Aaa Co", status = "contacted" });
        (await admin.GetJsonAsync($"{Base}/leads?status=contacted")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync($"{Base}/leads?sort=company&pageSize=1")).GetProperty("items").EnumerateArray().Single().GetProperty("company").GetString().ShouldBe("Aaa Co");
        (await admin.GetJsonAsync($"{Base}/leads?q=ref%20co")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await admin.GetAsync($"{Base}/leads?status=bogus", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Owner_MustBeAnActiveMemberOfTheOrganization()
    {
        var org = await factory.NewOrgAsync("Owner Org");
        var other = await factory.NewOrgAsync("Owner Other Org");
        var (memberClient, memberId) = await factory.AddMemberAsync(org, "Satış Temsilcisi", "crm.accounts.read", "crm.accounts.write");

        await (await org.Admin.PostAsJsonAsync($"{Base}/accounts", new { name = "A", ownerUserId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await org.Admin.PostAsJsonAsync($"{Base}/accounts", new { name = "A", ownerUserId = other.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await org.Admin.PostAsJsonAsync($"{Base}/leads", new { lastName = "A", company = "B", ownerUserId = other.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");

        var assigned = await org.Admin.CreateAccountAsync("Atanmış", new { OwnerUserId = memberId });
        assigned.GetProperty("ownerUserId").GetGuid().ShouldBe(memberId);
        assigned.GetProperty("ownerName").GetString().ShouldBe("Satış Temsilcisi");

        // Sahip verilmezse çağıran kullanıcı.
        var own = await memberClient.CreateAccountAsync("Kendi kaydım");
        own.GetProperty("ownerUserId").GetGuid().ShouldBe(memberId);

        // Pasifleşen üyeye yeni atama yapılamaz; mevcut sahibi olan kayıt ise sahibi korunarak düzenlenebilir.
        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{memberId}", new { isActive = false }, HttpStatusCode.NoContent);
        await (await org.Admin.PostAsJsonAsync($"{Base}/accounts", new { name = "B", ownerUserId = memberId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await org.Admin.PutJsonAsync($"{Base}/accounts/{assigned.Id()}", new { name = "Atanmış (düzenlendi)", ownerUserId = memberId });
        (await org.Admin.GetJsonAsync($"{Base}/accounts/{assigned.Id()}")).GetProperty("ownerName").GetString().ShouldBe("Satış Temsilcisi");
        await (await org.Admin.PutAsJsonAsync($"{Base}/accounts/{assigned.Id()}", new { name = "X", ownerUserId = other.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
    }

    [Fact]
    public async Task UserWithoutWritePermission_GetsForbidden_ButCanRead()
    {
        var org = await factory.NewOrgAsync("Rbac Sales Org");
        var account = await org.Admin.CreateAccountAsync("Okunabilir");
        var (reader, _) = await factory.AddMemberAsync(org, "Salt Okur", "crm.accounts.read", "crm.contacts.read");

        (await reader.GetJsonAsync($"{Base}/accounts")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await reader.GetJsonAsync($"{Base}/accounts/{account.Id()}")).GetProperty("name").GetString().ShouldBe("Okunabilir");

        await (await reader.PostAsJsonAsync($"{Base}/accounts", new { name = "Yeni" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PutAsJsonAsync($"{Base}/accounts/{account.Id()}", new { name = "Değişti" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.DeleteAsync($"{Base}/accounts/{account.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{Base}/contacts", new { lastName = "Yeni" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Hiç okuma izni olmayan kaynaklar.
        await (await reader.GetAsync($"{Base}/leads", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.GetAsync($"{Base}/deals", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.GetAsync($"{Base}/deals/board", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.GetAsync($"{Base}/pipelines", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.GetAsync($"{Base}/accounts/{account.Id()}/deals", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Kimliksiz istek.
        await (await factory.CreateClient().GetAsync($"{Base}/accounts", Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");

        // Değişmedi.
        (await org.Admin.GetJsonAsync($"{Base}/accounts/{account.Id()}")).GetProperty("name").GetString().ShouldBe("Okunabilir");
    }

    [Fact]
    public async Task StandardRole_HasCrmAccess_ButCannotManagePipelines()
    {
        var org = await factory.NewOrgAsync("Standard Org");
        var roles = await org.Admin.GetJsonAsync($"{Base}/organization/roles");
        var standardId = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Standard").Id();
        var standard = (await ApiTestClient.AddMemberAsync(factory, org.Admin, "Std", standardId)).Client;

        (await standard.CreateAccountAsync("Standart kullanıcı firması")).GetProperty("name").GetString().ShouldBe("Standart kullanıcı firması");
        var pipeline = await standard.DefaultPipelineAsync();

        await (await standard.PostAsJsonAsync($"{Base}/pipelines", new { name = "Yeni" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await standard.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}", new { name = "Yeni", isDefault = true }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var stages = pipeline.GetProperty("stages").EnumerateArray().Select(s => new { id = s.Id(), name = s.GetProperty("name").GetString(), probability = s.GetProperty("probability").GetInt32(), kind = s.GetProperty("kind").GetString() }).ToList();
        await (await standard.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task PermissionCatalog_KeepsTheContractKeys()
    {
        var admin = (await factory.NewOrgAsync("Catalog Sales Org")).Admin;

        var keys = (await admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => p.GetProperty("key").GetString()!).ToList();

        keys.ShouldContain("crm.accounts.read");
        keys.ShouldContain("crm.deals.write");
        keys.ShouldContain("crm.activities.read");
        keys.ShouldContain("crm.reports.read");
        keys.Distinct().Count().ShouldBe(keys.Count);
        keys.ShouldContain("crm.campaigns.read");
        keys.ShouldContain("crm.campaigns.write");
        keys.ShouldContain("org.workflows.manage");
        keys.ShouldContain("crm.approvals.decide");

        // Administrator tüm anahtarlara sahip.
        var me = await admin.GetJsonAsync($"{Base}/me");
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).OrderBy(k => k, StringComparer.Ordinal).ShouldBe(keys.OrderBy(k => k, StringComparer.Ordinal));
    }
}
