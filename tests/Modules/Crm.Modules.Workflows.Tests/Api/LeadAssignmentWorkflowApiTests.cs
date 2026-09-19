using System.Net;
using System.Net.Http.Json;
using Crm.Modules.Sales.Infrastructure.Persistence;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Workflows.Tests.Api.WorkflowsApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Workflows.Tests.Api;

/// <summary>
/// Workflow 1 — potansiyel atama: lead oluştur → outbox → kural değerlendirme → (sahte motor, gerçek tanım + görev işleyicileri) round-robin
/// atama + takip görevi → durum senkronu. Ayrıca idempotent tetikleme, kaynak filtresi, hata yolu, yeniden dene, kiracı izolasyonu.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LeadAssignmentWorkflowApiTests(CrmApiFactory factory)
{
    private static async Task<Guid> OwnerOfAsync(HttpClient client, Guid leadId) => (await client.GetJsonAsync($"{Base}/leads/{leadId}")).GetProperty("ownerUserId").GetGuid();

    [Fact]
    public async Task NewLeads_AreAssignedRoundRobin_AndGetAFollowUpTask()
    {
        var org = await factory.NewOrgAsync("Lead RR");
        var role = await org.CreateRoleAsync("Satis temsilcileri", "crm.leads.read", "crm.activities.read");
        var first = await factory.AddMemberAsync(org, role, "Ayse Temsilci");
        var second = await factory.AddMemberAsync(org, role, "Can Temsilci");
        var rule = await org.CreateRuleAsync(LeadRule(role, followUpHours: 48));

        var leadIds = new List<Guid>();
        foreach (var lastName in new[] { "Yilmaz", "Demir", "Kaya" })
        {
            leadIds.Add((await org.Admin.CreateLeadAsync("Ali", lastName, "Acme", "referral")).Id());
            await factory.DrainSalesAsync(); // sıra deterministik: her lead için olay ayrı işlenir
        }

        await factory.SyncExecutionsAsync();

        var owners = new List<Guid>();
        foreach (var leadId in leadIds)
        {
            owners.Add(await OwnerOfAsync(org.Admin, leadId));
        }

        var members = new[] { first.UserId, second.UserId };
        owners.Take(2).ShouldBe(members.OrderBy(m => m).ToList(), "boş havuzda kimlik sırası; ikinci lead diğer üyeye gider");
        owners[2].ShouldBe(owners[0], "eşit açık lead sayısında en uzun süredir atama almayan üye seçilir");
        owners.ShouldAllBe(o => members.Contains(o));

        // Takip görevi: atanan kişiye, ilişkili kayıt lead, vade = şimdi + 48 saat.
        for (var i = 0; i < leadIds.Count; i++)
        {
            var task = (await org.Admin.ActivitiesForAsync("lead", leadIds[i])).Single();
            task.Str("type").ShouldBe("task");
            task.Str("subject").ShouldBe($"Yeni potansiyel: Ali {new[] { "Yilmaz", "Demir", "Kaya" }[i]}");
            task.GetProperty("assignedUserId").GetGuid().ShouldBe(owners[i]);
            task.Str("status").ShouldBe("open");
            task.GetProperty("dueAt").GetDateTime().ShouldBe(DateTime.UtcNow.AddHours(48), TimeSpan.FromMinutes(2));
            task.Str("relatedName").ShouldBe($"Ali {new[] { "Yilmaz", "Demir", "Kaya" }[i]}");
        }

        // Yürütmeler tamamlandı; detayda adımlar var.
        var executions = await org.Admin.ExecutionsAsync();
        executions.Count.ShouldBe(3);
        executions.ShouldAllBe(e => e.Str("status") == "completed" && e.Str("subjectType") == "lead" && e.Str("ruleName") == "Lead atama" && e.Str("kind") == "leadAssignment");
        executions.Select(e => e.GetProperty("subjectId").GetGuid()).ShouldBe(leadIds.AsEnumerable().Reverse(), "en yeni önce");
        foreach (var execution in executions)
        {
            execution.TryGetProperty("endedAt", out _).ShouldBeTrue();
            execution.TryGetProperty("error", out _).ShouldBeFalse();
        }

        var detail = await org.Admin.GetJsonAsync($"{ExecutionsPath}/{executions[2].Id()}");
        detail.GetProperty("steps").EnumerateArray().Select(s => (s.Str("name"), s.Str("status")))
            .ShouldBe([("crm_assign_lead_owner", "COMPLETED"), ("crm_create_followup_task", "COMPLETED")]);
        detail.GetProperty("steps")[0].GetProperty("output").GetProperty("ownerUserId").GetGuid().ShouldBe(owners[0]);
        detail.TryGetProperty("approvals", out _).ShouldBeFalse();
        detail.Str("subjectName").ShouldBe("Ali Yilmaz");
        detail.GetProperty("ruleId").GetGuid().ShouldBe(rule.Id());
    }

    [Fact]
    public async Task FewestOpenLeads_WinsOverRotation()
    {
        var org = await factory.NewOrgAsync("Lead Fewest");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        var busy = await factory.AddMemberAsync(org, role, "Mesgul");
        var free = await factory.AddMemberAsync(org, role, "Bos");

        // "Mesgul" zaten 2 açık lead'e sahip (workflow dışı), kural sonradan kurulur.
        for (var i = 0; i < 2; i++)
        {
            await org.Admin.PostJsonAsync($"{Base}/leads", new { lastName = "Onceki" + i, company = "X", ownerUserId = busy.UserId });
        }

        await factory.DrainSalesAsync(); // önceki lead'lerin olayları kural yokken işlenir (yürütme yok)
        await org.CreateRuleAsync(LeadRule(role));
        (await org.Admin.ExecutionsAsync()).ShouldBeEmpty();

        var lead = await org.Admin.CreateLeadAsync("Ali", "Yeni");
        await factory.DrainSalesAsync();

        (await OwnerOfAsync(org.Admin, lead.Id())).ShouldBe(free.UserId);
    }

    [Fact]
    public async Task SourceFilter_OnlyMatchingSourcesTrigger_DisabledAndDeletedRulesDoNot()
    {
        var org = await factory.NewOrgAsync("Lead Sources");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        var member = await factory.AddMemberAsync(org, role, "Tek Uye");
        var referralOnly = await org.CreateRuleAsync(LeadRule(role, "Sadece referans", ["referral"]));
        var disabled = await org.CreateRuleAsync(LeadRule(role, "Kapali", isEnabled: false));

        var web = await org.Admin.CreateLeadAsync("Ali", "Web", source: "web");
        await factory.DrainSalesAsync();
        (await org.Admin.ExecutionsAsync()).ShouldBeEmpty("kaynak eşleşmedi, kapalı kural çalışmaz");
        (await OwnerOfAsync(org.Admin, web.Id())).ShouldBe(org.AdminUserId);

        var referral = await org.Admin.CreateLeadAsync("Ali", "Referans", source: "referral");
        await factory.DrainSalesAsync();
        var executions = await org.Admin.ExecutionsAsync();
        executions.Single().Str("ruleName").ShouldBe("Sadece referans");
        (await OwnerOfAsync(org.Admin, referral.Id())).ShouldBe(member.UserId);

        // Aynı türden birden çok etkin kural hepsi çalışır.
        await org.Admin.PostJsonAsync($"{RulesPath}/{disabled.Id()}/enable", null, HttpStatusCode.NoContent);
        var another = await org.Admin.CreateLeadAsync("Ali", "Iki kural", source: "referral");
        await factory.DrainSalesAsync();
        (await org.Admin.ExecutionsAsync($"?subjectType=lead&subjectId={another.Id()}")).Select(e => e.Str("ruleName")).ShouldBe(["Kapali", "Sadece referans"], ignoreOrder: true);

        // Silinen kural artık tetiklenmez; geçmiş yürütme adını korur.
        await org.Admin.DeleteJsonAsync($"{RulesPath}/{referralOnly.Id()}");
        var afterDelete = await org.Admin.CreateLeadAsync("Ali", "Silindi", source: "referral");
        await factory.DrainSalesAsync();
        (await org.Admin.ExecutionsAsync($"?subjectId={afterDelete.Id()}")).Select(e => e.Str("ruleName")).ShouldBe(["Kapali"]);
        (await org.Admin.ExecutionsAsync($"?ruleId={referralOnly.Id()}")).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Trigger_IsIdempotent_PerRuleAndEvent()
    {
        var org = await factory.NewOrgAsync("Lead Idempotent");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        await factory.AddMemberAsync(org, role, "Tek Uye");
        await org.CreateRuleAsync(LeadRule(role));

        var lead = await org.Admin.CreateLeadAsync("Ali", "Tekil");
        await factory.DrainSalesAsync();
        (await org.Admin.ExecutionsAsync()).Count.ShouldBe(1);
        var workflowsBefore = factory.Engine().Workflows.Count;

        // Outbox olayı yeniden teslim edilir (işlenmedi işaretlenir): aynı (kural, olay) için ikinci yürütme açılmaz.
        await factory.ExecuteAsync("UPDATE sales.outbox_messages SET processed_at = NULL, attempts = 0 WHERE type = 'Sales.LeadCreated' AND payload::text ILIKE @id", ("id", "%" + lead.Id() + "%"));
        await factory.DrainSalesAsync();
        await factory.DrainSalesAsync();

        (await org.Admin.ExecutionsAsync()).Count.ShouldBe(1);
        factory.Engine().Workflows.Count.ShouldBe(workflowsBefore, "motora ikinci workflow gönderilmedi");
        (await org.Admin.ActivitiesForAsync("lead", lead.Id())).Count.ShouldBe(1, "ikinci takip görevi açılmadı");
    }

    [Fact]
    public async Task EmptyRole_FailsWithNoAssignee_LeadOwnerUnchanged_ThenRetrySucceedsOnceAMemberExists()
    {
        var org = await factory.NewOrgAsync("Lead NoAssignee");
        var role = await org.CreateRoleAsync("Bos rol", "crm.leads.read");
        var rule = await org.CreateRuleAsync(LeadRule(role));

        var lead = await org.Admin.CreateLeadAsync("Ali", "Sahipsiz");
        await factory.DrainSalesAsync();
        await factory.SyncExecutionsAsync();

        var failed = (await org.Admin.ExecutionsAsync()).Single();
        (failed.Str("status"), failed.Str("error")).ShouldBe(("failed", "no_assignee"));
        (await OwnerOfAsync(org.Admin, lead.Id())).ShouldBe(org.AdminUserId, "lead sahibi değişmez");
        (await org.Admin.ActivitiesForAsync("lead", lead.Id())).ShouldBeEmpty("takip görevi açılmadı");
        var detail = await org.Admin.GetJsonAsync($"{ExecutionsPath}/{failed.Id()}");
        detail.GetProperty("steps").EnumerateArray().Single().Str("status").ShouldBe("FAILED_WITH_TERMINAL_ERROR");

        // Yalnız başarısız yürütme yeniden denenir; çalışan/tamamlanan denenemez.
        var member = await factory.AddMemberAsync(org, role, "Gec Gelen");
        await org.Admin.PostJsonAsync($"{ExecutionsPath}/{failed.Id()}/retry", null, HttpStatusCode.NoContent);
        await factory.SyncExecutionsAsync();

        var all = await org.Admin.ExecutionsAsync();
        all.Count.ShouldBe(2);
        all.Select(e => e.Str("status")).ShouldBe(["completed", "failed"], "yeni deneme açıldı, eskisi geçmişte kalır");
        (await OwnerOfAsync(org.Admin, lead.Id())).ShouldBe(member.UserId);
        (await org.Admin.ActivitiesForAsync("lead", lead.Id())).Single().GetProperty("assignedUserId").GetGuid().ShouldBe(member.UserId);

        var completed = all.Single(e => e.Str("status") == "completed");
        await (await org.Admin.PostRawAsync($"{ExecutionsPath}/{completed.Id()}/retry")).ShouldBeProblemAsync(HttpStatusCode.Conflict, "workflow.not_failed");
        await (await org.Admin.PostRawAsync($"{ExecutionsPath}/{completed.Id()}/terminate")).ShouldBeProblemAsync(HttpStatusCode.Conflict, "workflow.not_running");
        await (await org.Admin.PostRawAsync($"{ExecutionsPath}/{Guid.NewGuid()}/retry")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Kural silinirse başarısız yürütme yeniden denenemez.
        await org.Admin.DeleteJsonAsync($"{RulesPath}/{rule.Id()}");
        await (await org.Admin.PostRawAsync($"{ExecutionsPath}/{failed.Id()}/retry")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task InactiveMembers_AreNotAssigneeCandidates()
    {
        var org = await factory.NewOrgAsync("Lead Inactive");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        var gone = await factory.AddMemberAsync(org, role, "Ayrilan");
        var stays = await factory.AddMemberAsync(org, role, "Kalan");
        await org.DeactivateMemberAsync(gone.UserId);
        await org.CreateRuleAsync(LeadRule(role));

        var lead = await org.Admin.CreateLeadAsync("Ali", "Aktif");
        await factory.DrainSalesAsync();

        (await OwnerOfAsync(org.Admin, lead.Id())).ShouldBe(stays.UserId);

        await org.DeactivateMemberAsync(stays.UserId);
        var orphan = await org.Admin.CreateLeadAsync("Ali", "Kimsesiz");
        await factory.DrainSalesAsync();
        await factory.SyncExecutionsAsync();
        (await org.Admin.ExecutionsAsync($"?subjectId={orphan.Id()}")).Single().Str("error").ShouldBe("no_assignee");
    }

    [Fact]
    public async Task EngineOutage_MarksTheExecutionFailed_AndRetryRecovers()
    {
        var org = await factory.NewOrgAsync("Lead Outage");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        var member = await factory.AddMemberAsync(org, role, "Uye");
        await org.CreateRuleAsync(LeadRule(role));

        Guid leadId = default;
        await factory.WithEngineOutageAsync(start: true, complete: false, async () =>
        {
            leadId = (await org.Admin.CreateLeadAsync("Ali", "Kesinti")).Id();
            await factory.DrainSalesAsync();
        });

        var failed = (await org.Admin.ExecutionsAsync()).Single();
        (failed.Str("status"), failed.Str("error")).ShouldBe(("failed", "workflow.engine_unavailable"));
        (await OwnerOfAsync(org.Admin, leadId)).ShouldBe(org.AdminUserId);

        await org.Admin.PostJsonAsync($"{ExecutionsPath}/{failed.Id()}/retry", null, HttpStatusCode.NoContent);
        await factory.SyncExecutionsAsync();
        (await org.Admin.ExecutionsAsync()).Select(e => e.Str("status")).ShouldBe(["completed", "failed"]);
        (await OwnerOfAsync(org.Admin, leadId)).ShouldBe(member.UserId);
    }

    [Fact]
    public async Task CrossTenant_OtherOrganizationsLeadsNeverTriggerMyRule_AndExecutionsAreInvisible()
    {
        var orgA = await factory.NewOrgAsync("Lead Isolation A");
        var orgB = await factory.NewOrgAsync("Lead Isolation B");
        var roleA = await orgA.CreateRoleAsync("Satis A", "crm.leads.read");
        var memberA = await factory.AddMemberAsync(orgA, roleA, "A temsilcisi");
        await orgA.CreateRuleAsync(LeadRule(roleA));
        var roleB = await orgB.CreateRoleAsync("Satis B", "crm.leads.read");
        var memberB = await factory.AddMemberAsync(orgB, roleB, "B temsilcisi");

        // B'nin lead'i A'nın kuralını tetiklemez, B'nin kuralı yok.
        var leadB = await orgB.Admin.CreateLeadAsync("Ali", "B lead");
        await factory.DrainSalesAsync();
        (await orgA.Admin.ExecutionsAsync()).ShouldBeEmpty();
        (await orgB.Admin.ExecutionsAsync()).ShouldBeEmpty();
        (await OwnerOfAsync(orgB.Admin, leadB.Id())).ShouldBe(orgB.AdminUserId);

        // A'nın lead'i yalnız A'nın üyesine gider ve yürütme B'ye görünmez.
        var leadA = await orgA.Admin.CreateLeadAsync("Ali", "A lead");
        await factory.DrainSalesAsync();
        (await OwnerOfAsync(orgA.Admin, leadA.Id())).ShouldBe(memberA.UserId);
        var execution = (await orgA.Admin.ExecutionsAsync()).Single();

        (await orgB.Admin.ExecutionsAsync()).ShouldBeEmpty();
        await (await orgB.Admin.GetAsync($"{ExecutionsPath}/{execution.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await orgB.Admin.PostRawAsync($"{ExecutionsPath}/{execution.Id()}/terminate")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await orgB.Admin.PostRawAsync($"{ExecutionsPath}/{execution.Id()}/retry")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await orgB.Admin.ActivitiesForAsync("lead", leadA.Id())).ShouldBeEmpty();
        memberB.UserId.ShouldNotBe(memberA.UserId);
    }

    [Fact]
    public async Task ExecutionList_Filters_Paging_AndTimeRange()
    {
        var org = await factory.NewOrgAsync("Lead Filters");
        var emptyRole = await org.CreateRoleAsync("Bos", "crm.leads.read");
        var role = await org.CreateRoleAsync("Dolu", "crm.leads.read");
        await factory.AddMemberAsync(org, role, "Uye");
        var okRule = await org.CreateRuleAsync(LeadRule(role, "Calisan"));
        var badRule = await org.CreateRuleAsync(LeadRule(emptyRole, "Bos rol"));

        var leads = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            leads.Add((await org.Admin.CreateLeadAsync("Ali", "Filtre" + i)).Id());
        }

        await factory.DrainSalesAsync();
        await factory.SyncExecutionsAsync();

        (await org.Admin.ExecutionsAsync()).Count.ShouldBe(6);
        (await org.Admin.ExecutionsAsync("?status=completed")).ShouldAllBe(e => e.Str("ruleName") == "Calisan");
        (await org.Admin.ExecutionsAsync("?status=failed")).Count.ShouldBe(3);
        (await org.Admin.ExecutionsAsync("?status=running")).ShouldBeEmpty();
        (await org.Admin.ExecutionsAsync($"?ruleId={badRule.Id()}")).ShouldAllBe(e => e.Str("error") == "no_assignee");
        (await org.Admin.ExecutionsAsync($"?ruleId={okRule.Id()}&subjectType=lead&subjectId={leads[1]}")).Single().GetProperty("subjectId").GetGuid().ShouldBe(leads[1]);
        (await org.Admin.ExecutionsAsync("?subjectType=deal")).ShouldBeEmpty();

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-5).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(5).ToString("O"));
        var past = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O"));
        (await org.Admin.ExecutionsAsync($"?from={from}&to={to}")).Count.ShouldBe(6);
        (await org.Admin.ExecutionsAsync($"?to={past}")).ShouldBeEmpty();
        (await org.Admin.ExecutionsAsync($"?from={to}")).ShouldBeEmpty();

        var page = await org.Admin.GetJsonAsync($"{ExecutionsPath}?page=2&pageSize=4");
        (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("totalCount").GetInt64()).ShouldBe((2, 4, 6L));
        page.GetProperty("items").GetArrayLength().ShouldBe(2);

        (await org.Admin.GetAsync($"{ExecutionsPath}?status=bogus", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LeadCreated_IsPublishedThroughTheOutbox_ExistingLeadBehaviourIsUnchanged()
    {
        var org = await factory.NewOrgAsync("Lead Event");

        var lead = await org.Admin.CreateLeadAsync("Ali", "Olay", "Acme", "coldCall");

        var events = await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadCreated", lead.Id().ToString());
        var pending = events.ShouldHaveSingleItem();
        pending.Payload.ShouldContain("coldCall");
        pending.Payload.ShouldContain("Ali Olay");
        await factory.DrainSalesAsync();
        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadCreated", lead.Id().ToString())).Single().ProcessedAt.ShouldNotBeNull();

        // Başarısız oluşturma olay yayınlamaz (doğrulama hatası).
        var before = (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadCreated", org.TenantId.ToString())).Count;
        (await org.Admin.PostAsJsonAsync($"{Base}/leads", new { lastName = "", company = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.LeadCreated", org.TenantId.ToString())).Count.ShouldBe(before);
    }
}
