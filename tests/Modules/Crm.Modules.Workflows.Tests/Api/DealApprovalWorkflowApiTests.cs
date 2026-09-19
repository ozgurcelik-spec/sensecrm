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
/// Workflow 2 — fırsat onayı: büyük tutarlı fırsat kazanılır → outbox (DealStageChanged → DealStageChangedIntegration) → kural → onay
/// talepleri + görevler → HUMAN görevi bekler → <c>POST /approvals/{id}/decision</c> görevi tamamlar → not + diğer onaylar iptal → tamamlandı.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DealApprovalWorkflowApiTests(CrmApiFactory factory)
{
    private const string DecideOnly = "crm.approvals.decide";

    private sealed record Setup(Org Org, Member First, Member Second, Guid RuleId);

    private async Task<Setup> ArrangeAsync(string name, decimal minAmount = 50_000m)
    {
        var org = await factory.NewOrgAsync(name);
        var role = await org.CreateRoleAsync("Onaycilar", DecideOnly, "crm.deals.read");
        var first = await factory.AddMemberAsync(org, role, "Onaycı Bir");
        var second = await factory.AddMemberAsync(org, role, "Onaycı İki");
        var rule = await org.CreateRuleAsync(DealRule(role, minAmount));
        return new Setup(org, first, second, rule.Id());
    }

    private async Task<Guid> WinAsync(Org org, string name, decimal? amount)
    {
        var deal = await org.Admin.CreateDealAsync(name, amount);
        await org.Admin.WinDealAsync(deal.Id());
        await factory.DrainSalesAsync();
        return deal.Id();
    }

    [Fact]
    public async Task BigWonDeal_OpensApprovals_ApproveCancelsTheRest_RecordsNoteAndCompletes()
    {
        var s = await ArrangeAsync("Deal Approve");
        var dealId = await WinAsync(s.Org, "Yillik lisans", 120_000m);

        // Yürütme çalışıyor ve HUMAN görevinde bekliyor; iki onay + iki görev açıldı.
        var execution = (await s.Org.Admin.ExecutionsAsync()).Single();
        (execution.Str("status"), execution.Str("subjectType"), execution.Str("subjectName")).ShouldBe(("running", "deal", "Yillik lisans"));
        execution.GetProperty("subjectId").GetGuid().ShouldBe(dealId);
        var waiting = await s.Org.Admin.GetJsonAsync($"{ExecutionsPath}/{execution.Id()}");
        waiting.GetProperty("steps").EnumerateArray().Select(x => (x.Str("name"), x.Str("status")))
            .ShouldBe([("crm_create_approvals", "COMPLETED"), ("wait_for_decision", "IN_PROGRESS")]);
        var listed = waiting.GetProperty("approvals").EnumerateArray().ToList();
        listed.Select(a => (a.Str("approverName"), a.Str("status"))).ShouldBe([("Onaycı Bir", "pending"), ("Onaycı İki", "pending")], ignoreOrder: true);

        var tasks = (await s.Org.Admin.ActivitiesForAsync("deal", dealId)).Where(a => a.Str("type") == "task").ToList();
        tasks.Select(t => t.GetProperty("assignedUserId").GetGuid()).ShouldBe([s.First.UserId, s.Second.UserId], ignoreOrder: true);
        tasks.ShouldAllBe(t => t.Str("subject") == "Fırsat onayı: Yillik lisans");

        // Onay kaydı şekli + rozet + "mine" filtresi.
        var mine = (await s.First.Client.ApprovalsAsync("?mine=true&status=pending")).Single();
        (mine.Str("title"), mine.Str("subjectType"), mine.Str("status"), mine.Str("currency")).ShouldBe(("Fırsat onayı: Yillik lisans", "deal", "pending", "TRY"));
        (mine.GetProperty("amount").GetDecimal(), mine.GetProperty("subjectId").GetGuid(), mine.GetProperty("executionId").GetGuid()).ShouldBe((120_000m, dealId, execution.Id()));
        (mine.GetProperty("approverUserId").GetGuid(), mine.Str("approverName"), mine.Str("subjectName")).ShouldBe((s.First.UserId, "Onaycı Bir", "Yillik lisans"));
        mine.TryGetProperty("decidedAt", out _).ShouldBeFalse();
        (await s.First.Client.GetJsonAsync($"{ApprovalsPath}/summary")).GetProperty("pendingCount").GetInt32().ShouldBe(1);

        // Başkasının onayına karar veremez.
        var othersApproval = (await s.Second.Client.ApprovalsAsync()).Single();
        await (await s.First.Client.PostAsJsonAsync($"{ApprovalsPath}/{othersApproval.Id()}/decision", new { decision = "approve" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await s.First.Client.GetAsync($"{ApprovalsPath}/{othersApproval.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // İlk onay sonucu belirler.
        await s.First.Client.PostJsonAsync($"{ApprovalsPath}/{mine.Id()}/decision", new { decision = "approve", comment = "Uygun" }, HttpStatusCode.NoContent);
        var approved = await s.First.Client.GetJsonAsync($"{ApprovalsPath}/{mine.Id()}");
        (approved.Str("status"), approved.Str("comment")).ShouldBe(("approved", "Uygun"));
        approved.TryGetProperty("decidedAt", out _).ShouldBeTrue();
        (await s.Second.Client.GetJsonAsync($"{ApprovalsPath}/{othersApproval.Id()}")).Str("status").ShouldBe("cancelled");
        (await s.Second.Client.GetJsonAsync($"{ApprovalsPath}/summary")).GetProperty("pendingCount").GetInt32().ShouldBe(0);
        await (await s.Second.Client.PostAsJsonAsync($"{ApprovalsPath}/{othersApproval.Id()}/decision", new { decision = "reject", comment = "Gec" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "approval.already_decided");
        await (await s.First.Client.PostAsJsonAsync($"{ApprovalsPath}/{mine.Id()}/decision", new { decision = "approve" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "approval.already_decided");

        await factory.SyncExecutionsAsync();
        var done = await s.Org.Admin.GetJsonAsync($"{ExecutionsPath}/{execution.Id()}");
        done.Str("status").ShouldBe("completed");
        done.GetProperty("steps").EnumerateArray().Select(x => x.Str("name"))
            .ShouldBe(["crm_create_approvals", "wait_for_decision", "crm_record_decision", "crm_cancel_pending_approvals"]);
        done.GetProperty("approvals").EnumerateArray().Select(a => a.Str("status")).Order().ShouldBe(["approved", "cancelled"]);

        // Sonuç fırsata not olarak eklendi; fırsatın aşaması değişmedi (bilgilendirici onay).
        var note = (await s.Org.Admin.ActivitiesForAsync("deal", dealId)).Single(a => a.Str("type") == "note");
        note.Str("subject").ShouldBe("Onay: onaylandı (Uygun)");
        note.GetProperty("assignedUserId").GetGuid().ShouldBe(s.First.UserId);
        (await s.Org.Admin.GetJsonAsync($"{Base}/deals/{dealId}")).Str("stageKind").ShouldBe("won");
    }

    [Fact]
    public async Task Rejection_RequiresAComment_RecordsTheOutcome_AndFirstRejectionDecides()
    {
        var s = await ArrangeAsync("Deal Reject");
        var dealId = await WinAsync(s.Org, "Reddedilecek", 75_000m);
        var mine = (await s.First.Client.ApprovalsAsync()).Single();
        var url = $"{ApprovalsPath}/{mine.Id()}/decision";

        await (await s.First.Client.PostAsJsonAsync(url, new { decision = "reject" }, Ct)).ShouldBeValidationErrorAsync("comment");
        await (await s.First.Client.PostAsJsonAsync(url, new { decision = "reject", comment = "  " }, Ct)).ShouldBeValidationErrorAsync("comment");
        await (await s.First.Client.PostAsJsonAsync(url, new { comment = "karar yok" }, Ct)).ShouldBeValidationErrorAsync("decision");
        (await s.First.Client.PostAsJsonAsync(url, new { decision = "maybe" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.First.Client.GetJsonAsync($"{ApprovalsPath}/{mine.Id()}")).Str("status").ShouldBe("pending", "geçersiz istekler hiçbir şeyi değiştirmez");

        await s.First.Client.PostJsonAsync(url, new { decision = "reject", comment = "Butce yok" }, HttpStatusCode.NoContent);
        (await s.First.Client.GetJsonAsync($"{ApprovalsPath}/{mine.Id()}")).Str("status").ShouldBe("rejected");
        (await s.Second.Client.ApprovalsAsync()).Single().Str("status").ShouldBe("cancelled");

        await factory.SyncExecutionsAsync();
        (await s.Org.Admin.ExecutionsAsync()).Single().Str("status").ShouldBe("completed");
        (await s.Org.Admin.ActivitiesForAsync("deal", dealId)).Single(a => a.Str("type") == "note").Str("subject").ShouldBe("Onay: reddedildi (Butce yok)");
        (await s.Org.Admin.GetJsonAsync($"{Base}/deals/{dealId}")).Str("stageKind").ShouldBe("won");
    }

    [Fact]
    public async Task NoteTextFollowsTheOrganizationsLanguage()
    {
        var org = await factory.NewOrgAsync("Deal English", "en");
        var role = await org.CreateRoleAsync("Approvers", DecideOnly);
        var approver = await factory.AddMemberAsync(org, role, "Approver");
        await org.CreateRuleAsync(DealRule(role, 1000));
        var dealId = await WinAsync(org, "Renewal", 5000);

        var approval = (await approver.Client.ApprovalsAsync()).Single();
        approval.Str("title").ShouldBe("Deal approval: Renewal");
        await approver.Client.PostJsonAsync($"{ApprovalsPath}/{approval.Id()}/decision", new { decision = "approve" }, HttpStatusCode.NoContent);
        await factory.SyncExecutionsAsync();

        (await org.Admin.ActivitiesForAsync("deal", dealId)).Single(a => a.Str("type") == "note").Str("subject").ShouldBe("Approval: approved");
    }

    [Fact]
    public async Task OnlyWonDealsAtOrAboveTheThreshold_TriggerTheRule()
    {
        var s = await ArrangeAsync("Deal Threshold", 50_000m);

        await WinAsync(s.Org, "Kucuk", 49_999.99m);
        await WinAsync(s.Org, "Tutarsiz", null);
        (await s.Org.Admin.ExecutionsAsync()).ShouldBeEmpty("eşik altı ve tutarsız fırsat onay istemez");

        // Aşama değişimi kazanma değilse (açık → açık, kayıp) tetiklenmez.
        var open = await s.Org.Admin.CreateDealAsync("Acik kalan", 900_000m);
        var lost = await s.Org.Admin.CreateDealAsync("Kaybedilen", 900_000m);
        await s.Org.Admin.PostJsonAsync($"{Base}/deals/{lost.Id()}/stage", new { stageId = await s.Org.Admin.LostStageIdAsync(), lostReason = "Butce" }, HttpStatusCode.NoContent);
        await factory.DrainSalesAsync();
        (await s.Org.Admin.ExecutionsAsync()).ShouldBeEmpty();
        open.Id().ShouldNotBe(default);

        // Tam eşik dahil (>=).
        var exact = await WinAsync(s.Org, "Tam esik", 50_000m);
        (await s.Org.Admin.ExecutionsAsync()).Single().GetProperty("subjectId").GetGuid().ShouldBe(exact);

        // Kapalı kural çalışmaz.
        await s.Org.Admin.PostJsonAsync($"{RulesPath}/{s.RuleId}/disable", null, HttpStatusCode.NoContent);
        await WinAsync(s.Org, "Kural kapali", 300_000m);
        (await s.Org.Admin.ExecutionsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task StageChange_IsPublishedOncePerChange_AsAnIntegrationEvent_WithoutChangingExistingBehaviour()
    {
        var org = await factory.NewOrgAsync("Deal Event");
        var deal = await org.Admin.CreateDealAsync("Olay", 10_000m);

        await org.Admin.WinDealAsync(deal.Id());
        await factory.DrainSalesAsync();

        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.DealStageChanged", deal.Id().ToString())).ShouldHaveSingleItem().ProcessedAt.ShouldNotBeNull();
        var integration = (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.DealStageChangedIntegration", deal.Id().ToString())).ShouldHaveSingleItem();
        integration.ProcessedAt.ShouldNotBeNull();
        using var payload = System.Text.Json.JsonDocument.Parse(integration.Payload);
        (payload.RootElement.GetProperty("toStageKind").GetString(), payload.RootElement.GetProperty("amount").GetDecimal(), payload.RootElement.GetProperty("currency").GetString())
            .ShouldBe(("won", 10000m, "TRY"));
        payload.RootElement.GetProperty("tenantId").GetGuid().ShouldBe(org.TenantId);

        // Aynı domain event yeniden işlense de kararlı kimlik: ikinci bir integration event doğmaz (aynı kimlik = birincil anahtar çakışması yerine yeniden yayın).
        var domainEvent = (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.DealStageChanged", deal.Id().ToString())).Single();
        await factory.ExecuteAsync("DELETE FROM sales.outbox_messages WHERE id = @id", ("id", integration.Id));
        await factory.ExecuteAsync("UPDATE sales.outbox_messages SET processed_at = NULL WHERE id = @id", ("id", domainEvent.Id));
        await factory.DrainSalesAsync();
        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.DealStageChangedIntegration", deal.Id().ToString())).Single().Id.ShouldBe(integration.Id, "kimlik domain event'ten türetilir");
    }

    [Fact]
    public async Task Terminate_CancelsPendingApprovals_AndBlocksLateDecisions()
    {
        var s = await ArrangeAsync("Deal Terminate");
        await WinAsync(s.Org, "Sonlanacak", 200_000m);
        var execution = (await s.Org.Admin.ExecutionsAsync()).Single();
        var approval = (await s.First.Client.ApprovalsAsync()).Single();

        await s.Org.Admin.PostJsonAsync($"{ExecutionsPath}/{execution.Id()}/terminate", null, HttpStatusCode.NoContent);

        var terminated = await s.Org.Admin.GetJsonAsync($"{ExecutionsPath}/{execution.Id()}");
        terminated.Str("status").ShouldBe("terminated");
        terminated.TryGetProperty("endedAt", out _).ShouldBeTrue();
        terminated.GetProperty("approvals").EnumerateArray().ShouldAllBe(a => a.Str("status") == "cancelled");
        await (await s.First.Client.PostAsJsonAsync($"{ApprovalsPath}/{approval.Id()}/decision", new { decision = "approve" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "approval.already_decided");
        await (await s.Org.Admin.PostRawAsync($"{ExecutionsPath}/{execution.Id()}/terminate")).ShouldBeProblemAsync(HttpStatusCode.Conflict, "workflow.not_running");
        await (await s.Org.Admin.PostRawAsync($"{ExecutionsPath}/{execution.Id()}/retry")).ShouldBeProblemAsync(HttpStatusCode.Conflict, "workflow.not_failed");
    }

    [Fact]
    public async Task EmptyApproverRole_FailsWithNoApprover_AndCanBeRetriedAfterFixingTheRole()
    {
        var org = await factory.NewOrgAsync("Deal NoApprover");
        var role = await org.CreateRoleAsync("Bos onay", DecideOnly);
        await org.CreateRuleAsync(DealRule(role, 1000));
        var dealId = await WinAsync(org, "Onaysiz", 5000);
        await factory.SyncExecutionsAsync();

        var failed = (await org.Admin.ExecutionsAsync()).Single();
        (failed.Str("status"), failed.Str("error")).ShouldBe(("failed", "no_approver"));
        (await org.Admin.ApprovalsAsync("?mine=false")).ShouldBeEmpty();

        var approver = await factory.AddMemberAsync(org, role, "Yeni Onayci");
        await org.Admin.PostJsonAsync($"{ExecutionsPath}/{failed.Id()}/retry", null, HttpStatusCode.NoContent);

        var running = (await org.Admin.ExecutionsAsync()).First();
        running.Str("status").ShouldBe("running");
        var approval = (await approver.Client.ApprovalsAsync()).Single();
        approval.GetProperty("subjectId").GetGuid().ShouldBe(dealId);
        await approver.Client.PostJsonAsync($"{ApprovalsPath}/{approval.Id()}/decision", new { decision = "approve" }, HttpStatusCode.NoContent);
        await factory.SyncExecutionsAsync();
        (await org.Admin.ExecutionsAsync()).Select(e => e.Str("status")).ShouldBe(["completed", "failed"]);
    }

    [Fact]
    public async Task EngineOutageOnDecision_ReturnsAnError_AndKeepsTheApprovalPending()
    {
        var s = await ArrangeAsync("Deal DecisionOutage");
        await WinAsync(s.Org, "Kesinti", 90_000m);
        var mine = (await s.First.Client.ApprovalsAsync()).Single();

        await factory.WithEngineOutageAsync(start: false, complete: true, async () =>
            await (await s.First.Client.PostAsJsonAsync($"{ApprovalsPath}/{mine.Id()}/decision", new { decision = "approve" }, Ct))
                .ShouldBeProblemAsync(HttpStatusCode.InternalServerError, "workflow.engine_unavailable"));

        (await s.First.Client.GetJsonAsync($"{ApprovalsPath}/{mine.Id()}")).Str("status").ShouldBe("pending", "karar kaydedilmedi");
        (await s.Second.Client.ApprovalsAsync()).Single().Str("status").ShouldBe("pending");

        await s.First.Client.PostJsonAsync($"{ApprovalsPath}/{mine.Id()}/decision", new { decision = "approve" }, HttpStatusCode.NoContent);
        await factory.SyncExecutionsAsync();
        (await s.Org.Admin.ExecutionsAsync()).Single().Str("status").ShouldBe("completed");
    }

    [Fact]
    public async Task ApprovalPermissions_DecideNeedsThePermission_MineNeedsNone_AllNeedsManage_SummaryIsOpenToEveryMember()
    {
        var s = await ArrangeAsync("Deal Permissions");
        var dealId = await WinAsync(s.Org, "Izinli", 100_000m);
        var approval = (await s.First.Client.ApprovalsAsync()).Single();

        // Onaylayıcı role sahip ama karar iznini (sonradan) kaybetmiş üye: aynı kullanıcıyla izin dışı bir rol.
        var plain = await factory.AddMemberWithPermissionsAsync(s.Org, "Izinsiz", "crm.leads.read");
        (await plain.Client.GetJsonAsync($"{ApprovalsPath}/summary")).GetProperty("pendingCount").GetInt32().ShouldBe(0);
        (await plain.Client.ApprovalsAsync("?mine=true")).ShouldBeEmpty();
        (await plain.Client.ApprovalsAsync("")).ShouldBeEmpty("mine varsayılanı true");
        await (await plain.Client.GetAsync($"{ApprovalsPath}?mine=false", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await plain.Client.GetAsync($"{ApprovalsPath}/{approval.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await plain.Client.PostAsJsonAsync($"{ApprovalsPath}/{approval.Id()}/decision", new { decision = "approve" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Yönetici (org.workflows.manage) tüm onayları görür ve okuyabilir; karar veremez (kendi onayı değil).
        (await s.Org.Admin.ApprovalsAsync("?mine=false")).Count.ShouldBe(2);
        (await s.Org.Admin.ApprovalsAsync("?mine=true")).ShouldBeEmpty();
        (await s.Org.Admin.GetJsonAsync($"{ApprovalsPath}/{approval.Id()}")).Id().ShouldBe(approval.Id());
        await (await s.Org.Admin.PostAsJsonAsync($"{ApprovalsPath}/{approval.Id()}/decision", new { decision = "approve" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await s.Org.Admin.GetJsonAsync($"{ApprovalsPath}/summary")).GetProperty("pendingCount").GetInt32().ShouldBe(0);

        // Statü filtresi ve sayfalama.
        (await s.Org.Admin.ApprovalsAsync("?mine=false&status=pending")).Count.ShouldBe(2);
        (await s.Org.Admin.ApprovalsAsync("?mine=false&status=approved")).ShouldBeEmpty();
        var page = await s.Org.Admin.GetJsonAsync($"{ApprovalsPath}?mine=false&page=2&pageSize=1");
        (page.GetProperty("page").GetInt32(), page.GetProperty("totalCount").GetInt64(), page.GetProperty("items").GetArrayLength()).ShouldBe((2, 2L, 1));
        await (await s.Org.Admin.GetAsync($"{ApprovalsPath}/{Guid.NewGuid()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        dealId.ShouldNotBe(default);
    }

    [Fact]
    public async Task CrossTenant_ApprovalsAndExecutionsAreInvisible_AndOtherOrganizationsDealsNeverTriggerMyRule()
    {
        var a = await ArrangeAsync("Deal Isolation A", 1000m);
        var b = await ArrangeAsync("Deal Isolation B", 1000m);
        await WinAsync(a.Org, "Sadece A", 500_000m);
        var approvalA = (await a.First.Client.ApprovalsAsync()).Single();
        var executionA = (await a.Org.Admin.ExecutionsAsync()).Single();

        // B'nin olayı yalnız B'nin kuralını tetikler.
        await WinAsync(b.Org, "Sadece B", 500_000m);
        (await a.Org.Admin.ExecutionsAsync()).Count.ShouldBe(1);
        (await b.Org.Admin.ExecutionsAsync()).Single().Str("subjectName").ShouldBe("Sadece B");

        (await b.Org.Admin.ApprovalsAsync("?mine=false")).Select(x => x.Str("subjectName")).ShouldBe(["Sadece B", "Sadece B"]);
        await (await b.Org.Admin.GetAsync($"{ApprovalsPath}/{approvalA.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.First.Client.GetAsync($"{ApprovalsPath}/{approvalA.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.First.Client.PostAsJsonAsync($"{ApprovalsPath}/{approvalA.Id()}/decision", new { decision = "approve" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Org.Admin.GetAsync($"{ExecutionsPath}/{executionA.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await a.First.Client.GetJsonAsync($"{ApprovalsPath}/{approvalA.Id()}")).Str("status").ShouldBe("pending");
    }
}
