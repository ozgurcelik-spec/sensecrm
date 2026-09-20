using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Application.Tasks;
using Sense.Crm.Modules.Workflows.Infrastructure;
using Sense.Crm.Modules.Workflows.Infrastructure.Conductor;

namespace Sense.Crm.Worker.Workflows;

/// <summary>
/// Conductor görev işleyicileri (Milestone 4): her görev türü için toplu poll eder, görevi <see cref="WorkflowTaskRunner"/> ile
/// (kiracı = <c>input.tenantId</c>, sistem bağlamı, görev başına DI kapsamı) çalıştırır ve sonucu Conductor'a bildirir.
/// Sıralı işler (bir Worker'da bir görev aynı anda): round-robin atama yarışsız kalır. Sonuç türleri: tamamlandı →
/// <c>COMPLETED</c>; iş kuralı hatası → <c>FAILED_WITH_TERMINAL_ERROR</c> (yeniden denenmez, neden = kararlı kod); geçici hata →
/// <c>FAILED</c> (motor görev tanımına göre yeniden dener). Conductor at-least-once teslim eder: bildirim yolunda çökme görevi yeniden
/// kuyruğa düşürür (handler'lar mümkün olduğunca idempotenttir: onaylar (yürütme, onaylayıcı) benzersizdir).
/// Conductor'a ulaşılamazsa süreç çökmeden geri çekilerek yeniden dener.
/// </summary>
public sealed partial class ConductorTaskPollingService(
    IServiceScopeFactory scopes,
    WorkflowTaskRunner runner,
    IOptions<ConductorOptions> options,
    TimeProvider clock,
    ILogger<ConductorTaskPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            LogNotConfigured(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var client = scope.ServiceProvider.GetRequiredService<ConductorClient>();
                foreach (var taskType in WorkflowNames.TaskTypes)
                {
                    var tasks = await client.PollBatchAsync(taskType, settings.WorkerId, settings.PollBatchSize, settings.PollTimeoutMs, stoppingToken).ConfigureAwait(false);
                    foreach (var task in tasks)
                    {
                        await ProcessAsync(client, task, settings.WorkerId, stoppingToken).ConfigureAwait(false);
                        worked++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPollFailed(logger, ex.Message);
                await DelayAsync(ErrorDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (worked == 0)
            {
                await DelayAsync(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(ConductorClient client, ConductorTask task, string workerId, CancellationToken ct)
    {
        // M7: plan/askı zorlaması (olay veri yoluyla aynı ITenantEntitlements): askıda/deneme bitmiş kiracıda ya da workflows modülü kapalıyken görev terminal başarısız olur.
        // Yürütme ailed olur; yeniden açılınca yönetici retry ile yeniden dener. Kiracı kimliği yalnız kapı içindir (görev girdisi güvenilmezdir; runner asıl doğrulamayı yapar).
        var blocked = await CheckEntitlementAsync(task, ct).ConfigureAwait(false);

        // Görev girdisi güvenilmezdir: runner, (tenantId, executionId, motor workflow kimliği) üçlüsünü workflow_executions ile doğrular (H1).
        var result = blocked ?? await runner.ExecuteAsync(task.TaskType, task.WorkflowInstanceId, task.InputData, ct).ConfigureAwait(false);
        var status = result.Succeeded
            ? ConductorStatuses.Completed
            : result.Terminal ? ConductorStatuses.FailedWithTerminalError : ConductorStatuses.Failed;

        try
        {
            await client.UpdateTaskAsync(task, workerId, status, result.Output, result.Reason, ct).ConfigureAwait(false);
            LogTaskProcessed(logger, task.TaskType, task.TaskId, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sonuç bildirilemedi: görev yanıt zaman aşımından sonra motorca yeniden kuyruğa alınır.
            LogUpdateFailed(logger, ex, task.TaskType, task.TaskId);
        }
    }

    /// <summary>Kiracı erişimi <c>full</c> değilse (<c>tenant_suspended</c>) ya da workflows kapalıysa (<c>module_disabled</c>) terminal sonuç; aksi null.</summary>
    private async Task<WorkflowTaskResult?> CheckEntitlementAsync(ConductorTask task, CancellationToken ct)
    {
        if (task.InputData.ValueKind != System.Text.Json.JsonValueKind.Object
            || !task.InputData.TryGetProperty("tenantId", out var raw)
            || !Guid.TryParse(raw.GetString(), out var tenantId))
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<Sense.Crm.Shared.Contracts.Entitlements.ITenantEntitlements>().GetAsync(tenantId, ct).ConfigureAwait(false);
        var (_, access) = snapshot.Evaluate(clock.GetUtcNow());
        if (access != Sense.Crm.Shared.Contracts.Entitlements.AccessLevel.Full)
        {
            return WorkflowTaskResult.Failed("tenant_suspended");
        }

        return snapshot.IsModuleEnabled(Sense.Crm.Shared.Contracts.Entitlements.GatedModules.Workflows) ? null : WorkflowTaskResult.Failed("module_disabled");
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // kapanıyor
        }
    }

    [LoggerMessage(EventId = 4100, Level = LogLevel.Warning, Message = "Conductor:BaseUrl is not configured; workflow task workers are disabled")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "Conductor polling failed ({Reason}); backing off")]
    private static partial void LogPollFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information, Message = "Workflow task {TaskType} ({TaskId}) reported as {Status}")]
    private static partial void LogTaskProcessed(ILogger logger, string taskType, string taskId, string status);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Error, Message = "Reporting workflow task {TaskType} ({TaskId}) result failed; Conductor will re-queue it")]
    private static partial void LogUpdateFailed(ILogger logger, Exception exception, string taskType, string taskId);
}

/// <summary>
/// Çalışan yürütmelerin durumunu Conductor'dan <c>workflow_executions</c>'a yansıtır (running → completed | failed | terminated) her
/// <c>Conductor:StatusSyncIntervalSeconds</c> saniyede bir. Hata süreci düşürmez, sonraki turda tekrar denenir.
/// </summary>
public sealed partial class ExecutionStatusSyncService(ExecutionSyncRunner runner, IOptions<ConductorOptions> options, ILogger<ExecutionStatusSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BaseUrl))
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.StatusSyncIntervalSeconds, 1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changed = await runner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (changed > 0)
                {
                    LogSynced(logger, changed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Veritabanı henüz hazır/migrate edilmemiş olabilir; süreç çökmeden bir sonraki turda tekrar denenir.
                LogSyncFailed(logger, ex.Message);
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // kapanıyor
            }
        }
    }

    [LoggerMessage(EventId = 4110, Level = LogLevel.Information, Message = "Workflow execution status synchronized for {Count} execution(s)")]
    private static partial void LogSynced(ILogger logger, int count);

    [LoggerMessage(EventId = 4111, Level = LogLevel.Warning, Message = "Workflow execution status sync failed ({Reason})")]
    private static partial void LogSyncFailed(ILogger logger, string reason);
}
