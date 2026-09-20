using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Modules.Workflows.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Observability;
using Sense.Crm.Shared.Infrastructure.Observability;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Worker.Observability;

/// <summary>
/// Örneklenen (gauge) işletim metrikleri (C-OPS1): outbox bekleyen/ölü/en eski yaş (modül başına), çalışan workflow yürütmeleri ve KVKK silme talepleri.
/// Her <see cref="Interval"/> (15 sn) bir kez, dar ve indeksli sorgularla (<c>processed_at IS NULL</c> kısmi indeksi, <c>status = 'Running'</c> kısmi indeksi,
/// <c>deletion_requests(status, scheduled_for)</c>) okunur; kiracı kimliği/adı okunmaz, yalnız sayılar ve modül/durum etiketleri yayınlanır.
/// Tek Worker kopyası varsayılır; birden çok kopyada her biri aynı değeri yayınlar (Prometheus tarafında <c>max by (module)</c> ile birleştirilir).
/// Hata süreci düşürmez; son başarılı değer korunur ve sonraki turda yeniden denenir.
/// </summary>
public sealed partial class WorkerMetricsSampler(IServiceScopeFactory scopes, ILogger<WorkerMetricsSampler> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CrmMetrics.Gauges.Ensure();
        PrimeCounters();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SampleOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Veritabanı henüz hazır/migrate edilmemiş olabilir; eski değer korunur, sonraki turda tekrar denenir.
                LogSampleFailed(logger, ex.GetType().Name);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Sabit değer kümeli Worker sayaçlarını 0 ile oluşturur: ilk olay (ör. ilk ölü mesaj/başarısız görev) <c>increase()</c> ile görülebilsin.</summary>
    private void PrimeCounters()
    {
        CrmMetrics.PrimeBackground();
        using var scope = scopes.CreateScope();
        foreach (var context in scope.ServiceProvider.GetServices<ModuleDbContext>())
        {
            CrmMetrics.PrimeModule(context.ModuleName);
        }

        foreach (var taskType in Sense.Crm.Modules.Workflows.Application.WorkflowNames.TaskTypes)
        {
            CrmMetrics.PrimeTaskType(taskType);
        }

        CrmMetrics.PrimeBackgroundJob(nameof(Sense.Crm.Worker.Platform.UsageSnapshotService));
        CrmMetrics.PrimeBackgroundJob(nameof(Sense.Crm.Worker.Platform.TenantErasureService));
    }

    public async Task SampleOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var outbox = new List<(string, long, long, double)>();
        foreach (var context in sp.GetServices<ModuleDbContext>())
        {
            var row = await QueryOutboxAsync(context, ct).ConfigureAwait(false);
            outbox.Add((context.ModuleName, row.Pending, row.Dead, row.OldestPendingAgeSeconds));
        }

        CrmMetrics.Gauges.SetOutbox(outbox);

        if (sp.GetService<WorkflowsDbContext>() is { } workflows)
        {
            var running = await ScalarAsync(workflows, "SELECT count(*) FROM workflows.workflow_executions WHERE status = 'Running'", ct).ConfigureAwait(false);
            CrmMetrics.Gauges.SetWorkflowsRunning(Convert.ToInt64(running, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (sp.GetService<PlatformDbContext>() is { } platform)
        {
            await SampleDeletionAsync(platform, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(long Pending, long Dead, double OldestPendingAgeSeconds)> QueryOutboxAsync(ModuleDbContext context, CancellationToken ct)
    {
        // Şema adı kod sabitidir (DbContext.SchemaName), kullanıcı girdisi değildir.
        var sql = $"""
            SELECT count(*) FILTER (WHERE NOT is_dead),
                   count(*) FILTER (WHERE is_dead),
                   COALESCE(EXTRACT(EPOCH FROM (now() - min(occurred_at) FILTER (WHERE NOT is_dead))), 0)::float8
            FROM "{context.Schema}"."outbox_messages"
            WHERE processed_at IS NULL
            """;
        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Şema adı sabit koddan gelir.
            command.CommandText = sql;
#pragma warning restore CA2100
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2))
                : (0, 0, 0);
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<object?> ScalarAsync(DbContext context, string sql, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Sorgu metni sabittir.
            command.CommandText = sql;
#pragma warning restore CA2100
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task SampleDeletionAsync(PlatformDbContext platform, CancellationToken ct)
    {
        var connection = platform.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var byStatus = new Dictionary<string, long> { ["scheduled"] = 0, ["running"] = 0, ["failed"] = 0 };
            var oldest = 0d;
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT status, count(*),
                       COALESCE(max(EXTRACT(EPOCH FROM (now() - scheduled_for))) FILTER (WHERE scheduled_for <= now()), 0)::float8
                FROM platform.deletion_requests
                WHERE status IN ('scheduled', 'running', 'failed')
                GROUP BY status
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                byStatus[reader.GetString(0)] = reader.GetInt64(1);
                oldest = Math.Max(oldest, reader.GetDouble(2));
            }

            CrmMetrics.Gauges.SetDeletion(byStatus.Select(kv => (kv.Key, kv.Value)), oldest);
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 4300, Level = LogLevel.Warning, Message = "Metrics sampling failed ({Reason}); keeping the last values")]
    private static partial void LogSampleFailed(ILogger logger, string reason);
}

public static class WorkerMetricsSamplerExtensions
{
    /// <summary>Örnekleyiciyi (outbox/workflow/silme gauge'ları) yalnız metrik uç noktası açıkken kaydeder (kapalıyken veritabanına ek sorgu yok).</summary>
    public static IServiceCollection AddWorkerMetricsSampler(this IServiceCollection services, IConfiguration configuration) =>
        configuration.GetSection(MetricsEndpointOptions.SectionName).GetValue<bool>(nameof(MetricsEndpointOptions.Enabled))
            ? services.AddHostedService<WorkerMetricsSampler>()
            : services;
}
