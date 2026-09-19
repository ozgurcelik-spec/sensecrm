using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Workflows.Application;

namespace Sense.Crm.Modules.Workflows.Infrastructure.Conductor;

/// <summary>
/// Kodda tutulan sürümlü Conductor tanımları (gömülü JSON kaynakları, <c>Definitions/*.json</c>). Bir tanım değişirse
/// sürüm artırılır (<c>*.v2.json</c>) ve <see cref="WorkflowNames.DefinitionVersion"/> güncellenir; çalışan yürütmeler kendi
/// sürümüyle biter. Sahte motor (testler) ve <see cref="ConductorDefinitionRegistrar"/> aynı kaynakları okur.
/// </summary>
public static class WorkflowDefinitions
{
    /// <summary>SIMPLE görevlerin ortak yeniden deneme/zaman aşımı ayarı (geçici hata: 3 deneme, üstel geri çekilme).</summary>
    private const int RetryCount = 3;
    private const int RetryDelaySeconds = 5;
    private const int ResponseTimeoutSeconds = 120;
    private const string OwnerEmail = "crm@algosense.com.tr";

    public static IReadOnlyList<JsonObject> Workflows { get; } = LoadWorkflows();

    /// <summary>Görev tanımları (ortak yeniden deneme ayarıyla zenginleştirilmiş).</summary>
    public static IReadOnlyList<JsonObject> Tasks { get; } = LoadTasks();

    public static JsonObject Workflow(string name, int version) =>
        Workflows.Single(w => (string?)w["name"] == name && (int?)w["version"] == version);

    private static List<JsonObject> LoadWorkflows() =>
        ResourceNames().Where(n => !n.Contains("task_definitions", StringComparison.Ordinal))
            .Select(n => (JsonObject)JsonNode.Parse(Read(n))!)
            .ToList();

    private static List<JsonObject> LoadTasks() =>
        ((JsonArray)JsonNode.Parse(Read(ResourceNames().Single(n => n.Contains("task_definitions", StringComparison.Ordinal))))!)
            .OfType<JsonObject>()
            .Select(t =>
            {
                t["retryCount"] = RetryCount;
                t["retryLogic"] = "EXPONENTIAL_BACKOFF";
                t["retryDelaySeconds"] = RetryDelaySeconds;
                t["timeoutSeconds"] = 0;
                t["timeoutPolicy"] = "TIME_OUT_WF";
                t["responseTimeoutSeconds"] = ResponseTimeoutSeconds;
                t["ownerEmail"] = OwnerEmail;
                return t;
            })
            .ToList();

    private static IEnumerable<string> ResourceNames() =>
        typeof(WorkflowDefinitions).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal) && n.Contains(".Definitions.", StringComparison.Ordinal));

    private static string Read(string resource)
    {
        using var stream = typeof(WorkflowDefinitions).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Tanımları Conductor'a idempotent kaydeder (görev tanımları + workflow tanımları; <c>PUT</c> ile üzerine yazar). API ve Worker
/// başlangıcında arka planda çağrılır; başarısız olursa (Conductor henüz ayakta değil) ilk workflow başlatmasında yeniden denenir.
/// </summary>
public sealed class ConductorDefinitionRegistrar(IServiceScopeFactory scopes) : IWorkflowDefinitionRegistrar, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _registered;

    public void Dispose() => _gate.Dispose();

    public async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        if (_registered)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_registered)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<ConductorClient>();
            foreach (var task in WorkflowDefinitions.Tasks)
            {
                await client.UpsertTaskDefinitionAsync(task, cancellationToken).ConfigureAwait(false);
            }

            foreach (var workflow in WorkflowDefinitions.Workflows)
            {
                await client.PutWorkflowDefinitionAsync(workflow, cancellationToken).ConfigureAwait(false);
            }

            _registered = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
