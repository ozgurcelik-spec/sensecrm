using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Crm.Modules.Workflows.Infrastructure.Conductor;

/// <summary>Conductor bağlantı ayarları (<c>Conductor</c> bölümü). <see cref="BaseUrl"/> boşsa motor yapılandırılmamıştır (başlatmalar başarısız olur, API çalışmaya devam eder).</summary>
public sealed class ConductorOptions
{
    public const string SectionName = "Conductor";

    /// <summary>Örn. <c>http://localhost:18090</c> (REST köke <c>/api/</c> eklenir).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Tek istek zaman aşımı (uzun-poll dahil).</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Worker'ın poll toplu boyutu (görev türü başına).</summary>
    public int PollBatchSize { get; set; } = 5;

    /// <summary>Uzun-poll bekleme süresi (ms), görev türü başına.</summary>
    public int PollTimeoutMs { get; set; } = 300;

    /// <summary>Yürütme durumunun Conductor'dan senkronlanma aralığı (sn).</summary>
    public int StatusSyncIntervalSeconds { get; set; } = 5;

    /// <summary>Bir senkron turunda işlenen en çok çalışan yürütme.</summary>
    public int StatusSyncBatchSize { get; set; } = 200;

    /// <summary>Worker kimliği (Conductor'da görevi kimin aldığını gösterir).</summary>
    public string WorkerId { get; set; } = "crm-worker";
}

/// <summary>Conductor'un poll ettiğimiz görevi (yalnız kullandığımız alanlar).</summary>
public sealed record ConductorTask(string TaskId, string WorkflowInstanceId, string TaskType, JsonElement InputData);

/// <summary>Conductor'un workflow/task sonlanma durumları (yalnız kullandığımız alanlar).</summary>
public static class ConductorStatuses
{
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string FailedWithTerminalError = "FAILED_WITH_TERMINAL_ERROR";
    public const string InProgress = "IN_PROGRESS";
    public const string Scheduled = "SCHEDULED";
    public const string Running = "RUNNING";
    public const string Terminated = "TERMINATED";
    public const string TimedOut = "TIMED_OUT";
    public const string Canceled = "CANCELED";
    public const string Skipped = "SKIPPED";

    public static bool IsTerminalTask(string status) =>
        status is Completed or Failed or FailedWithTerminalError or TimedOut or Canceled or Skipped or "COMPLETED_WITH_ERRORS";
}

/// <summary>
/// Conductor OSS REST istemcisi (tipli <see cref="HttpClient"/>; dayanıklılık işleyicisi DI kaydında). Yalnız kullandığımız uçlar:
/// tanım kaydı (<c>metadata</c>), workflow başlat/oku/sonlandır, görev poll/güncelle. Ham JSON'u döner; CRM eşlemesi
/// <see cref="ConductorWorkflowEngine"/>'dedir.
/// </summary>
public sealed class ConductorClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Görev tanımını idempotent kaydeder: varsa günceller (<c>PUT</c>), yoksa oluşturur (<c>POST</c>; Conductor <c>PUT</c>'ta 404 döner).</summary>
    public async Task UpsertTaskDefinitionAsync(JsonObject definition, CancellationToken ct)
    {
        using (var update = await http.PutAsJsonAsync("metadata/taskdefs", definition, Json, ct).ConfigureAwait(false))
        {
            if (update.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(update, ct).ConfigureAwait(false);
                return;
            }
        }

        using var create = await http.PostAsJsonAsync("metadata/taskdefs", new JsonArray(definition.DeepClone()), Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(create, ct).ConfigureAwait(false);
    }

    public async Task PutWorkflowDefinitionAsync(JsonNode definition, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync("metadata/workflow", new JsonArray(definition.DeepClone()), Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Workflow'u başlatır; workflow kimliğini döner.</summary>
    public async Task<string> StartWorkflowAsync(string name, int version, string correlationId, JsonObject input, CancellationToken ct)
    {
        var body = new JsonObject { ["name"] = name, ["version"] = version, ["correlationId"] = correlationId, ["input"] = input.DeepClone() };
        using var response = await http.PostAsJsonAsync("workflow", body, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Trim('"');
    }

    /// <summary>Workflow + görevleri; bulunamazsa null.</summary>
    public async Task<JsonObject?> GetWorkflowAsync(string workflowId, CancellationToken ct)
    {
        using var response = await http.GetAsync(string.Create(CultureInfo.InvariantCulture, $"workflow/{Uri.EscapeDataString(workflowId)}?includeTasks=true"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct).ConfigureAwait(false))!;
    }

    public async Task TerminateWorkflowAsync(string workflowId, string? reason, CancellationToken ct)
    {
        var url = $"workflow/{Uri.EscapeDataString(workflowId)}?reason={Uri.EscapeDataString(reason ?? string.Empty)}";
        using var response = await http.DeleteAsync(url, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Workflow içindeki görevi (referans adıyla) verilen durumla günceller — HUMAN/WAIT görevini tamamlamak için.</summary>
    public async Task UpdateTaskByReferenceAsync(string workflowId, string taskReferenceName, string status, string workerId, JsonObject output, CancellationToken ct)
    {
        var url = $"tasks/{Uri.EscapeDataString(workflowId)}/{Uri.EscapeDataString(taskReferenceName)}/{status}?workerid={Uri.EscapeDataString(workerId)}";
        using var response = await http.PostAsJsonAsync(url, output, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Görev türü için toplu poll (uzun-poll <paramref name="timeoutMs"/>).</summary>
    public async Task<IReadOnlyList<ConductorTask>> PollBatchAsync(string taskType, string workerId, int count, int timeoutMs, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"tasks/poll/batch/{Uri.EscapeDataString(taskType)}?workerid={Uri.EscapeDataString(workerId)}&count={count}&timeout={timeoutMs}");
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var tasks = await response.Content.ReadFromJsonAsync<JsonArray>(Json, ct).ConfigureAwait(false);
        return tasks is null
            ? []
            : tasks.OfType<JsonObject>()
                .Select(t => new ConductorTask(
                    t["taskId"]!.GetValue<string>(),
                    t["workflowInstanceId"]!.GetValue<string>(),
                    t["taskType"]!.GetValue<string>(),
                    JsonSerializer.SerializeToElement(t["inputData"] ?? new JsonObject(), Json)))
                .ToList();
    }

    /// <summary>Poll edilen görevin sonucunu bildirir (<c>COMPLETED</c>, <c>FAILED</c>, <c>FAILED_WITH_TERMINAL_ERROR</c>).</summary>
    public async Task UpdateTaskAsync(ConductorTask task, string workerId, string status, JsonObject? output, string? reason, CancellationToken ct)
    {
        var result = new JsonObject
        {
            ["workflowInstanceId"] = task.WorkflowInstanceId,
            ["taskId"] = task.TaskId,
            ["workerId"] = workerId,
            ["status"] = status,
            ["outputData"] = output?.DeepClone() ?? new JsonObject(),
        };
        if (reason is not null)
        {
            result["reasonForIncompletion"] = reason.Length > 500 ? reason[..500] : reason;
        }

        using var response = await http.PostAsJsonAsync("tasks", result, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ConductorException(response.StatusCode, body.Length > 500 ? body[..500] : body);
    }
}

/// <summary>Conductor 4xx/5xx yanıtı.</summary>
public sealed class ConductorException(HttpStatusCode statusCode, string body)
    : Exception(new StringBuilder("Conductor responded ").Append((int)statusCode).Append(": ").Append(body).ToString())
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
