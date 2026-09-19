using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Shared.Kernel.Time;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Activities.Tests.Api.ActivitiesApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Activities.Tests.Api;

/// <summary>Aktiviteler: CRUD, doğrulama, tür/durum kuralları, tamamla/yeniden aç, ilişkili kayıt, atama.</summary>
[Collection(ApiCollection.Name)]
public sealed class ActivitiesCrudApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Task_Crud_DefaultsResponseShapeAndFullReplace()
    {
        var org = await factory.NewOrgAsync("Act Crud");
        var admin = org.Admin;
        var due = DateTime.UtcNow.AddDays(3);

        var created = await admin.CreateActivityAsync("task", "  Teklifi gönder  ", new { description = "PDF olarak", dueAt = due.Iso() });
        var id = created.Id();

        (created.Str("type"), created.Str("subject"), created.Str("status"), created.Str("priority")).ShouldBe(("task", "Teklifi gönder", "open", "normal"));
        created.Str("description").ShouldBe("PDF olarak");
        created.GetProperty("assignedUserId").GetGuid().ShouldBe(org.AdminUserId);
        created.Str("assignedUserName").ShouldBe(org.AdminName);
        created.GetProperty("isOverdue").GetBoolean().ShouldBeFalse();
        created.GetProperty("dueAt").GetDateTime().ShouldBe(due, TimeSpan.FromSeconds(1));
        created.TryGetProperty("completedAt", out _).ShouldBeFalse();
        created.TryGetProperty("relatedType", out _).ShouldBeFalse();
        created.TryGetProperty("relatedName", out _).ShouldBeFalse();
        created.TryGetProperty("updatedAt", out _).ShouldBeFalse();
        created.TryGetProperty("createdAt", out _).ShouldBeTrue();

        // PUT tam değiştirmedir: gönderilmeyen isteğe bağlı alan temizlenir, atanan ve durum korunur.
        await admin.PutJsonAsync($"{ActivitiesPath}/{id}", new { type = "task", subject = "Teklifi gönder (v2)", priority = "high" });
        var updated = await admin.GetJsonAsync($"{ActivitiesPath}/{id}");
        (updated.Str("subject"), updated.Str("priority"), updated.Str("status")).ShouldBe(("Teklifi gönder (v2)", "high", "open"));
        updated.TryGetProperty("description", out _).ShouldBeFalse();
        updated.TryGetProperty("dueAt", out _).ShouldBeFalse();
        updated.GetProperty("assignedUserId").GetGuid().ShouldBe(org.AdminUserId);
        updated.TryGetProperty("updatedAt", out _).ShouldBeTrue();

        await admin.DeleteJsonAsync($"{ActivitiesPath}/{id}");
        await (await admin.GetAsync($"{ActivitiesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PutAsJsonAsync($"{ActivitiesPath}/{id}", new { type = "task", subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.DeleteAsync($"{ActivitiesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await admin.ListIdsAsync()).ShouldNotContain(id);
        await (await admin.PostAsync($"{ActivitiesPath}/{id}/complete", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Validation_RejectsBadInput_WithFieldErrors()
    {
        var admin = (await factory.NewOrgAsync("Act Validation")).Admin;

        await (await admin.PostAsJsonAsync(ActivitiesPath, new { subject = "Tür yok" }, Ct)).ShouldBeValidationErrorAsync("type");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "" }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = new string('x', 201) }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", description = new string('x', 4001) }, Ct)).ShouldBeValidationErrorAsync("description");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", relatedType = "account" }, Ct)).ShouldBeValidationErrorAsync("relatedId");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", relatedId = Guid.NewGuid() }, Ct)).ShouldBeValidationErrorAsync("relatedType");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", assignedUserId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("assignedUserId");
        (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "bogus", subject = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", priority = "urgent" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var start = DateTime.UtcNow.AddDays(1);
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "meeting", subject = "x", startAt = start.Iso(), endAt = start.AddMinutes(-5).Iso() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "activity.invalid_range");

        var meeting = await admin.CreateActivityAsync("meeting", "Aralık tamam", new { startAt = start.Iso(), endAt = start.Iso() });
        await (await admin.PutAsJsonAsync($"{ActivitiesPath}/{meeting.Id()}", new { type = "meeting", subject = "x", startAt = start.Iso(), endAt = start.AddHours(-1).Iso() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "activity.invalid_range");
        (await admin.GetJsonAsync($"{ActivitiesPath}/{meeting.Id()}")).Str("subject").ShouldBe("Aralık tamam");
    }

    [Fact]
    public async Task Calls_And_Meetings_KeepTheirTimeRange()
    {
        var admin = (await factory.NewOrgAsync("Act Range")).Admin;
        var start = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

        var call = await admin.CreateActivityAsync("call", "Müşteri araması", new { startAt = start.Iso(), endAt = start.AddMinutes(20).Iso() });
        var meeting = await admin.CreateActivityAsync("meeting", "Yıllık değerlendirme", new { startAt = start.AddDays(1).Iso(), priority = "high" });

        call.GetProperty("startAt").GetDateTime().ShouldBe(start);
        call.GetProperty("endAt").GetDateTime().ShouldBe(start.AddMinutes(20));
        meeting.TryGetProperty("endAt", out _).ShouldBeFalse();
        (meeting.Str("type"), meeting.Str("status"), meeting.Str("priority")).ShouldBe(("meeting", "open", "high"));
    }

    [Fact]
    public async Task Note_IsAlwaysCompleted_AndItsStatusCannotChange()
    {
        var admin = (await factory.NewOrgAsync("Act Note")).Admin;

        var note = await admin.CreateActivityAsync("note", "Toplantı notu", new { description = "Fiyat konuşuldu" });
        var id = note.Id();
        note.Str("status").ShouldBe("completed");
        note.TryGetProperty("completedAt", out _).ShouldBeTrue();
        note.GetProperty("isOverdue").GetBoolean().ShouldBeFalse();

        await (await admin.PostAsync($"{ActivitiesPath}/{id}/complete", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "activity.note_status_fixed");
        await (await admin.PostAsync($"{ActivitiesPath}/{id}/reopen", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "activity.note_status_fixed");
        await (await admin.PutAsJsonAsync($"{ActivitiesPath}/{id}", new { type = "note", subject = "x", status = "open" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "activity.note_status_fixed");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "note", subject = "x", status = "cancelled" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "activity.note_status_fixed");

        // Not düzenlenebilir; durum completed kalır.
        await admin.PutJsonAsync($"{ActivitiesPath}/{id}", new { type = "note", subject = "Güncel not" });
        (await admin.GetJsonAsync($"{ActivitiesPath}/{id}")).Str("status").ShouldBe("completed");

        // Göreve dönüşen tür/nota dönüşen görev kuralları.
        var task = await admin.CreateActivityAsync("task", "Görev iken");
        await admin.PutJsonAsync($"{ActivitiesPath}/{task.Id()}", new { type = "note", subject = "Artık not" });
        (await admin.GetJsonAsync($"{ActivitiesPath}/{task.Id()}")).Str("status").ShouldBe("completed");
    }

    [Fact]
    public async Task CompleteAndReopen_ToggleStatusCompletedAtAndOverdue()
    {
        var admin = (await factory.NewOrgAsync("Act Complete")).Admin;
        var overdue = await admin.CreateActivityAsync("task", "Geciken", new { dueAt = DateTime.UtcNow.AddDays(-2).Iso() });
        var id = overdue.Id();
        overdue.GetProperty("isOverdue").GetBoolean().ShouldBeTrue();

        await admin.PostJsonAsync($"{ActivitiesPath}/{id}/complete", null, HttpStatusCode.NoContent);
        var done = await admin.GetJsonAsync($"{ActivitiesPath}/{id}");
        done.Str("status").ShouldBe("completed");
        done.GetProperty("isOverdue").GetBoolean().ShouldBeFalse("tamamlanan geciken sayılmaz");
        var completedAt = done.GetProperty("completedAt").GetDateTime();

        await admin.PostJsonAsync($"{ActivitiesPath}/{id}/complete", null, HttpStatusCode.NoContent); // idempotent
        (await admin.GetJsonAsync($"{ActivitiesPath}/{id}")).GetProperty("completedAt").GetDateTime().ShouldBe(completedAt);

        await admin.PostJsonAsync($"{ActivitiesPath}/{id}/reopen", null, HttpStatusCode.NoContent);
        var reopened = await admin.GetJsonAsync($"{ActivitiesPath}/{id}");
        reopened.Str("status").ShouldBe("open");
        reopened.TryGetProperty("completedAt", out _).ShouldBeFalse();
        reopened.GetProperty("isOverdue").GetBoolean().ShouldBeTrue();

        // Durum PUT ile de değişebilir (iptal); iptal edilen geciken sayılmaz.
        await admin.PutJsonAsync($"{ActivitiesPath}/{id}", new { type = "task", subject = "Geciken", status = "cancelled", dueAt = DateTime.UtcNow.AddDays(-2).Iso() });
        var cancelled = await admin.GetJsonAsync($"{ActivitiesPath}/{id}");
        cancelled.Str("status").ShouldBe("cancelled");
        cancelled.GetProperty("isOverdue").GetBoolean().ShouldBeFalse();
        await admin.PostJsonAsync($"{ActivitiesPath}/{id}/reopen", null, HttpStatusCode.NoContent);
        (await admin.GetJsonAsync($"{ActivitiesPath}/{id}")).Str("status").ShouldBe("open");
    }

    [Fact]
    public async Task RelatedRecord_IsValidated_NamedAndSoftlyLinked()
    {
        var org = await factory.NewOrgAsync("Act Related");
        var admin = org.Admin;
        var other = await factory.NewOrgAsync("Act Related Other");

        var account = await admin.CreateAccountAsync("Acme Ltd");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { firstName = "Ayşe", lastName = "Yılmaz", accountId = account.Id() });
        var lead = await admin.PostJsonAsync($"{Base}/leads", new { firstName = "Can", lastName = "Öz", company = "Öz A.Ş." });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Yıllık lisans", accountId = account.Id() });

        var links = new (string Type, JsonElement Record, string ExpectedName)[]
        {
            ("account", account, "Acme Ltd"),
            ("contact", contact, "Ayşe Yılmaz"),
            ("lead", lead, "Can Öz"),
            ("deal", deal, "Yıllık lisans"),
        };
        var activityIds = new Dictionary<string, Guid>();
        foreach (var (type, record, expectedName) in links)
        {
            var activity = await admin.CreateActivityAsync("task", $"{type} görevi", new { relatedType = type, relatedId = record.Id() });
            (activity.Str("relatedType"), activity.GetProperty("relatedId").GetGuid(), activity.Str("relatedName")).ShouldBe((type, record.Id(), expectedName));
            activityIds[type] = activity.Id();
        }

        // Filtre: relatedType + relatedId.
        (await admin.ListIdsAsync($"?relatedType=deal&relatedId={deal.Id()}")).ShouldBe([activityIds["deal"]]);
        (await admin.ListIdsAsync($"?relatedId={account.Id()}")).ShouldBe([activityIds["account"]]);
        (await admin.GetJsonAsync($"{ActivitiesPath}?relatedType=contact")).GetProperty("totalCount").GetInt32().ShouldBe(1);

        // Yok olan / başka organizasyonun kaydı: 404 activity.related_not_found (varlık sızdırılmaz).
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", relatedType = "account", relatedId = Guid.NewGuid() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "activity.related_not_found");
        var foreignAccount = await other.Admin.CreateAccountAsync("Yabancı firma");
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", relatedType = "account", relatedId = foreignAccount.Id() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "activity.related_not_found");
        // Tür uyuşmazlığı: bir firma kimliği "contact" olarak bağlanamaz.
        await (await admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", relatedType = "contact", relatedId = account.Id() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "activity.related_not_found");
        await (await admin.PutAsJsonAsync($"{ActivitiesPath}/{activityIds["account"]}", new { type = "task", subject = "x", relatedType = "account", relatedId = foreignAccount.Id() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "activity.related_not_found");

        // Bağlı kayıt silinirse aktivite kalır, relatedName boş döner; aktivite başka alanlar için düzenlenebilir kalır.
        await admin.DeleteJsonAsync($"{Base}/deals/{deal.Id()}");
        var orphan = await admin.GetJsonAsync($"{ActivitiesPath}/{activityIds["deal"]}");
        (orphan.Str("relatedType"), orphan.GetProperty("relatedId").GetGuid()).ShouldBe(("deal", deal.Id()));
        orphan.TryGetProperty("relatedName", out _).ShouldBeFalse();
        await admin.PutJsonAsync($"{ActivitiesPath}/{activityIds["deal"]}", new { type = "task", subject = "Silinen fırsat görevi", relatedType = "deal", relatedId = deal.Id() });
        (await admin.GetJsonAsync($"{ActivitiesPath}/{activityIds["deal"]}")).Str("subject").ShouldBe("Silinen fırsat görevi");

        // İlişki kaldırılabilir (PUT'ta ikisi de yok).
        await admin.PutJsonAsync($"{ActivitiesPath}/{activityIds["account"]}", new { type = "task", subject = "Bağsız" });
        (await admin.GetJsonAsync($"{ActivitiesPath}/{activityIds["account"]}")).TryGetProperty("relatedType", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Assignment_DefaultsToCaller_RequiresAnActiveMember()
    {
        var org = await factory.NewOrgAsync("Act Assign");
        var other = await factory.NewOrgAsync("Act Assign Other");
        var (member, memberId) = await factory.AddMemberAsync(org, "Üye Ali", "crm.activities.read", "crm.activities.write");

        var forMember = await org.Admin.CreateActivityAsync("task", "Üyeye görev", new { assignedUserId = memberId });
        (forMember.GetProperty("assignedUserId").GetGuid(), forMember.Str("assignedUserName")).ShouldBe((memberId, "Üye Ali"));

        var byMember = await member.CreateActivityAsync("call", "Üyenin araması");
        byMember.GetProperty("assignedUserId").GetGuid().ShouldBe(memberId);

        await (await org.Admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", assignedUserId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await org.Admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", assignedUserId = other.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await org.Admin.PutAsJsonAsync($"{ActivitiesPath}/{forMember.Id()}", new { type = "task", subject = "x", assignedUserId = other.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");

        // Atama PUT ile değişir; gönderilmezse mevcut atanan korunur.
        await org.Admin.PutJsonAsync($"{ActivitiesPath}/{forMember.Id()}", new { type = "task", subject = "Yeniden atandı", assignedUserId = org.AdminUserId });
        (await org.Admin.GetJsonAsync($"{ActivitiesPath}/{forMember.Id()}")).GetProperty("assignedUserId").GetGuid().ShouldBe(org.AdminUserId);
        await org.Admin.PutJsonAsync($"{ActivitiesPath}/{forMember.Id()}", new { type = "task", subject = "Atanan korunur" });
        (await org.Admin.GetJsonAsync($"{ActivitiesPath}/{forMember.Id()}")).GetProperty("assignedUserId").GetGuid().ShouldBe(org.AdminUserId);

        // Atanan pasifleşse de aktivite düzenlenebilir kalır (üyelik yeniden sorgulanmaz).
        await org.Admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{memberId}", new { isActive = false }, HttpStatusCode.NoContent);
        await org.Admin.PutJsonAsync($"{ActivitiesPath}/{byMember.Id()}", new { type = "call", subject = "Pasif üyenin araması (düzenlendi)" });
    }

    [Fact]
    public async Task Permissions_ReadAndWriteAreSeparate_AndReportsNeedTheirOwn()
    {
        var org = await factory.NewOrgAsync("Act Permissions");
        var (reader, _) = await factory.AddMemberAsync(org, "Okuyucu", "crm.activities.read");
        var (writer, _) = await factory.AddMemberAsync(org, "Yazıcı", "crm.activities.write");
        var (nobody, _) = await factory.AddMemberAsync(org, "Yetkisiz", "crm.deals.read");
        var existing = await org.Admin.CreateActivityAsync("task", "Var olan");
        var id = existing.Id();

        // Okuyucu okur, yazamaz.
        (await reader.GetAsync(ActivitiesPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reader.GetAsync($"{ActivitiesPath}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reader.GetAsync($"{ActivitiesPath}/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await reader.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PutAsJsonAsync($"{ActivitiesPath}/{id}", new { type = "task", subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.DeleteAsync($"{ActivitiesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsync($"{ActivitiesPath}/{id}/complete", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsync($"{ActivitiesPath}/{id}/reopen", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Yazıcı yazar (tamamlar, siler), okuyamaz.
        await (await writer.GetAsync(ActivitiesPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await writer.GetAsync($"{ActivitiesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await writer.GetAsync($"{ActivitiesPath}/summary", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await writer.PostAsync($"{ActivitiesPath}/{id}/complete", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await org.Admin.GetJsonAsync($"{ActivitiesPath}/{id}")).Str("status").ShouldBe("completed");
        (await writer.DeleteAsync($"{ActivitiesPath}/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Hiçbiri olmayan hem okuyamaz hem yazamaz; aktivite raporu yalnız crm.reports.read ile.
        await (await nobody.GetAsync(ActivitiesPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await nobody.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.GetAsync($"{Base}/reports/activities/by-user", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var (reports, _) = await factory.AddMemberAsync(org, "Raporcu", "crm.reports.read");
        (await reports.GetAsync($"{Base}/reports/activities/by-user", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await reports.GetAsync(ActivitiesPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task CrossTenantIsolation_AdminOfA_CannotSeeOrModify_ActivitiesOfB()
    {
        var a = await factory.NewOrgAsync("Act Iso A");
        var b = await factory.NewOrgAsync("Act Iso B");
        var accountB = await b.Admin.CreateAccountAsync("B Firması");
        var taskB = await b.Admin.CreateActivityAsync("task", "B görevi", new { relatedType = "account", relatedId = accountB.Id(), dueAt = DateTime.UtcNow.AddDays(-1).Iso() });
        var noteB = await b.Admin.CreateActivityAsync("note", "B notu");
        var taskA = await a.Admin.CreateActivityAsync("task", "A görevi");

        // Okuma: bulunamaz, listelerde yok.
        await (await a.Admin.GetAsync($"{ActivitiesPath}/{taskB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await a.Admin.ListIdsAsync()).ShouldBe([taskA.Id()]);
        (await a.Admin.ListIdsAsync($"?assignedUserId={b.AdminUserId}")).ShouldBeEmpty();
        (await a.Admin.ListIdsAsync($"?relatedId={accountB.Id()}")).ShouldBeEmpty();
        (await a.Admin.ListIdsAsync("?overdue=true")).ShouldBeEmpty();

        // Değiştirme/silme/tamamlama: bulunamaz.
        await (await a.Admin.PutAsJsonAsync($"{ActivitiesPath}/{taskB.Id()}", new { type = "task", subject = "Ele geçirildi" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.DeleteAsync($"{ActivitiesPath}/{taskB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.PostAsync($"{ActivitiesPath}/{taskB.Id()}/complete", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.PostAsync($"{ActivitiesPath}/{noteB.Id()}/reopen", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Çapraz referanslar.
        await (await a.Admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", relatedType = "account", relatedId = accountB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "activity.related_not_found");
        await (await a.Admin.PostAsJsonAsync(ActivitiesPath, new { type = "task", subject = "x", assignedUserId = b.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");

        // Özet ve denetim de kiracıya bağlıdır.
        var summaryA = await a.Admin.GetJsonAsync($"{ActivitiesPath}/summary");
        (summaryA.GetProperty("openCount").GetInt32(), summaryA.GetProperty("overdueCount").GetInt32()).ShouldBe((1, 0));
        var foreignSummary = await a.Admin.GetJsonAsync($"{ActivitiesPath}/summary?assignedUserId={b.AdminUserId}");
        foreignSummary.GetProperty("openCount").GetInt32().ShouldBe(0);
        (await a.Admin.GetJsonAsync($"{Base}/audit?entityType=Activity&entityId={taskB.Id()}")).GetProperty("total").GetInt64().ShouldBe(0);
        var orgAudit = await a.Admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100");
        var auditedIds = orgAudit.GetProperty("items").EnumerateArray().Select(i => i.Str("entityId")).ToList();
        auditedIds.ShouldNotContain(taskB.Id().ToString());
        auditedIds.ShouldNotContain(noteB.Id().ToString());

        // B tarafında hiçbir şey değişmedi.
        var taskBAfter = await b.Admin.GetJsonAsync($"{ActivitiesPath}/{taskB.Id()}");
        (taskBAfter.Str("subject"), taskBAfter.Str("status")).ShouldBe(("B görevi", "open"));
    }

    [Fact]
    public async Task Audit_RecordsCreateUpdateDelete_AndIsReadableWithActivitiesReadOnly()
    {
        var org = await factory.NewOrgAsync("Act Audit");
        var (reader, _) = await factory.AddMemberAsync(org, "Denetçi", "crm.activities.read");
        var (outsider, _) = await factory.AddMemberAsync(org, "Yabancı", "crm.deals.read");

        var task = await org.Admin.CreateActivityAsync("task", "İzlenen görev", new { description = "ilk", priority = "low" });
        var id = task.Id();
        await org.Admin.PutJsonAsync($"{ActivitiesPath}/{id}", new { type = "task", subject = "İzlenen görev", description = "ikinci", priority = "high" });
        await org.Admin.PostJsonAsync($"{ActivitiesPath}/{id}/complete", null, HttpStatusCode.NoContent);
        await org.Admin.DeleteJsonAsync($"{ActivitiesPath}/{id}");

        // crm.activities.read yeter (org.audit.read gerekmez); tür-izin eşlemesi Activities modülünden gelir.
        var audit = await reader.GetJsonAsync($"{Base}/audit?entityType=Activity&entityId={id}");
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        audit.GetProperty("total").GetInt64().ShouldBe(4);
        items.Select(i => i.Str("action")).Reverse().ShouldBe(["created", "updated", "updated", "deleted"]);
        items.ShouldAllBe(i => i.Str("entityType") == "Activity");

        var created = items[^1].GetProperty("changes");
        created.GetProperty("subject").GetProperty("new").GetString().ShouldBe("İzlenen görev");
        created.GetProperty("status").GetProperty("new").GetString().ShouldBe("open");
        created.GetProperty("priority").GetProperty("new").GetString().ShouldBe("low");
        var updated = items[^2].GetProperty("changes");
        (updated.GetProperty("description").GetProperty("old").GetString(), updated.GetProperty("description").GetProperty("new").GetString()).ShouldBe(("ilk", "ikinci"));
        updated.GetProperty("priority").GetProperty("new").GetString().ShouldBe("high");
        var completed = items[^3].GetProperty("changes");
        completed.GetProperty("status").GetProperty("new").GetString().ShouldBe("completed");
        completed.TryGetProperty("completedAt", out _).ShouldBeTrue();

        // Kişisel veri alanı yok: hiçbir değer maskelenmez.
        items.SelectMany(i => i.GetProperty("changes").EnumerateObject()).ShouldAllBe(c => c.Value.ToString().Contains("***", StringComparison.Ordinal) == false);

        // İzni olmayan okuyamaz; org.audit.read'e sahip yönetici okur.
        await (await outsider.GetAsync($"{Base}/audit?entityType=Activity&entityId={id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await org.Admin.GetJsonAsync($"{Base}/audit?entityType=Activity&entityId={id}")).GetProperty("total").GetInt64().ShouldBe(4);
    }
}
