using System.Net;
using System.Net.Http.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Service.Tests.Api;

/// <summary>Talepler: oluşturma, tam değiştirme, silme, firma/kişi/atanan kuralları, doğrulama, zaman çizelgesi.</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseCrudApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Create_AppliesDefaults_ReturnsTheDetailShape_AndTheFirstNumber()
    {
        var org = await factory.NewOrgAsync("Case Create");
        var admin = org.Admin;

        var response = await admin.PostAsJsonAsync(CasesPath, new { subject = "  Fatura hatalı  ", description = "  Tutar yanlış  " }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        response.Headers.Location!.ToString().ShouldEndWith($"/cases/{System.Text.Json.JsonDocument.Parse(body).RootElement.Id()}");
        var created = System.Text.Json.JsonDocument.Parse(body).RootElement;

        created.Str("number").ShouldMatch(@"^C-\d{4}-0001$");
        created.Str("subject").ShouldBe("Fatura hatalı");
        created.Str("description").ShouldBe("Tutar yanlış");
        (created.Str("status"), created.Str("priority"), created.Str("channel")).ShouldBe(("new", "normal", "other"));
        created.Has("assignedUserId").ShouldBeFalse("verilmezse atanmamış (temsilci kuyruğu)");
        created.Has("accountId").ShouldBeFalse();
        created.Has("firstResponseAt").ShouldBeFalse();
        created.Has("resolvedAt").ShouldBeFalse();
        created.Has("closedAt").ShouldBeFalse();
        created.Has("resolutionNote").ShouldBeFalse();
        created.Has("updatedAt").ShouldBeFalse();
        created.GetProperty("reopenCount").GetInt32().ShouldBe(0);
        created.GetProperty("isSlaBreached").GetBoolean().ShouldBeFalse();
        created.GetProperty("firstResponseBreached").GetBoolean().ShouldBeFalse();
        created.GetProperty("resolutionBreached").GetBoolean().ShouldBeFalse();
        created.Str("slaState").ShouldBe("ok");
        created.GetProperty("createdByUserId").GetGuid().ShouldBe(org.AdminUserId);
        created.Str("createdByName").ShouldBe(org.AdminName);

        var createdAt = created.Utc("createdAt");
        created.Utc("firstResponseDueAt").ShouldBe(createdAt.AddMinutes(480), TimeSpan.FromSeconds(1));
        created.Utc("dueAt").ShouldBe(createdAt.AddMinutes(4320), TimeSpan.FromSeconds(1));

        var second = await admin.CreateCaseAsync("İkinci talep", new { priority = "urgent", channel = "phone" });
        second.Str("number").ShouldEndWith("-0002");
        (second.Str("priority"), second.Str("channel")).ShouldBe(("urgent", "phone"));
        second.Utc("dueAt").ShouldBe(second.Utc("createdAt").AddMinutes(240), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Create_AssignsTheGivenActiveMember_AndRejectsNonMembers()
    {
        var org = await factory.NewOrgAsync("Case Assign Create");
        var other = await factory.NewOrgAsync("Case Assign Create Other");
        var (_, memberId) = await factory.AddMemberAsync(org, "Mert Kaya", "crm.cases.read");

        var created = await org.Admin.CreateCaseAsync("Atanmış", new { assignedUserId = memberId });

        created.GetProperty("assignedUserId").GetGuid().ShouldBe(memberId);
        created.Str("assignedUserName").ShouldBe("Mert Kaya");

        await (await org.Admin.PostAsJsonAsync(CasesPath, new { subject = "Yabancı", assignedUserId = other.AdminUserId }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await org.Admin.PostAsJsonAsync(CasesPath, new { subject = "Rastgele", assignedUserId = Guid.NewGuid() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
    }

    [Fact]
    public async Task Create_Validation_RejectsBadInput_WithFieldErrors()
    {
        var admin = (await factory.NewOrgAsync("Case Validation")).Admin;

        await (await admin.PostAsJsonAsync(CasesPath, new { description = "konu yok" }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "" }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "   " }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = new string('x', 201) }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", description = new string('x', 8001) }, Ct)).ShouldBeValidationErrorAsync("description");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", accountId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("accountId");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", contactId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("contactId");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", assignedUserId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("assignedUserId");
        (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", priority = "bogus" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(CasesPath, new { subject = "x", channel = "smoke" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Geçersiz istekler numara tüketmedi.
        (await admin.CreateCaseAsync("İlk geçerli")).Str("number").ShouldEndWith("-0001");
    }

    [Fact]
    public async Task Create_DerivesTheAccountFromTheContact_AndChecksMismatchAndExistence()
    {
        var org = await factory.NewOrgAsync("Case Links");
        var other = await factory.NewOrgAsync("Case Links Other");
        var admin = org.Admin;
        var acme = await admin.CreateAccountAsync("Acme A.Ş.");
        var globex = await admin.CreateAccountAsync("Globex");
        var ayse = await admin.CreateContactAsync("Yılmaz", acme.Id());
        var loner = await admin.CreateContactAsync("Firmasız");

        var derived = await admin.CreateCaseAsync("Türetilen firma", new { contactId = ayse.Id() });
        derived.GetProperty("accountId").GetGuid().ShouldBe(acme.Id());
        derived.Str("accountName").ShouldBe("Acme A.Ş.");
        derived.GetProperty("contactId").GetGuid().ShouldBe(ayse.Id());
        derived.Str("contactName").ShouldBe("Test Yılmaz");

        var same = await admin.CreateCaseAsync("Aynı firma", new { contactId = ayse.Id(), accountId = acme.Id() });
        same.GetProperty("accountId").GetGuid().ShouldBe(acme.Id());

        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "Uyuşmaz", contactId = ayse.Id(), accountId = globex.Id() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "case.contact_account_mismatch");

        var lonerWithAccount = await admin.CreateCaseAsync("Firmasız kişi + firma", new { contactId = loner.Id(), accountId = globex.Id() });
        lonerWithAccount.GetProperty("accountId").GetGuid().ShouldBe(globex.Id());
        var lonerOnly = await admin.CreateCaseAsync("Firmasız kişi", new { contactId = loner.Id() });
        lonerOnly.Has("accountId").ShouldBeFalse();

        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "Yok", accountId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.account_not_found");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "Yok", contactId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.contact_not_found");

        var foreignAccount = await other.Admin.CreateAccountAsync("B Firması");
        var foreignContact = await other.Admin.CreateContactAsync("Yabancı");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "Çapraz", accountId = foreignAccount.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.account_not_found");
        await (await admin.PostAsJsonAsync(CasesPath, new { subject = "Çapraz", contactId = foreignContact.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.contact_not_found");
    }

    [Fact]
    public async Task Put_IsAFullReplace_KeepsChannel_AndRevalidatesLinksOnlyWhenTheyChange()
    {
        var org = await factory.NewOrgAsync("Case Put");
        var admin = org.Admin;
        var acme = await admin.CreateAccountAsync("Acme");
        var globex = await admin.CreateAccountAsync("Globex");
        var ayse = await admin.CreateContactAsync("Yılmaz", acme.Id());
        var created = await admin.CreateCaseAsync("İlk konu", new { description = "ilk", contactId = ayse.Id(), priority = "high", channel = "email" });
        var id = created.Id();

        await admin.PutJsonAsync($"{CasesPath}/{id}", new { subject = "Yeni konu" });
        var replaced = await admin.GetCaseAsync(id);
        replaced.Str("subject").ShouldBe("Yeni konu");
        replaced.Has("description").ShouldBeFalse("gönderilmeyen isteğe bağlı alan temizlenir");
        replaced.Has("accountId").ShouldBeFalse();
        replaced.Has("contactId").ShouldBeFalse();
        replaced.Str("channel").ShouldBe("email", "channel verilmezse korunur");
        (replaced.Str("priority"), replaced.Str("status")).ShouldBe(("high", "new"));
        replaced.Has("updatedAt").ShouldBeTrue();
        replaced.Utc("dueAt").ShouldBe(created.Utc("dueAt"), "SLA'ya dokunmaz");

        // Kişi değişince firma (yalnız accountId yoksa) türetilir.
        await admin.PutJsonAsync($"{CasesPath}/{id}", new { subject = "Kişili", contactId = ayse.Id(), channel = "phone" });
        var withContact = await admin.GetCaseAsync(id);
        (withContact.GetProperty("accountId").GetGuid(), withContact.Str("channel")).ShouldBe((acme.Id(), "phone"));

        // Firma kişiyle uyuşmuyorsa reddedilir; bağ değişmez.
        await (await admin.PutAsJsonAsync($"{CasesPath}/{id}", new { subject = "Uyuşmaz", contactId = ayse.Id(), accountId = globex.Id() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "case.contact_account_mismatch");
        (await admin.GetCaseAsync(id)).GetProperty("accountId").GetGuid().ShouldBe(acme.Id());

        // Bağlı kayıt sonradan silinirse talep düzenlenebilir kalır (bağ yeniden doğrulanmaz), adlar boş döner.
        await admin.DeleteJsonAsync($"{Base}/contacts/{ayse.Id()}");
        var orphan = await admin.GetCaseAsync(id);
        orphan.Has("contactName").ShouldBeFalse();
        orphan.GetProperty("contactId").GetGuid().ShouldBe(ayse.Id());
        await admin.PutJsonAsync($"{CasesPath}/{id}", new { subject = "Yetim", contactId = ayse.Id(), accountId = acme.Id() });

        await (await admin.PutAsJsonAsync($"{CasesPath}/{id}", new { subject = "" }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PutAsJsonAsync($"{CasesPath}/{Guid.NewGuid()}", new { subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Put_And_PriorityAndAssign_AreRejected_OnInactiveCases()
    {
        var org = await factory.NewOrgAsync("Case Inactive");
        var admin = org.Admin;
        var c = await admin.CreateCaseAsync("Kapanacak");
        var id = c.Id();
        await admin.SetStatusAsync(id, "resolved", "Çözüldü");

        await (await admin.PutAsJsonAsync($"{CasesPath}/{id}", new { subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.not_active");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/priority", new { priority = "high" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.not_active");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = org.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.not_active");

        await admin.SetStatusAsync(id, "closed");
        await (await admin.PutAsJsonAsync($"{CasesPath}/{id}", new { subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.not_active");

        // Yeniden açınca yine düzenlenebilir.
        await admin.SetStatusAsync(id, "open");
        await admin.PutJsonAsync($"{CasesPath}/{id}", new { subject = "Yeniden düzenlendi" });
    }

    [Fact]
    public async Task Delete_IsSoft_Everywhere_AndNumbersAreNotReused()
    {
        var org = await factory.NewOrgAsync("Case Delete");
        var admin = org.Admin;
        var first = await admin.CreateCaseAsync("Silinecek");
        var id = first.Id();
        await admin.CommentAsync(id, "public", "Yorum");

        await admin.DeleteJsonAsync($"{CasesPath}/{id}");

        await (await admin.GetAsync($"{CasesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.GetAsync($"{CasesPath}/{id}/timeline", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.DeleteAsync($"{CasesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public", body = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.ListIdsAsync()).ShouldNotContain(id);

        // Yorumlar/olaylar kalır ama okunamaz; numara yeniden kullanılmaz.
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.case_comments WHERE case_id = @id", ("id", id))).ShouldBe(1);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.cases WHERE id = @id AND is_deleted", ("id", id))).ShouldBe(1);
        (await admin.CreateCaseAsync("Sonraki")).Str("number").ShouldBe(IncrementNumber(first.Str("number")));
    }

    [Fact]
    public async Task Timeline_ListsCommentsAndEvents_NewestFirst_WithNamesAndPaging()
    {
        var org = await factory.NewOrgAsync("Case Timeline");
        var (member, memberId) = await factory.AddMemberAsync(org, "Mert Kaya", "crm.cases.read", "crm.cases.write");
        var admin = org.Admin;
        var c = await admin.CreateCaseAsync("Zaman çizelgesi");
        var id = c.Id();

        await admin.CommentAsync(id, "internal", "Faturayı muhasebeye ilettim.");
        await Task.Delay(20, Ct);
        await admin.PostJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = memberId }, HttpStatusCode.NoContent);
        await Task.Delay(20, Ct);
        await admin.PostJsonAsync($"{CasesPath}/{id}/priority", new { priority = "high" }, HttpStatusCode.NoContent);
        await Task.Delay(20, Ct);
        await member.CommentAsync(id, "public", "Merhaba, bakıyoruz.");
        await Task.Delay(20, Ct);
        await member.SetStatusAsync(id, "resolved", "Fatura düzeltildi.");

        var items = await admin.TimelineAsync(id);

        // created, comment(internal), assigned, priorityChanged, comment(public), statusChanged(new→open, aktör: yorumu yazan), statusChanged(→resolved)
        items.Count.ShouldBe(7);
        items.Select(i => i.Str("type")).Count(t => t == "statusChanged").ShouldBe(2);
        var times = items.Select(i => i.Utc("occurredAt")).ToList();
        times.ShouldBe(times.OrderByDescending(t => t).ToList(), "en yeni önce");
        items[0].Str("type").ShouldBe("statusChanged");
        (items[0].Str("from"), items[0].Str("to"), items[0].Str("note")).ShouldBe(("open", "resolved", "Fatura düzeltildi."));
        items[0].Str("actorName").ShouldBe("Mert Kaya");
        items[^1].Str("type").ShouldBe("created");
        items[^1].Str("actorName").ShouldBe(org.AdminName);

        var comment = items.Single(i => i.Str("type") == "comment" && i.Str("visibility") == "internal");
        comment.Str("body").ShouldBe("Faturayı muhasebeye ilettim.");
        comment.Str("actorName").ShouldBe(org.AdminName);
        comment.Has("from").ShouldBeFalse();

        var assigned = items.Single(i => i.Str("type") == "assigned");
        assigned.Str("toName").ShouldBe("Mert Kaya");
        assigned.Has("fromName").ShouldBeFalse("atanmamış = boş");

        var priority = items.Single(i => i.Str("type") == "priorityChanged");
        (priority.Str("from"), priority.Str("to")).ShouldBe(("normal", "high"));

        var autoOpen = items.Single(i => i.Str("type") == "statusChanged" && i.Str("to") == "open");
        autoOpen.GetProperty("actorUserId").GetGuid().ShouldBe(memberId, "aktör yorumu yazan");

        // Sayfalama.
        var page = await admin.GetJsonAsync($"{CasesPath}/{id}/timeline?page=2&pageSize=3");
        (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("totalCount").GetInt32()).ShouldBe((2, 3, 7));
        page.GetProperty("items").GetArrayLength().ShouldBe(3);
        page.GetProperty("items")[0].Id().ShouldBe(items[3].Id());

        // Atamayı kaldırma (talep çözülmüştü, önce yeniden açılır): toName yok, fromName dolu.
        await admin.SetStatusAsync(id, "open");
        await admin.PostJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = (Guid?)null }, HttpStatusCode.NoContent);
        var afterClear = (await admin.TimelineAsync(id)).First(i => i.Str("type") == "assigned");
        afterClear.Str("fromName").ShouldBe("Mert Kaya");
        afterClear.Has("toName").ShouldBeFalse();
    }

    [Fact]
    public async Task Assign_And_Priority_AreIdempotent_AndAssignChecksMembership()
    {
        var org = await factory.NewOrgAsync("Case Assign");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Ayşe Demir", "crm.cases.read");
        var c = await admin.CreateCaseAsync("Atama", new { priority = "high" });
        var id = c.Id();

        await admin.PostJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = memberId }, HttpStatusCode.NoContent);
        await admin.PostJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = memberId }, HttpStatusCode.NoContent);
        await admin.PostJsonAsync($"{CasesPath}/{id}/priority", new { priority = "high" }, HttpStatusCode.NoContent);

        var events = (await admin.TimelineAsync(id)).Select(i => i.Str("type")).ToList();
        events.Count(t => t == "assigned").ShouldBe(1);
        events.ShouldNotContain("priorityChanged");
        (await admin.GetCaseAsync(id)).GetProperty("assignedUserId").GetGuid().ShouldBe(memberId);

        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("assignedUserId");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/priority", new { }, Ct)).ShouldBeValidationErrorAsync("priority");

        // Pasifleşen atanan korunurken üyelik yeniden sorgulanmaz (aynı atanan idempotent).
        await admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{memberId}", new { isActive = false }, HttpStatusCode.NoContent);
        await admin.PostJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = memberId }, HttpStatusCode.NoContent);
        (await admin.GetCaseAsync(id)).Str("assignedUserName").ShouldBe("Ayşe Demir", "pasif üye adı da çözülür");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{Guid.NewGuid()}/assign", new { assignedUserId = memberId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    internal static string IncrementNumber(string number)
    {
        var parts = number.Split('-');
        return $"{parts[0]}-{parts[1]}-{(int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
