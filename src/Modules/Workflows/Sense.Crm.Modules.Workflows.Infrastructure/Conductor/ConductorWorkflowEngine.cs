using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Domain.Executions;

namespace Sense.Crm.Modules.Workflows.Infrastructure.Conductor;

/// <summary>
/// <see cref="IWorkflowEngine"/>'in Conductor OSS uygulaması. Conductor durumlarını CRM yürütme durumlarına eşler; hata nedeni olarak
/// başarısız görevin <c>reasonForIncompletion</c>'ını (worker'ın yazdığı kararlı kod: <c>no_assignee</c> …) önceler.
/// </summary>
public sealed partial class ConductorWorkflowEngine(ConductorClient client, IWorkflowDefinitionRegistrar registrar, ILogger<ConductorWorkflowEngine> logger) : IWorkflowEngine
{
    /// <summary>HUMAN görevi henüz zamanlanmamışsa (workflow bir önceki görevden yeni çıktı) bekleme denemesi.</summary>
    private const int WaitTaskAttempts = 10;
    private const int WaitTaskDelayMs = 300;
    private const string ApiWorkerId = "crm-api";

    public async Task<string> StartAsync(StartWorkflowRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await registrar.EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
            return await client.StartWorkflowAsync(request.Name, request.Version, request.CorrelationId, request.Input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Çağıran (WorkflowStarter) yürütmeyi failed işaretler; nedeni yalnız burada loglanır.
            LogStartFailed(logger, ex, request.Name, request.CorrelationId);
            throw;
        }
    }

    public async Task<WorkflowState?> GetAsync(string workflowId, CancellationToken cancellationToken)
    {
        var workflow = await client.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return workflow is null ? null : Map(workflow);
    }

    public async Task TerminateAsync(string workflowId, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            await client.TerminateWorkflowAsync(workflowId, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (ConductorException)
        {
            // Zaten sonlanmış/tamamlanmışsa idempotent say; hâlâ çalışıyorsa hatayı ilet.
            var state = await GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
            if (state is null || state.Status == ExecutionStatus.Running)
            {
                throw;
            }
        }
    }

    public async Task RemoveAsync(string workflowId, CancellationToken cancellationToken)
    {
        // Çalışan yürütme önce sonlandırılır (idempotent), sonra kayıt kalıcı silinir.
        await TerminateAsync(workflowId, "tenant erasure", cancellationToken).ConfigureAwait(false);
        await client.RemoveWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteWaitTaskAsync(string workflowId, string taskReferenceName, IReadOnlyDictionary<string, object?> output, CancellationToken cancellationToken)
    {
        var body = new JsonObject();
        foreach (var (key, value) in output)
        {
            body[key] = value is null ? null : JsonValue.Create(value);
        }

        for (var attempt = 0; attempt < WaitTaskAttempts; attempt++)
        {
            var workflow = await client.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Conductor workflow {workflowId} was not found.");
            var task = (workflow["tasks"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => (string?)t["referenceTaskName"] == taskReferenceName);

            if (task is not null && ConductorStatuses.IsTerminalTask((string?)task["status"] ?? string.Empty))
            {
                return; // karar zaten işlenmiş (idempotent)
            }

            if (task is not null)
            {
                await client.UpdateTaskByReferenceAsync(workflowId, taskReferenceName, ConductorStatuses.Completed, ApiWorkerId, body, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (Map(workflow).Status != ExecutionStatus.Running)
            {
                throw new InvalidOperationException($"Conductor workflow {workflowId} is not running; wait task '{taskReferenceName}' cannot be completed.");
            }

            // Onay kayıtları workflow'un bir önceki görevinde yazılır; HUMAN görevi birkaç yüz ms sonra zamanlanır.
            await Task.Delay(WaitTaskDelayMs, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Wait task '{taskReferenceName}' of workflow {workflowId} was not scheduled in time.");
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Error, Message = "Starting Conductor workflow {Workflow} failed (correlation {CorrelationId})")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string workflow, string correlationId);

    public static WorkflowState Map(JsonObject workflow)
    {
        var status = MapStatus((string?)workflow["status"]);
        var tasks = (workflow["tasks"] as JsonArray)?.OfType<JsonObject>().OrderBy(t => (long?)t["seq"] ?? 0).ToList() ?? [];

        var steps = tasks.Select(t => new WorkflowStep(
            Name(t),
            (string?)t["status"] ?? string.Empty,
            FromEpochMs(t["startTime"]),
            FromEpochMs(t["endTime"]),
            t["outputData"] is JsonObject { Count: > 0 } output ? JsonSerializer.SerializeToElement(output) : null)).ToList();

        string? error = null;
        if (status == ExecutionStatus.Failed)
        {
            error = tasks.LastOrDefault(t => (string?)t["status"] is ConductorStatuses.Failed or ConductorStatuses.FailedWithTerminalError or ConductorStatuses.TimedOut)
                ?["reasonForIncompletion"]?.GetValue<string>();
            error = string.IsNullOrWhiteSpace(error) ? (string?)workflow["reasonForIncompletion"] : error;
        }

        return new WorkflowState(status, string.IsNullOrWhiteSpace(error) ? null : error, steps);
    }

    private static ExecutionStatus MapStatus(string? status) => status switch
    {
        ConductorStatuses.Completed => ExecutionStatus.Completed,
        ConductorStatuses.Failed or ConductorStatuses.TimedOut => ExecutionStatus.Failed,
        ConductorStatuses.Terminated => ExecutionStatus.Terminated,
        _ => ExecutionStatus.Running, // RUNNING, PAUSED
    };

    private static string Name(JsonObject task) =>
        (string?)task["taskDefName"] is { Length: > 0 } definition ? definition : (string?)task["taskType"] ?? (string?)task["referenceTaskName"] ?? string.Empty;

    private static DateTime? FromEpochMs(JsonNode? node) =>
        node is not null && (long?)node is > 0 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
}
