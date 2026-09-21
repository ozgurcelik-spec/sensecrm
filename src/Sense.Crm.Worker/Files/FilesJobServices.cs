using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Infrastructure.Jobs;
using Sense.Crm.Worker.Platform;

namespace Sense.Crm.Worker.Files;

/// <summary>
/// Dosya ekleri temizlik işi (M8C; <c>Files:Purge:PollMinutes</c> dakikada bir): yumuşak silinmiş dosyaların nesnelerini bekleme süresi sonunda fiziksel siler, erişim günlüğü
/// saklama temizliği. <c>pg_try_advisory_lock</c> ile tek örnek (M7 <c>PlatformJobService</c> iskeleti); hata süreci düşürmez, sonraki turda yeniden denenir.
/// </summary>
public sealed partial class FilesPurgeService(IServiceScopeFactory scopes, IOptions<FilesOptions> options, IConfiguration configuration, ILogger<FilesPurgeService> logger)
    : PlatformJobService(configuration, logger)
{
    protected override string JobName => nameof(FilesPurgeService);

    protected override long LockKey => 7_303_001;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(options.Value.Purge.PollMinutes, 1));

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<FilesPurgeJob>().RunOnceAsync(ct).ConfigureAwait(false);
        if (run.Purged > 0 || run.Failed > 0 || run.AccessLogRowsDeleted > 0)
        {
            LogRun(logger, run.Tenants, run.Purged, run.Failed, run.AccessLogRowsDeleted);
        }
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Information, Message = "Files purge round: {Tenants} tenants, {Purged} files purged, {Failed} failed, {LogRows} access log rows deleted")]
    private static partial void LogRun(ILogger logger, int tenants, int purged, int failed, long logRows);
}

/// <summary>
/// Dosya ekleri uzlaştırma işi (M8C; <c>Files:Reconcile:PollHours</c> saatte bir): nesneler ↔ satırlar, yetim nesne silme (güvenlik supabıyla), kayıt-yok süpürmesi.
/// <c>migrator files-reconcile</c> aynı kodu koşar.
/// </summary>
public sealed partial class FilesReconciliationService(IServiceScopeFactory scopes, IOptions<FilesOptions> options, IConfiguration configuration, ILogger<FilesReconciliationService> logger)
    : PlatformJobService(configuration, logger)
{
    protected override string JobName => nameof(FilesReconciliationService);

    protected override long LockKey => 7_303_002;

    protected override TimeSpan Interval => TimeSpan.FromHours(Math.Max(options.Value.Reconcile.PollHours, 1));

    protected override async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<FilesReconciliationJob>().RunAsync(new ReconcileRunOptions(), ct).ConfigureAwait(false);
        var missing = run.Tenants.Sum(t => t.MarkedMissing);
        var orphans = run.Tenants.Sum(t => t.OrphansDeleted);
        if (missing > 0 || orphans > 0 || run.Failed > 0)
        {
            LogRun(logger, run.Tenants.Count, missing, orphans, run.Failed);
        }
    }

    [LoggerMessage(EventId = 4302, Level = LogLevel.Information, Message = "Files reconciliation round: {Tenants} tenants, {Missing} rows marked missing, {Orphans} orphan objects deleted, {Failed} tenants failed")]
    private static partial void LogRun(ILogger logger, int tenants, int missing, int orphans, int failed);
}
