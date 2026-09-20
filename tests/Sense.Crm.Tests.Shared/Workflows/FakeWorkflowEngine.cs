using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Application.Tasks;
using Sense.Crm.Modules.Workflows.Domain.Executions;
using Sense.Crm.Modules.Workflows.Infrastructure;
using Sense.Crm.Modules.Workflows.Infrastructure.Conductor;

namespace Sense.Crm.Tests.Shared.Workflows;

/// <summary>
/// Testler için sahte workflow motoru: gerçek Conductor'un yerine geçer ama <b>gerçek tanımları</b> (<c>Definitions/*.json</c>) ve <b>gerçek
/// görev işleyicilerini</b> (<see cref="WorkflowTaskRunner"/>) çalıştırır. Başlatma anında SIMPLE görevleri sırayla eşzamanlı yürütür
/// (<c>${workflow.input.x}</c> / <c>${ref.output.y}</c> çözümleme), HUMAN görevinde bekler; <see cref="CompleteWaitTaskAsync"/> devam ettirir.
/// Geçici hata (<see cref="WorkflowTaskResult.Retry"/>) 3 denemeden sonra, iş kuralı hatası hemen workflow'u <c>failed</c> yapar
/// (neden = görev sonucu; Conductor gibi). Kesinti simülasyonu: <see cref="FailStart"/>, <see cref="FailComplete"/>.
/// </summary>
public sealed partial class FakeWorkflowEngine(IServiceProvider services) : IWorkflowEngine, IWorkflowDefinitionRegistrar
{
    private const int MaxTaskAttempts = 3;

    private static readonly Regex Expression = ExpressionRegex();

    private readonly ConcurrentDictionary<string, FakeWorkflow> _workflows = new();

    /// <summary>true iken <see cref="StartAsync"/> motor kesintisi gibi istisna fırlatır.</summary>
    public bool FailStart { get; set; }

    /// <summary>true iken <see cref="CompleteWaitTaskAsync"/> istisna fırlatır.</summary>
    public bool FailComplete { get; set; }

    /// <summary>Başlatılan tüm workflow'lar (motor kimliğine göre).</summary>
    public IReadOnlyDictionary<string, FakeWorkflow> Workflows => _workflows;

    public Task EnsureRegisteredAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> StartAsync(StartWorkflowRequest request, CancellationToken cancellationToken)
    {
        if (FailStart)
        {
            throw new InvalidOperationException("Simulated engine outage.");
        }

        var definition = WorkflowDefinitions.Workflow(request.Name, request.Version);
        var workflow = new FakeWorkflow(Guid.NewGuid().ToString(), request.Name, request.Input, (JsonArray)definition["tasks"]!) { CorrelationId = request.CorrelationId };
        _workflows[workflow.Id] = workflow;

        // Gerçek Conductor gibi: başlatma hemen döner, görevler ayrı bir işlemde (Worker) ve başlatan işlem commit edildikten SONRA
        // çalışır (görev işleyicileri yürütme satırını/onayları veritabanından okur ve motor kimliğini doğrular). Bekleyen iş
        // <see cref="DrainAsync"/> ile (istek bitince ve outbox boşaltılınca) yürütülür.
        _pending.Enqueue(() => RunAsync(workflow, CancellationToken.None));
        return Task.FromResult(workflow.Id);
    }

    private readonly ConcurrentQueue<Func<Task>> _pending = new();

    /// <summary>
    /// Bekleyen workflow işlerini (başlatma sonrası görevler, HUMAN görevi sonrası devam) çalıştırır. Test altyapısı her HTTP isteğinin
    /// ve outbox boşaltmanın ardından çağırır: böylece gerçek motorun "commit sonrası, ayrı işlemde" davranışı deterministik taklit edilir.
    /// </summary>
    public async Task DrainAsync()
    {
        while (_pending.TryDequeue(out var work))
        {
            await work().ConfigureAwait(false);
        }
    }

