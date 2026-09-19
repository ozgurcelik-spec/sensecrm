using System.Text.Json;
using System.Text.Json.Nodes;
using Sense.Crm.Modules.Sales.Domain.Leads;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Application.Rules;
using Sense.Crm.Modules.Workflows.Application.Tasks;
using Sense.Crm.Modules.Workflows.Domain;
using Sense.Crm.Modules.Workflows.Domain.Approvals;
using Sense.Crm.Modules.Workflows.Domain.Executions;
using Sense.Crm.Modules.Workflows.Domain.Rules;
using Sense.Crm.Modules.Workflows.Infrastructure.Conductor;
using Sense.Crm.Shared.Kernel.Results;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Workflows.Tests.Domain;

/// <summary>Kural parametre doğrulaması (tür başına şema, alan hataları, varsayılanlar).</summary>
public sealed class RuleParamsParserTests
{
    private static readonly Guid RoleId = Guid.NewGuid();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void LeadAssignment_Valid_DefaultsFollowUpHoursAndDedupesSources()
    {
        var (parsed, errors) = RuleParamsParser.Parse(WorkflowRuleKind.LeadAssignment, Json($$"""{"assigneeRoleId":"{{RoleId}}","sources":["web","web","referral"]}"""));

        errors.ShouldBeEmpty();
        var lead = parsed.ShouldBeOfType<LeadAssignmentParams>();
        (lead.AssigneeRoleId, lead.FollowUpHours).ShouldBe((RoleId, 24));
        lead.Sources.ShouldBe(["web", "referral"]);
        lead.Matches("web").ShouldBeTrue();
        lead.Matches("campaign").ShouldBeFalse();
    }

    [Fact]
    public void LeadAssignment_EmptyOrMissingSources_MatchesEverySource()
    {
        var (missing, _) = RuleParamsParser.Parse(WorkflowRuleKind.LeadAssignment, Json($$"""{"assigneeRoleId":"{{RoleId}}","followUpHours":720}"""));
        var (empty, _) = RuleParamsParser.Parse(WorkflowRuleKind.LeadAssignment, Json($$"""{"assigneeRoleId":"{{RoleId}}","sources":[]}"""));

        LeadSources.All.ShouldAllBe(s => ((LeadAssignmentParams)missing!).Matches(s) && ((LeadAssignmentParams)empty!).Matches(s));
        ((LeadAssignmentParams)missing!).FollowUpHours.ShouldBe(720);
    }

    [Theory]
    [InlineData("""{"sources":["web"]}""", "assigneeRoleId", RuleParamsParser.Required)]
    [InlineData("""{"assigneeRoleId":"not-a-guid"}""", "assigneeRoleId", RuleParamsParser.InvalidRoleId)]
    [InlineData("""{"assigneeRoleId":"00000000-0000-0000-0000-000000000000"}""", "assigneeRoleId", RuleParamsParser.InvalidRoleId)]
    [InlineData("""{"assigneeRoleId":"ROLE","sources":["fax"]}""", "sources", RuleParamsParser.InvalidSource)]
    [InlineData("""{"assigneeRoleId":"ROLE","sources":"web"}""", "sources", RuleParamsParser.InvalidSource)]
    [InlineData("""{"assigneeRoleId":"ROLE","followUpHours":0}""", "followUpHours", RuleParamsParser.InvalidFollowUpHours)]
    [InlineData("""{"assigneeRoleId":"ROLE","followUpHours":721}""", "followUpHours", RuleParamsParser.InvalidFollowUpHours)]
    [InlineData("""{"assigneeRoleId":"ROLE","followUpHours":1.5}""", "followUpHours", RuleParamsParser.InvalidFollowUpHours)]
    [InlineData("""{"assigneeRoleId":"ROLE","followUpHours":"24"}""", "followUpHours", RuleParamsParser.InvalidFollowUpHours)]
    public void LeadAssignment_Invalid_ReportsFieldError(string json, string field, string message)
    {
        var (parsed, errors) = RuleParamsParser.Parse(WorkflowRuleKind.LeadAssignment, Json(json.Replace("ROLE", RoleId.ToString(), StringComparison.Ordinal)));

        parsed.ShouldBeNull();
        errors.ShouldContain(new ParamError(field, message));
    }

