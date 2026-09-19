using System.Net;
using System.Text.Json;
using Crm.Modules.Workflows.Application;
using Crm.Modules.Workflows.Application.Tasks;
using Crm.Modules.Workflows.Infrastructure;
using Crm.Tests.Shared.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Crm.Modules.Workflows.Tests.Api.WorkflowsApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Workflows.Tests.Api;

/// <summary>
/// H1 — Conductor görev girdisine güvenilmez: görev çalışmadan önce <c>(tenantId, executionId, motor workflow kimliği)</c> çalışan bir
/// <c>workflow_executions</c> satırıyla doğrulanır; rol kimlikleri kayıtlı kuraldan, karar <c>approvals</c> satırlarından okunur.
/// L3 — fırsat sahibi kendi fırsatının onaylayıcısı olamaz.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TaskInputTrustApiTests(CrmApiFactory factory)
{
    private const string DecideOnly = "crm.approvals.decide";

    private sealed record Waiting(Org Org, Member First, Member Second, Guid Role, Guid ExecutionId, string EngineId, Guid DealId);

    private WorkflowTaskRunner Runner => factory.Services.GetRequiredService<WorkflowTaskRunner>();

    /// <summary>İki onaylayıcılı, HUMAN görevinde bekleyen bir fırsat onayı yürütmesi hazırlar.</summary>
    private async Task<Waiting> ArrangeWaitingAsync(string name)
    {
        var org = await factory.NewOrgAsync(name);
        var role = await org.CreateRoleAsync("Onaycilar", DecideOnly, "crm.deals.read");
        var first = await factory.AddMemberAsync(org, role, "Onaycı Bir");
        var second = await factory.AddMemberAsync(org, role, "Onaycı İki");
        await org.CreateRuleAsync(DealRule(role, 1000));
        var deal = await org.Admin.CreateDealAsync("Buyuk anlasma", 90_000m);
        await org.Admin.WinDealAsync(deal.Id());
        await factory.DrainSalesAsync();

        var execution = (await org.Admin.ExecutionsAsync()).Single();
        execution.Str("status").ShouldBe("running");
        var engineId = (string)(await ScalarAsync("SELECT engine_workflow_id FROM workflows.workflow_executions WHERE id = @id", ("id", execution.Id())))!;
        return new Waiting(org, first, second, role, execution.Id(), engineId, deal.Id());
    }

    private static JsonElement Input(Guid tenantId, Guid executionId, object? extra = null)
    {
        var values = new Dictionary<string, object?> { ["tenantId"] = tenantId, ["executionId"] = executionId };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                values[property.Name] = property.GetValue(extra);
            }
        }

        return JsonSerializer.SerializeToElement(values);
    }

    private static void ShouldBeUntrusted(WorkflowTaskResult result)
    {
        result.Succeeded.ShouldBeFalse();
        result.Terminal.ShouldBeTrue("doğrulanamayan görev yeniden denenmez");
        result.Reason.ShouldBe("untrusted_task");
    }

    private async Task<int> ApprovalCountAsync(Guid executionId, string? status = null) =>
        Convert.ToInt32(await ScalarAsync(
            "SELECT count(*) FROM workflows.approvals WHERE execution_id = @id AND (@status::text IS NULL OR status = @status)",
            ("id", executionId), ("status", (object?)status ?? DBNull.Value)));

    // ---- sahte kiracı / motor kimliği / bitmiş yürütme ---------------------------------------------------------------

    [Fact]
    public async Task ForgedTenantId_CannotRunTasksAgainstAnotherTenantsExecution_AndCausesNoSideEffects()
    {
        var a = await ArrangeWaitingAsync("Trust A");
        var b = await factory.NewOrgAsync("Trust B");
        var pendingBefore = await ApprovalCountAsync(a.ExecutionId, "Pending");
        pendingBefore.ShouldBe(2);

        // B'nin kiracı kimliği + A'nın yürütme kimliği + A'nın gerçek motor kimliği: yürütme B'nin kapsamında bulunmaz.
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_cancel_pending_approvals", a.EngineId, Input(b.TenantId, a.ExecutionId), Ct));
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_create_approvals", a.EngineId, Input(b.TenantId, a.ExecutionId), Ct));
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_record_decision", a.EngineId, Input(b.TenantId, a.ExecutionId), Ct));

        (await ApprovalCountAsync(a.ExecutionId, "Pending")).ShouldBe(pendingBefore, "onaylar değişmedi");
        (await ApprovalCountAsync(a.ExecutionId)).ShouldBe(2);
        (await b.Admin.ApprovalsAsync("?mine=false")).ShouldBeEmpty();
        (await b.Admin.ActivitiesForAsync("deal", a.DealId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ForgedEngineWorkflowInstanceId_IsRejected_EvenWithTheRightTenantAndExecution()
    {
        var a = await ArrangeWaitingAsync("Trust Instance");
        var other = await ArrangeWaitingAsync("Trust Other Instance");

        // Uydurma örnek kimliği ve başka bir yürütmenin gerçek motor kimliği (saldırgan kendi workflow'undan a'nın yürütmesini hedefler).
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_cancel_pending_approvals", "forged-instance-id", Input(a.Org.TenantId, a.ExecutionId), Ct));
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_cancel_pending_approvals", other.EngineId, Input(a.Org.TenantId, a.ExecutionId), Ct));
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_cancel_pending_approvals", " ", Input(a.Org.TenantId, a.ExecutionId), Ct));

        (await ApprovalCountAsync(a.ExecutionId, "Pending")).ShouldBe(2);
    }

    [Fact]
    public async Task UnknownFinishedOrMalformedExecutions_AreRejectedWithoutSideEffects()
    {
        var a = await ArrangeWaitingAsync("Trust Finished");

        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_cancel_pending_approvals", a.EngineId, Input(a.Org.TenantId, Guid.NewGuid()), Ct)); // bilinmeyen yürütme
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_cancel_pending_approvals", a.EngineId, Input(Guid.Empty, a.ExecutionId), Ct)); // kiracı yok
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_cancel_pending_approvals", a.EngineId, JsonSerializer.SerializeToElement(new { tenantId = a.Org.TenantId }), Ct)); // yürütme kimliği yok

        // Yürütme sonlandırıldı: aynı (doğru) üçlü bile artık geçerli değil.
        await a.Org.Admin.PostJsonAsync($"{ExecutionsPath}/{a.ExecutionId}/terminate", null, HttpStatusCode.NoContent);
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_create_approvals", a.EngineId, Input(a.Org.TenantId, a.ExecutionId), Ct));
    }

    [Fact]
    public async Task TaskTypeMustMatchTheExecutionsKind()
    {
        var a = await ArrangeWaitingAsync("Trust Kind");

        // Fırsat onayı yürütmesine lead atama görevi.
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_assign_lead_owner", a.EngineId, Input(a.Org.TenantId, a.ExecutionId), Ct));
        ShouldBeUntrusted(await Runner.ExecuteAsync("crm_create_followup_task", a.EngineId, Input(a.Org.TenantId, a.ExecutionId, new { ownerUserId = a.First.UserId }), Ct));
    }

    // ---- rol/parametre kayıtlı kuraldan; karar veritabanından --------------------------------------------------------

    [Fact]
    public async Task RoleIdsInTheTaskInput_AreIgnored_TheRegisteredRuleDecidesWhoApproves()
    {
        var a = await ArrangeWaitingAsync("Trust Role");
        var attackerRole = await a.Org.CreateRoleAsync("Saldirgan Rol", DecideOnly);
        var attacker = await factory.AddMemberAsync(a.Org, attackerRole, "Saldırgan");

        // Aynı (doğru) üçlü + sahte approverRoleId: görev kuraldaki rolü kullanır; saldırganın rolü onaylayıcı olmaz.
        var result = await Runner.ExecuteAsync("crm_create_approvals", a.EngineId, Input(a.Org.TenantId, a.ExecutionId, new { approverRoleId = attackerRole, dealName = "Sahte" }), Ct);

        result.Succeeded.ShouldBeTrue(result.Reason);
        (await attacker.Client.ApprovalsAsync("?mine=true")).ShouldBeEmpty();
        (await ApprovalCountAsync(a.ExecutionId)).ShouldBe(2);
        (await a.First.Client.ApprovalsAsync()).Single().Str("subjectName").ShouldBe("Buyuk anlasma", "konu adı yürütmenin anlık görüntüsünden");
    }

    [Fact]
    public async Task FollowUpOwner_FromTaskOutput_MustBeAnActiveMemberOfTheRulesRole()
    {
        var org = await factory.NewOrgAsync("Trust Follow Up");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read", "crm.activities.read");
        var member = await factory.AddMemberAsync(org, role, "Satisci");
        var outsider = await factory.AddMemberWithPermissionsAsync(org, "Dışarıdan", "crm.leads.read");
        await org.CreateRuleAsync(LeadRule(role));

        // Motor başlatılamıyorsa lead workflow'u çalışan (motor kimliği atanmış) bir yürütme bırakmaz; kimliği elle atayarak sahne kur.
        await factory.WithEngineOutageAsync(start: true, complete: false, async () =>
        {
            await org.Admin.CreateLeadAsync("Ada", "Lovelace");
            await factory.DrainSalesAsync();
        });
        var execution = (await org.Admin.ExecutionsAsync()).Single();
        execution.Str("status").ShouldBe("failed");
        await factory.ExecuteAsync("UPDATE workflows.workflow_executions SET status = 'Running', error = NULL, engine_workflow_id = 'manual-instance' WHERE id = @id", ("id", execution.Id()));

        var forged = await Runner.ExecuteAsync("crm_create_followup_task", "manual-instance", Input(org.TenantId, execution.Id(), new { ownerUserId = outsider.UserId }), Ct);
        forged.Succeeded.ShouldBeFalse();
        forged.Reason.ShouldBe("invalid_input");

        var valid = await Runner.ExecuteAsync("crm_create_followup_task", "manual-instance", Input(org.TenantId, execution.Id(), new { ownerUserId = member.UserId }), Ct);
        valid.Succeeded.ShouldBeTrue(valid.Reason);
    }

    [Fact]
    public async Task ForgedHumanTaskOutput_DoesNotRecordAnyDecision()
    {
        var a = await ArrangeWaitingAsync("Trust Human");
        var attackerId = a.Second.UserId;

        // Conductor'a erişimi olan biri HUMAN görevini uydurma çıktıyla tamamlar; DB'de karar verilmiş onay YOK.
        await factory.Engine().CompleteWaitTaskAsync(a.EngineId, WorkflowNames.WaitDecisionTaskRef,
            new Dictionary<string, object?> { ["decision"] = "approved", ["comment"] = "uydurma", ["approverUserId"] = attackerId.ToString() }, Ct);
        await factory.Engine().DrainAsync();
        await factory.SyncExecutionsAsync();

        var execution = await a.Org.Admin.GetJsonAsync($"{ExecutionsPath}/{a.ExecutionId}");
        execution.Str("status").ShouldBe("failed");
        execution.Str("error").ShouldBe("decision_not_found");
        (await a.Org.Admin.ActivitiesForAsync("deal", a.DealId)).Where(x => x.Str("type") == "note").ShouldBeEmpty("hiçbir karar notu yazılmaz");
    }

    [Fact]
    public async Task RecordedDecision_ComesFromTheApprovalRows_NotFromTheHumanTaskOutput()
    {
        var a = await ArrangeWaitingAsync("Trust Decision");
        var approval = (await a.First.Client.ApprovalsAsync()).Single();

        // Gerçek karar DB'de: birinci onaylayıcı ONAYLADI ("gercek yorum"). HUMAN çıktısı ise reddi ve başka bir kişiyi iddia ediyor.
        await factory.ExecuteAsync(
            "UPDATE workflows.approvals SET status = 'Approved', comment = 'gercek yorum', decided_at = now() WHERE id = @id", ("id", approval.Id()));
        await factory.Engine().CompleteWaitTaskAsync(a.EngineId, WorkflowNames.WaitDecisionTaskRef,
            new Dictionary<string, object?> { ["decision"] = "rejected", ["comment"] = "sahte red", ["approverUserId"] = a.Second.UserId.ToString() }, Ct);
        await factory.Engine().DrainAsync();
        await factory.SyncExecutionsAsync();

        var note = (await a.Org.Admin.ActivitiesForAsync("deal", a.DealId)).Single(x => x.Str("type") == "note");
        note.Str("subject").ShouldBe("Onay: onaylandı (gercek yorum)");
        note.GetProperty("assignedUserId").GetGuid().ShouldBe(a.First.UserId, "not, kararı veren gerçek onaylayıcıya atanır");
        (await a.Org.Admin.GetJsonAsync($"{ExecutionsPath}/{a.ExecutionId}")).Str("status").ShouldBe("completed");
    }

    // ---- L3: fırsat sahibi kendi fırsatını onaylayamaz ---------------------------------------------------------------

    [Fact]
    public async Task DealOwner_IsExcludedFromTheApprovers()
    {
        var org = await factory.NewOrgAsync("Owner Excluded");
        var role = await org.CreateRoleAsync("Onaycilar", DecideOnly, "crm.deals.read");
        var owner = await factory.AddMemberAsync(org, role, "Fırsat Sahibi");
        var other = await factory.AddMemberAsync(org, role, "Diğer Onaycı");
        await org.CreateRuleAsync(DealRule(role, 1000));

        var account = await org.Admin.PostJsonAsync($"{Base}/accounts", new { name = "Firma " + Guid.NewGuid().ToString("N")[..6] });
        var deal = await org.Admin.PostJsonAsync($"{Base}/deals", new { name = "Sahipli firsat", accountId = account.Id(), amount = 5000m, ownerUserId = owner.UserId });
        await org.Admin.WinDealAsync(deal.Id());
        await factory.DrainSalesAsync();

        (await owner.Client.ApprovalsAsync()).ShouldBeEmpty("sahip kendi fırsatını onaylayamaz");
        (await other.Client.ApprovalsAsync()).Single().Str("status").ShouldBe("pending");
        (await org.Admin.ExecutionsAsync()).Single().Str("status").ShouldBe("running");
    }

    [Fact]
    public async Task DealOwner_WhoIsTheOnlyApprover_FailsTheExecutionWithNoApprover()
    {
        var org = await factory.NewOrgAsync("Sole Owner");
        var role = await org.CreateRoleAsync("Tek Onaycı", DecideOnly, "crm.deals.read");
        var owner = await factory.AddMemberAsync(org, role, "Tek Üye");
        await org.CreateRuleAsync(DealRule(role, 1000));

        var account = await org.Admin.PostJsonAsync($"{Base}/accounts", new { name = "Firma " + Guid.NewGuid().ToString("N")[..6] });
        var deal = await org.Admin.PostJsonAsync($"{Base}/deals", new { name = "Tek sahipli", accountId = account.Id(), amount = 5000m, ownerUserId = owner.UserId });
        await org.Admin.WinDealAsync(deal.Id());
        await factory.DrainSalesAsync();
        await factory.SyncExecutionsAsync();

        var execution = (await org.Admin.ExecutionsAsync()).Single();
        (execution.Str("status"), execution.Str("error")).ShouldBe(("failed", "no_approver"));
        (await owner.Client.ApprovalsAsync()).ShouldBeEmpty();
    }

    // ---- yardımcılar -------------------------------------------------------------------------------------------------

    private async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var scalar = await command.ExecuteScalarAsync(Ct);
        return scalar is DBNull ? null : scalar;
    }
}