    public Task<WorkflowState?> GetAsync(string workflowId, CancellationToken cancellationToken) =>
        Task.FromResult(_workflows.TryGetValue(workflowId, out var workflow)
            ? new WorkflowState(workflow.Status, workflow.Error, workflow.Steps.ToList())
            : (WorkflowState?)null);

    /// <summary>KVKK imhasında <c>RemoveAsync</c> ile silinen motor kimlikleri (test doğrulaması).</summary>
    public IReadOnlyCollection<string> RemovedWorkflowIds => _removed.ToArray();

    private readonly ConcurrentQueue<string> _removed = new();

    /// <summary>C-SEC2 L2: ilişkilendirme kimliğiyle (CRM yürütme kimliği) başlatılmış workflow'lar — yetim yürütme temizliği testi için.</summary>
    public Task<IReadOnlyList<string>> FindIdsByCorrelationAsync(string workflowName, string correlationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_workflows.Values.Where(w => w.Name == workflowName && w.CorrelationId == correlationId).Select(w => w.Id).ToList());

    public Task RemoveAsync(string workflowId, CancellationToken cancellationToken)
    {
        _removed.Enqueue(workflowId);
        _workflows.TryRemove(workflowId, out _);
        return Task.CompletedTask;
    }

    public Task TerminateAsync(string workflowId, string? reason, CancellationToken cancellationToken)
    {
        if (_workflows.TryGetValue(workflowId, out var workflow) && workflow.Status == ExecutionStatus.Running)
        {
            workflow.Status = ExecutionStatus.Terminated;
        }

        return Task.CompletedTask;
    }

    public Task CompleteWaitTaskAsync(string workflowId, string taskReferenceName, IReadOnlyDictionary<string, object?> output, CancellationToken cancellationToken)
    {
        if (FailComplete)
        {
            throw new InvalidOperationException("Simulated engine outage.");
        }

        var workflow = _workflows[workflowId];
        if (workflow.Status != ExecutionStatus.Running || workflow.Waiting is not { } waiting || waiting.Ref != taskReferenceName)
        {
            return Task.CompletedTask; // idempotent
        }

        var body = new JsonObject();
        foreach (var (key, value) in output)
        {
            body[key] = value is null ? null : JsonValue.Create(value);
        }

        workflow.Outputs[waiting.Ref] = body;
        workflow.CompleteStep(body);
        workflow.Waiting = null;
        workflow.Index++;
        _pending.Enqueue(() => RunAsync(workflow, CancellationToken.None));
        return Task.CompletedTask;
    }

    private async Task RunAsync(FakeWorkflow workflow, CancellationToken ct)
    {
        var runner = services.GetRequiredService<WorkflowTaskRunner>();
        while (workflow.Status == ExecutionStatus.Running && workflow.Index < workflow.Tasks.Count)
        {
            var task = (JsonObject)workflow.Tasks[workflow.Index]!;
            var name = (string)task["name"]!;
            var reference = (string)task["taskReferenceName"]!;
            var type = (string)task["type"]!;

            if (type == "HUMAN")
            {
                workflow.Waiting = new WaitingTask(reference);
                workflow.Steps.Add(new WorkflowStep(name, ConductorStatuses.InProgress, DateTime.UtcNow, null, null));
                return;
            }

            var input = Resolve((JsonObject?)task["inputParameters"] ?? [], workflow);
            var inputElement = JsonSerializer.SerializeToElement(input);
            WorkflowTaskResult result = WorkflowTaskResult.Retry("not run");
            for (var attempt = 0; attempt < MaxTaskAttempts; attempt++)
            {
                result = await runner.ExecuteAsync(name, workflow.Id, inputElement, ct).ConfigureAwait(false);
                if (result.Succeeded || result.Terminal)
                {
                    break;
                }
            }

            if (result.Succeeded)
            {
                workflow.Outputs[reference] = result.Output ?? [];
                workflow.Steps.Add(new WorkflowStep(name, ConductorStatuses.Completed, DateTime.UtcNow, DateTime.UtcNow, ToElement(result.Output)));
                workflow.Index++;
            }
            else
            {
                workflow.Steps.Add(new WorkflowStep(name, result.Terminal ? ConductorStatuses.FailedWithTerminalError : ConductorStatuses.Failed, DateTime.UtcNow, DateTime.UtcNow, null));
                workflow.Status = ExecutionStatus.Failed;
                workflow.Error = result.Reason;
                return;
            }
        }

        if (workflow.Status == ExecutionStatus.Running && workflow.Index >= workflow.Tasks.Count)
        {
            workflow.Status = ExecutionStatus.Completed;
        }
    }

