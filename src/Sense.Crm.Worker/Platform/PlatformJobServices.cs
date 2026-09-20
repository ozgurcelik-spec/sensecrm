using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Observability;

namespace Sense.Crm.Worker.Platform;

/// <summary>
/// Platform işleri için ortak iskelet (M7): her turda <c>pg_try_advisory_xact_lock</c> ile <b>tek örnek</b> çalışır (çok Worker kopyasında yalnız biri iş yapar; C-SEC2 M1: işlem düzeyi
/// kilit, iş bitince ya da bağlantı kopunca düşer), hata süreci düşürmez, sonraki turda yeniden denenir.
/// </summary>
public abstract partial class PlatformJobService(IConfiguration configuration, ILogger logger) : BackgroundService
{
    protected abstract string JobName { get; }

    /// <summary>İstişari kilit anahtarı (işe özgü sabit).</summary>
    protected abstract long LockKey { get; }

    protected abstract TimeSpan Interval { get; }

    protected abstract Task RunOnceAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.Database);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // C-SEC2 M1: işlem düzeyi (transaction-level) istişari kilit: kilit işlemin ömrüne bağlıdır (commit/rollback/bağlantı kopması → düşer); oturum düzeyi kilit gibi havuzlanan bağlantıda
                // (PgBouncer vb.) sızma/yanlış sahiplik riski taşımaz. İş, kilidi tutan işlemin içinde çalışır (işlem boşta bekler; iş kendi bağlantılarını kullanır).
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(stoppingToken).ConfigureAwait(false);
                await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction);
                acquire.Parameters.AddWithValue("key", LockKey);
                if (await acquire.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false) is true)
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CrmMetrics.BackgroundFailed(JobName);
                LogFailed(logger, ex, JobName);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // kapanıyor
            }
        }
    }

    [LoggerMessage(EventId = 4200, Level = LogLevel.Error, Message = "Platform job {Job} failed; will retry on the next round")]
    private static partial void LogFailed(ILogger logger, Exception exception, string job);
}

/// <summary>Günlük kullanım anlık görüntüsü (<c>Platform:Usage:SnapshotPollMinutes</c> dakikada bir; idempotent, günde birden çok tur tek satır).</summary>
public sealed partial class UsageSnapshotService(UsageSnapshotJob job, IOptions<PlatformOptions> options, IConfiguration configuration, ILogger<UsageSnapshotService> logger)
    : PlatformJobService(configuration, logger)
{
    protected override string JobName => nameof(UsageSnapshotService);

    protected override long LockKey => 7_302_001;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(options.Value.Usage.SnapshotPollMinutes, 1));

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var run = await job.RunOnceAsync(ct).ConfigureAwait(false);
        if (run.Written > 0 || run.Failed > 0)
        {
            LogRun(logger, run.Written, run.Failed);
        }
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information, Message = "Usage snapshot round: {Written} written, {Failed} failed")]
    private static partial void LogRun(ILogger logger, int written, int failed);
}

/// <summary>KVKK imha işi (<c>Platform:Deletion:PollMinutes</c> dakikada bir): zamanı gelen silme taleplerini kalıcı imha eder.</summary>
public sealed partial class TenantErasureService(TenantErasureJob job, IOptions<PlatformOptions> options, IConfiguration configuration, ILogger<TenantErasureService> logger)
    : PlatformJobService(configuration, logger)
{
    protected override string JobName => nameof(TenantErasureService);

    protected override long LockKey => 7_302_002;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(options.Value.Deletion.PollMinutes, 1));

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        var run = await job.RunOnceAsync(ct).ConfigureAwait(false);
        if (run.Completed > 0 || run.Failed > 0)
        {
            LogRun(logger, run.Completed, run.Failed);
        }
    }

    [LoggerMessage(EventId = 4202, Level = LogLevel.Warning, Message = "Tenant erasure round: {Completed} completed, {Failed} failed")]
    private static partial void LogRun(ILogger logger, int completed, int failed);
}