    [Fact]
    public void DealApproval_Valid_AmountThresholdIsInclusive()
    {
        var (parsed, errors) = RuleParamsParser.Parse(WorkflowRuleKind.DealApproval, Json($$"""{"minAmount":50000.5,"approverRoleId":"{{RoleId}}"}"""));

        errors.ShouldBeEmpty();
        var deal = parsed.ShouldBeOfType<DealApprovalParams>();
        deal.MinAmount.ShouldBe(50000.5m);
        deal.Matches(50000.5m).ShouldBeTrue();
        deal.Matches(50000.49m).ShouldBeFalse();
        deal.Matches(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"approverRoleId":"ROLE"}""", "minAmount", RuleParamsParser.Required)]
    [InlineData("""{"minAmount":0,"approverRoleId":"ROLE"}""", "minAmount", RuleParamsParser.InvalidMinAmount)]
    [InlineData("""{"minAmount":-5,"approverRoleId":"ROLE"}""", "minAmount", RuleParamsParser.InvalidMinAmount)]
    [InlineData("""{"minAmount":"100","approverRoleId":"ROLE"}""", "minAmount", RuleParamsParser.InvalidMinAmount)]
    [InlineData("""{"minAmount":100}""", "approverRoleId", RuleParamsParser.Required)]
    public void DealApproval_Invalid_ReportsFieldError(string json, string field, string message)
    {
        var (parsed, errors) = RuleParamsParser.Parse(WorkflowRuleKind.DealApproval, Json(json.Replace("ROLE", RoleId.ToString(), StringComparison.Ordinal)));

        parsed.ShouldBeNull();
        errors.ShouldContain(new ParamError(field, message));
    }

    [Fact]
    public void MissingOrNonObjectParams_AreRejected()
    {
        RuleParamsParser.Parse(WorkflowRuleKind.DealApproval, null).Errors.Select(e => e.Field).ShouldBe(["minAmount", "approverRoleId"], ignoreOrder: true);
        RuleParamsParser.Parse(WorkflowRuleKind.LeadAssignment, Json("[1]")).Errors.ShouldContain(new ParamError(string.Empty, WorkflowsErrors.InvalidParams));
    }
}

/// <summary>Round-robin: en az açık lead, eşitlikte en eski atama, sonra kimlik.</summary>
public sealed class RoundRobinSelectorTests
{
    private static AssigneeCandidate Candidate(int open, DateTime? last, byte id = 1) =>
        new(new Guid(id, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]), "u" + id, open, last);

    [Fact]
    public void Pick_PrefersFewestOpenLeads()
    {
        var pick = RoundRobinSelector.Pick([Candidate(3, null, 1), Candidate(1, DateTime.UtcNow, 2), Candidate(2, null, 3)]);
        pick!.DisplayName.ShouldBe("u2");
    }

    [Fact]
    public void Pick_TieBreaksByLongestSinceLastAssignment_NeverAssignedFirst()
    {
        var now = DateTime.UtcNow;
        RoundRobinSelector.Pick([Candidate(2, now.AddHours(-1), 1), Candidate(2, now.AddHours(-5), 2), Candidate(2, now.AddMinutes(-1), 3)])!.DisplayName.ShouldBe("u2");
        RoundRobinSelector.Pick([Candidate(0, now.AddHours(-9), 1), Candidate(0, null, 2)])!.DisplayName.ShouldBe("u2");
    }

    [Fact]
    public void Pick_FullTie_IsDeterministicByUserId_AndEmptyPoolYieldsNull()
    {
        RoundRobinSelector.Pick([Candidate(0, null, 9), Candidate(0, null, 4), Candidate(0, null, 6)])!.DisplayName.ShouldBe("u4");
        RoundRobinSelector.Pick([]).ShouldBeNull();
    }

    [Fact]
    public void Rotation_AssigningEachPickAdvancesToTheNextUser()
    {
        // Üç kullanıcı, herkes 0 açık lead: her atamada seçilen kişinin açık lead sayısı artar, zamanı ilerler → sıra döner.
        var pool = new List<AssigneeCandidate> { Candidate(0, null, 1), Candidate(0, null, 2), Candidate(0, null, 3) };
        var clock = DateTime.UtcNow;
        var order = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var chosen = RoundRobinSelector.Pick(pool)!;
            order.Add(chosen.DisplayName);
            pool[pool.IndexOf(chosen)] = chosen with { OpenLeadCount = chosen.OpenLeadCount + 1, LastAssignedAt = clock.AddSeconds(i) };
        }

        order.ShouldBe(["u1", "u2", "u3", "u1", "u2", "u3"]);
    }
}

public sealed class WorkflowRuleDomainTests
{
    [Fact]
    public void Create_StoresTrimmedNameAndTypedParams_UpdateReplaces()
    {
        var role = Guid.NewGuid();
        var rule = WorkflowRule.Create(Guid.NewGuid(), "  Atama  ", WorkflowRuleKind.LeadAssignment, new LeadAssignmentParams(["web"], role, 12), isEnabled: true);

        (rule.Name, rule.IsEnabled, rule.Kind).ShouldBe(("Atama", true, WorkflowRuleKind.LeadAssignment));
        rule.LeadAssignment.ShouldBe(new LeadAssignmentParams(["web"], role, 12), new LeadAssignmentParamsComparer());
        Should.Throw<InvalidOperationException>(() => rule.DealApproval);

        var approver = Guid.NewGuid();
        rule.Update("Onay", WorkflowRuleKind.DealApproval, new DealApprovalParams(1000m, approver));
        (rule.Name, rule.Kind, rule.DealApproval.MinAmount, rule.DealApproval.ApproverRoleId).ShouldBe(("Onay", WorkflowRuleKind.DealApproval, 1000m, approver));

        rule.SetEnabled(false);
        rule.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Create_RejectsParamsOfTheWrongKind()
    {
        Should.Throw<ArgumentException>(() => WorkflowRule.Create(Guid.NewGuid(), "x", WorkflowRuleKind.DealApproval, new LeadAssignmentParams([], Guid.NewGuid(), 24), true));
    }

    private sealed class LeadAssignmentParamsComparer : IEqualityComparer<LeadAssignmentParams>
    {
        public bool Equals(LeadAssignmentParams? x, LeadAssignmentParams? y) =>
            x is not null && y is not null && x.AssigneeRoleId == y.AssigneeRoleId && x.FollowUpHours == y.FollowUpHours && x.Sources.SequenceEqual(y.Sources);

        public int GetHashCode(LeadAssignmentParams obj) => obj.AssigneeRoleId.GetHashCode();
    }
}

public sealed class ApprovalDomainTests
{
    private static Approval NewApproval() =>
        Approval.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Fırsat onayı: X", ExecutionSubjectType.Deal, Guid.NewGuid(), "X", 100m, "TRY", DateTime.UtcNow);

    [Fact]
    public void Approve_DoesNotRequireComment_AndFreezesTheApproval()
    {
        var approval = NewApproval();

        approval.Decide(ApprovalDecision.Approve, null, DateTime.UtcNow).IsSuccess.ShouldBeTrue();
        (approval.Status, approval.Comment).ShouldBe((ApprovalStatus.Approved, null));
        approval.DecidedAt.ShouldNotBeNull();

        approval.Decide(ApprovalDecision.Reject, "sonradan", DateTime.UtcNow).Error.Code.ShouldBe(WorkflowsErrors.ApprovalAlreadyDecided);
        approval.Cancel(DateTime.UtcNow).ShouldBeFalse();
        approval.Status.ShouldBe(ApprovalStatus.Approved);
    }

    [Fact]
    public void Reject_RequiresComment()
    {
        var approval = NewApproval();

        var blank = approval.Decide(ApprovalDecision.Reject, "   ", DateTime.UtcNow);
        blank.IsFailure.ShouldBeTrue();
        (blank.Error.Code, blank.Error.Type).ShouldBe((WorkflowsErrors.CommentRequired, ErrorType.Validation));
        approval.Status.ShouldBe(ApprovalStatus.Pending);

        approval.Decide(ApprovalDecision.Reject, " Bütçe yok ", DateTime.UtcNow).IsSuccess.ShouldBeTrue();
        (approval.Status, approval.Comment).ShouldBe((ApprovalStatus.Rejected, "Bütçe yok"));
    }

    [Fact]
    public void Cancel_OnlyPending_AndCancelledCannotBeDecided()
    {
        var approval = NewApproval();

        approval.Cancel(DateTime.UtcNow).ShouldBeTrue();
        approval.Status.ShouldBe(ApprovalStatus.Cancelled);
        approval.Decide(ApprovalDecision.Approve, null, DateTime.UtcNow).Error.Type.ShouldBe(ErrorType.Conflict);
    }
}

public sealed class WorkflowExecutionDomainTests
{
    private static WorkflowExecution NewExecution() =>
        WorkflowExecution.Start(Guid.NewGuid(), Guid.NewGuid(), "Kural", WorkflowRuleKind.LeadAssignment, Guid.NewGuid(), 1, ExecutionSubjectType.Lead, Guid.NewGuid(), "Ali", "{}", DateTime.UtcNow);

    [Fact]
    public void EndsOnce_FirstTransitionWins()
    {
        var execution = NewExecution();
        execution.IsRunning.ShouldBeTrue();

        execution.MarkFailed("no_assignee", DateTime.UtcNow).ShouldBeTrue();
        (execution.Status, execution.Error).ShouldBe((ExecutionStatus.Failed, "no_assignee"));
        execution.EndedAt.ShouldNotBeNull();

        execution.MarkCompleted(DateTime.UtcNow).ShouldBeFalse();
        execution.MarkTerminated(DateTime.UtcNow).ShouldBeFalse();
        execution.Status.ShouldBe(ExecutionStatus.Failed);
    }

    [Fact]
    public void Completed_ClearsError_TerminatedIsTerminal()
    {
        var completed = NewExecution();
        completed.MarkCompleted(DateTime.UtcNow).ShouldBeTrue();
        (completed.Status, completed.Error).ShouldBe((ExecutionStatus.Completed, null));

        var terminated = NewExecution();
        terminated.MarkTerminated(DateTime.UtcNow).ShouldBeTrue();
        terminated.MarkFailed("x", DateTime.UtcNow).ShouldBeFalse();
    }

    [Fact]
    public void AttachEngine_StoresId_LongErrorsAreTruncated()
    {
        var execution = NewExecution();
        execution.AttachEngine("wf-123");
        execution.EngineWorkflowId.ShouldBe("wf-123");

        execution.MarkFailed(new string('x', 5000), DateTime.UtcNow);
        execution.Error!.Length.ShouldBe(WorkflowLimits.ErrorMaxLength);
    }
}

/// <summary>Lead sahipliği (workflow atamasının kullandığı domain kuralı).</summary>
public sealed class LeadOwnerDomainTests
{
    [Fact]
    public void AssignOwner_ChangesOwnerAndStampsAssignmentTime_ConvertedLeadIsReadOnly()
    {
        var lead = Lead.Create(Guid.NewGuid(), "Ali", "Yilmaz", "Acme", Guid.NewGuid());
        lead.OwnerAssignedAt.ShouldBeNull();
        var owner = Guid.NewGuid();
        var at = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        lead.AssignOwner(owner, at).IsSuccess.ShouldBeTrue();
        (lead.OwnerUserId, lead.OwnerAssignedAt).ShouldBe((owner, at));

        lead.Convert(Guid.NewGuid(), Guid.NewGuid(), null, at).IsSuccess.ShouldBeTrue();
        lead.AssignOwner(Guid.NewGuid(), at).Error.Code.ShouldBe("lead.already_converted");
        lead.OwnerUserId.ShouldBe(owner);
    }
}

/// <summary>Kodda sürümlü Conductor tanımları: iki workflow, görev tanımı eşleşmesi, HUMAN referansı.</summary>
public sealed class WorkflowDefinitionsTests
{
    [Fact]
    public void BothWorkflows_AreDefinedAtTheCurrentVersion()
    {
        foreach (var (name, kind) in new[] { (WorkflowNames.LeadAssignmentWorkflow, WorkflowRuleKind.LeadAssignment), (WorkflowNames.DealApprovalWorkflow, WorkflowRuleKind.DealApproval) })
        {
            WorkflowNames.WorkflowFor(kind).ShouldBe(name);
            var definition = WorkflowDefinitions.Workflow(name, WorkflowNames.DefinitionVersion);
            ((JsonArray)definition["tasks"]!).ShouldNotBeEmpty();
            ((string?)definition["ownerEmail"]).ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void EverySimpleTask_HasATaskDefinition_AndAWorkerTypeInTheCatalog()
    {
        var simpleTasks = WorkflowDefinitions.Workflows
            .SelectMany(w => ((JsonArray)w["tasks"]!).OfType<JsonObject>())
            .Where(t => (string?)t["type"] == "SIMPLE")
            .Select(t => (string)t["name"]!)
            .Distinct()
            .ToList();

        simpleTasks.ShouldBe(WorkflowNames.TaskTypes, ignoreOrder: true);
        WorkflowDefinitions.Tasks.Select(t => (string)t["name"]!).ShouldBe(WorkflowNames.TaskTypes, ignoreOrder: true);
        WorkflowDefinitions.Tasks.ShouldAllBe(t => (int)t["retryCount"]! == 3 && (string)t["retryLogic"]! == "EXPONENTIAL_BACKOFF");
    }

    [Fact]
    public void DealApproval_WaitsOnAHumanTask_WithTheReferenceTheApiCompletes()
    {
        var tasks = ((JsonArray)WorkflowDefinitions.Workflow(WorkflowNames.DealApprovalWorkflow, 1)["tasks"]!).OfType<JsonObject>().ToList();

        var human = tasks.Single(t => (string?)t["type"] == "HUMAN");
        ((string)human["taskReferenceName"]!).ShouldBe(WorkflowNames.WaitDecisionTaskRef);
        tasks.IndexOf(human).ShouldBeGreaterThan(tasks.FindIndex(t => (string?)t["name"] == WorkflowNames.CreateApprovalsTask));
        tasks.IndexOf(human).ShouldBeLessThan(tasks.FindIndex(t => (string?)t["name"] == WorkflowNames.RecordDecisionTask));
    }
}

/// <summary>Conductor JSON → CRM durumu eşlemesi (hata nedeni önceliği, adım zamanları).</summary>
public sealed class ConductorMappingTests
{
    private static JsonObject Workflow(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void FailedWorkflow_UsesTheFailedTasksReason_NotTheGenericWorkflowMessage()
    {
        var state = ConductorWorkflowEngine.Map(Workflow("""
            {"status":"FAILED","reasonForIncompletion":"Task crm_assign_lead_owner failed with status: FAILED_WITH_TERMINAL_ERROR and reason: 'no_assignee'",
             "tasks":[{"seq":1,"taskType":"crm_assign_lead_owner","taskDefName":"crm_assign_lead_owner","status":"FAILED_WITH_TERMINAL_ERROR","reasonForIncompletion":"no_assignee","startTime":1789838730000,"endTime":1789838731000,"outputData":{}}]}
            """));

        (state.Status, state.Error).ShouldBe((ExecutionStatus.Failed, "no_assignee"));
        var step = state.Steps.Single();
        (step.Name, step.Status).ShouldBe(("crm_assign_lead_owner", "FAILED_WITH_TERMINAL_ERROR"));
        step.StartedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1789838730000).UtcDateTime);
        step.Output.ShouldBeNull();
    }

    [Fact]
    public void RunningWorkflow_MapsHumanTaskAndOrdersStepsBySequence()
    {
        var state = ConductorWorkflowEngine.Map(Workflow("""
            {"status":"RUNNING","tasks":[
              {"seq":2,"taskType":"HUMAN","taskDefName":"wait_for_decision","referenceTaskName":"wait_decision","status":"IN_PROGRESS","startTime":1789838732000,"endTime":0,"outputData":{}},
              {"seq":1,"taskType":"crm_create_approvals","taskDefName":"crm_create_approvals","status":"COMPLETED","startTime":1789838730000,"endTime":1789838731000,"outputData":{"approverCount":2}}]}
            """));

        (state.Status, state.Error).ShouldBe((ExecutionStatus.Running, null));
        state.Steps.Select(s => s.Name).ShouldBe(["crm_create_approvals", "wait_for_decision"]);
        state.Steps[0].Output!.Value.GetProperty("approverCount").GetInt32().ShouldBe(2);
        state.Steps[1].EndedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("COMPLETED", ExecutionStatus.Completed)]
    [InlineData("TERMINATED", ExecutionStatus.Terminated)]
    [InlineData("TIMED_OUT", ExecutionStatus.Failed)]
    [InlineData("PAUSED", ExecutionStatus.Running)]
    public void WorkflowStatuses_MapToExecutionStatuses(string conductor, ExecutionStatus expected) =>
        ConductorWorkflowEngine.Map(Workflow($$"""{"status":"{{conductor}}","tasks":[]}""")).Status.ShouldBe(expected);
}