    private static JsonObject Resolve(JsonObject parameters, FakeWorkflow workflow)
    {
        var resolved = new JsonObject();
        foreach (var (key, value) in parameters)
        {
            resolved[key] = value is JsonValue v && v.TryGetValue<string>(out var text) && Expression.Match(text) is { Success: true } match
                ? Lookup(match.Groups["path"].Value, workflow)
                : value?.DeepClone();
        }

        return resolved;
    }

    private static JsonNode? Lookup(string path, FakeWorkflow workflow)
    {
        var parts = path.Split('.');
        JsonNode? current = parts[0] == "workflow"
            ? workflow.Input
            : workflow.Outputs.GetValueOrDefault(parts[0]);
        foreach (var part in parts.Skip(2))
        {
            current = (current as JsonObject)?[part];
        }

        return current?.DeepClone();
    }

    private static JsonElement? ToElement(JsonObject? output) => output is { Count: > 0 } ? JsonSerializer.SerializeToElement(output) : null;

    [GeneratedRegex(@"^\$\{(?<path>[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+)\}$")]
    private static partial Regex ExpressionRegex();
}

/// <summary>Sahte motordaki bir workflow örneği (test içi gözlem için açıktır).</summary>
public sealed class FakeWorkflow(string id, string name, JsonObject input, JsonArray tasks)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public JsonObject Input { get; } = input;

    /// <summary>Başlatma isteğindeki ilişkilendirme kimliği (CRM yürütme kimliği).</summary>
    public string? CorrelationId { get; set; }

    public JsonArray Tasks { get; } = tasks;

    public ExecutionStatus Status { get; set; } = ExecutionStatus.Running;

    public string? Error { get; set; }

    public int Index { get; set; }

    public List<WorkflowStep> Steps { get; } = [];

    public Dictionary<string, JsonObject> Outputs { get; } = [];

    public WaitingTask? Waiting { get; set; }

    public void CompleteStep(JsonObject output)
    {
        var index = Steps.FindLastIndex(s => s.Status == ConductorStatuses.InProgress);
        if (index >= 0)
        {
            var step = Steps[index];
            Steps[index] = step with { Status = ConductorStatuses.Completed, EndedAt = DateTime.UtcNow, Output = JsonSerializer.SerializeToElement(output) };
        }

    }
}

/// <summary>Sahte motorda HUMAN görevinin beklediği referans adı.</summary>
public sealed record WaitingTask(string Ref);

/// <summary>
/// Her HTTP isteği bittikten SONRA (işlem commit edilmiş, yanıt gövdesi tamamlanmadan) sahte motorun bekleyen işlerini çalıştırır.
/// <c>HttpClient</c> varsayılan olarak yanıtın tamamlanmasını beklediği için testler, workflow görevlerinin (Worker taklidi) bittiği
/// durumu görür — gerçek motorun "commit sonrası, ayrı işlemde" davranışıyla aynı sıra.
/// </summary>
public sealed class FakeEngineDrainStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, pipeline) =>
            {
                await pipeline().ConfigureAwait(false);
                await context.RequestServices.GetRequiredService<FakeWorkflowEngine>().DrainAsync().ConfigureAwait(false);
            });
            next(app);
        };
}
