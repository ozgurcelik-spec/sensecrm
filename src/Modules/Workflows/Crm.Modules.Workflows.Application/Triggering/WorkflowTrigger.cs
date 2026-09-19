using System.Text.Json.Nodes;
using Crm.Modules.Sales.Contracts;
using Crm.Modules.Workflows.Domain;
using Crm.Modules.Workflows.Domain.Executions;
using Crm.Modules.Workflows.Domain.Rules;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Events;

namespace Crm.Modules.Workflows.Application.Triggering;

/// <summary>Workflow girdisi (Conductor <c>input</c>) kurucuları. Konu bilgisi yürütmede saklanır; kural parametreleri çalışma anındaki kuraldan gelir.</summary>
public static class WorkflowInputs
{
    /// <summary>Yürütmenin saklanan girdisi (konu): lead için <c>leadId, leadName</c>.</summary>
    public static JsonObject LeadSubject(Guid leadId, string leadName) => new() { ["leadId"] = leadId, ["leadName"] = leadName };

    /// <summary>Yürütmenin saklanan girdisi (konu): fırsat için <c>dealId, dealName, amount, currency</c>.</summary>
    public static JsonObject DealSubject(Guid dealId, string dealName, decimal? amount, string currency) =>
        new() { ["dealId"] = dealId, ["dealName"] = dealName, ["amount"] = amount, ["currency"] = currency };

    /// <summary>Motora gönderilen tam girdi: saklanan konu + <c>tenantId, executionId, ruleId</c> + kuralın güncel parametreleri.</summary>
    public static JsonObject Build(WorkflowExecution execution, WorkflowRule rule)
    {
        var input = (JsonObject)JsonNode.Parse(execution.InputJson)!;
        input["tenantId"] = execution.TenantId;
        input["executionId"] = execution.Id;
        input["ruleId"] = rule.Id;
        switch (rule.Kind)
        {
            case WorkflowRuleKind.LeadAssignment:
                var lead = rule.LeadAssignment;
                input["assigneeRoleId"] = lead.AssigneeRoleId;
                input["followUpHours"] = lead.FollowUpHours;
                break;
            case WorkflowRuleKind.DealApproval:
                input["approverRoleId"] = rule.DealApproval.ApproverRoleId;
                break;
        }

        return input;
    }
}

/// <summary>
/// Yürütmeyi motorda başlatır. Motor hatası yürütmeyi <c>failed</c> (<c>workflow.engine_unavailable</c>) yapar: kullanıcı arayüzden
/// "yeniden dene" diyebilir; çağıran değişiklikleri kaydeder.
/// </summary>
public sealed class WorkflowStarter(IWorkflowEngine engine, TimeProvider clock)
{
    /// <returns>Motor kabul ettiyse true; reddetti/ulaşılamadıysa yürütme failed işaretlenir ve false döner.</returns>
    public async Task<bool> StartAsync(WorkflowExecution execution, WorkflowRule rule, CancellationToken ct)
    {
        try
        {
            var engineId = await engine.StartAsync(
                new StartWorkflowRequest(WorkflowNames.WorkflowFor(rule.Kind), WorkflowNames.DefinitionVersion, execution.Id.ToString(), WorkflowInputs.Build(execution, rule)),
                ct).ConfigureAwait(false);
            execution.AttachEngine(engineId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            execution.MarkFailed(WorkflowsErrors.EngineUnavailable, clock.GetUtcNow().UtcDateTime);
            return false;
        }
    }
}

/// <summary>
/// Olay → kural değerlendirmesi → yürütme başlatma (Worker'da outbox olay yolu; API'de aynı handler'lar kayıtlıdır).
/// İdempotent: <c>(ruleId, eventId)</c> için yürütme satırı benzersizdir; olay yeniden teslim edilirse ikinci yürütme açılmaz
/// (motora hiç iletilememiş satır varsa yalnız başlatma tekrarlanır).
/// </summary>
public sealed class WorkflowTrigger(
    IWorkflowRuleRepository rules,
    IWorkflowExecutionRepository executions,
    IWorkflowsUnitOfWork unitOfWork,
    WorkflowStarter starter,
    IRecordLookup records,
    ITenantContext tenant,
    TimeProvider clock)
{
    public async Task OnLeadCreatedAsync(LeadCreated e, CancellationToken ct)
    {
        foreach (var rule in await rules.ListEnabledAsync(WorkflowRuleKind.LeadAssignment, ct).ConfigureAwait(false))
        {
            if (!rule.LeadAssignment.Matches(e.Source))
            {
                continue;
            }

            await RunAsync(rule, e.EventId, ExecutionSubjectType.Lead, e.LeadId, e.LeadName, WorkflowInputs.LeadSubject(e.LeadId, e.LeadName), ct).ConfigureAwait(false);
        }
    }

    public async Task OnDealStageChangedAsync(DealStageChangedIntegration e, CancellationToken ct)
    {
        if (!string.Equals(e.ToStageKind, DealStageKinds.Won, StringComparison.Ordinal))
        {
            return;
        }

        var matching = (await rules.ListEnabledAsync(WorkflowRuleKind.DealApproval, ct).ConfigureAwait(false))
            .Where(r => r.DealApproval.Matches(e.Amount))
            .ToList();
        if (matching.Count == 0)
        {
            return;
        }

        var name = await records.GetDisplayNameAsync(RecordType.Deal, e.DealId, ct).ConfigureAwait(false);
        if (name is null)
        {
            return; // fırsat bu arada silindi
        }

        foreach (var rule in matching)
        {
            await RunAsync(rule, e.EventId, ExecutionSubjectType.Deal, e.DealId, name, WorkflowInputs.DealSubject(e.DealId, name, e.Amount, e.Currency), ct).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(
        WorkflowRule rule,
        Guid eventId,
        ExecutionSubjectType subjectType,
        Guid subjectId,
        string subjectName,
        JsonObject subject,
        CancellationToken ct)
    {
        var execution = await executions.FindAsync(rule.Id, eventId, 1, ct).ConfigureAwait(false);
        if (execution is { EngineWorkflowId: not null } || execution is { IsRunning: false })
        {
            return; // bu olay bu kural için zaten işlendi
        }

        if (execution is null)
        {
            execution = WorkflowExecution.Start(
                tenant.TenantId,
                rule.Id,
                rule.Name,
                rule.Kind,
                eventId,
                attempt: 1,
                subjectType,
                subjectId,
                subjectName,
                subject.ToJsonString(),
                clock.GetUtcNow().UtcDateTime);
            executions.Add(execution);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await starter.StartAsync(execution, rule, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary><see cref="LeadCreated"/> tüketicisi: etkin <c>leadAssignment</c> kurallarını değerlendirir.</summary>
public sealed class LeadCreatedWorkflowHandler(WorkflowTrigger trigger) : IIntegrationEventHandler<LeadCreated>
{
    public Task Handle(LeadCreated integrationEvent, CancellationToken cancellationToken) => trigger.OnLeadCreatedAsync(integrationEvent, cancellationToken);
}

/// <summary><see cref="DealStageChangedIntegration"/> tüketicisi: kazanılan fırsatlar için etkin <c>dealApproval</c> kurallarını değerlendirir.</summary>
public sealed class DealStageChangedWorkflowHandler(WorkflowTrigger trigger) : IIntegrationEventHandler<DealStageChangedIntegration>
{
    public Task Handle(DealStageChangedIntegration integrationEvent, CancellationToken cancellationToken) => trigger.OnDealStageChangedAsync(integrationEvent, cancellationToken);
}
