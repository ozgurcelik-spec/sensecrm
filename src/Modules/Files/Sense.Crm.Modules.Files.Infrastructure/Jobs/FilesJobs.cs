using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Contracts;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Files.Infrastructure.Persistence;
using Sense.Crm.Modules.Files.Infrastructure.Upload;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Files;

namespace Sense.Crm.Modules.Files.Infrastructure.Jobs;

/// <summary>Bir temizlik turunun sonucu (kişisel veri yok).</summary>
public sealed record PurgeRunResult(int Tenants, int Purged, int Failed, long AccessLogRowsDeleted);

/// <summary>
/// Temizlik işi (<c>FilesPurgeService</c>, Worker; <c>Files:Purge:PollMinutes</c>): <c>state = deleted</c> ve <c>deleted_at &lt; now − SoftDeleteRetentionDays</c> satırlar için 200'lük
/// parçalarla nesneyi <c>DeleteAsync</c> (idempotent) → başarılıysa satırı fiziksel siler; nesne silme hatası satırı <b>bırakır</b> (sonraki tur). Aynı işte
/// <c>file_access_log</c> saklama temizliği. <b>Yeni <c>IgnoreQueryFilters</c> yok:</b> kiracı listesi tek parametreli ham SQL ile alınır, her kiracı
/// <c>ITenantContextSetter.BeginScope</c> ile kendi kapsamında işlenir; bir kiracının hatası diğerlerini engellemez.
/// </summary>
public sealed partial class FilesPurgeJob(IServiceScopeFactory scopeFactory, IOptions<FilesOptions> options, TimeProvider clock, ILogger<FilesPurgeJob> logger)
{
    public async Task<PurgeRunResult> RunOnceAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var deletedCutoff = now.AddDays(-settings.Purge.SoftDeleteRetentionDays);
        var logCutoff = now.AddDays(-settings.AccessLog.RetentionDays);

        IReadOnlyList<Guid> tenants;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();

            // Kiracıya özel kapsam yok (kiracı listesi); yalnız kimlik döner, tek parametreli ham SQL (ham SQL envanterinde listeli).
            tenants = await db.Database.SqlQuery<Guid>(
                $"""
                SELECT tenant_id AS "Value" FROM files.attachments WHERE state = 'deleted' AND deleted_at < {deletedCutoff}
                UNION
                SELECT tenant_id AS "Value" FROM files.file_access_log WHERE occurred_at < {logCutoff}
                """).ToListAsync(ct).ConfigureAwait(false);
        }

        int purged = 0, failed = 0;
        long logRows = 0;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var _ = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().BeginScope(tenantId);
                var (p, f, l) = await PurgeTenantAsync(scope.ServiceProvider, settings, deletedCutoff, logCutoff, ct).ConfigureAwait(false);
                purged += p;
                failed += f;
                logRows += l;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenantId);
            }
        }

        return new PurgeRunResult(tenants.Count, purged, failed, logRows);
    }

    private static async Task<(int Purged, int Failed, long LogRows)> PurgeTenantAsync(
        IServiceProvider services, FilesOptions settings, DateTime deletedCutoff, DateTime logCutoff, CancellationToken ct)
    {
        var db = services.GetRequiredService<FilesDbContext>();
        var storage = services.GetRequiredService<IFileStorage>();
        var tenant = services.GetRequiredService<ITenantContext>();
        var chunk = Math.Max(settings.Purge.ChunkSize, 1);
        int purged = 0, failed = 0;
        var skip = new HashSet<Guid>();

        while (true)
        {
            var batch = await db.Attachments
                .Where(a => a.State == FileState.Deleted && a.DeletedAt < deletedCutoff && !skip.Contains(a.Id))
                .OrderBy(a => a.DeletedAt)
                .Take(chunk)
                .ToListAsync(ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            var removedInBatch = 0;
            foreach (var file in batch)
            {
                if (!ObjectKey.TryParse(file.StorageKey, out var key) || key.TenantId != tenant.TenantId)
                {
                    // Bozuk/yabancı anahtar: nesneye dokunulmaz, satır yine de temizlenir (yetim satır bırakılmaz).
                    db.Attachments.Remove(file);
                    removedInBatch++;
                    continue;
                }

                try
                {
                    await storage.DeleteAsync(key, ct).ConfigureAwait(false);
                    db.Attachments.Remove(file);
                    removedInBatch++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Nesne silme hatası satırı bırakır (sonraki tur).
                    failed++;
                    skip.Add(file.Id);
                    _ = ex;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            purged += removedInBatch;
            if (removedInBatch == 0)
            {
                break;
            }
        }

        var logRows = await db.AccessLog.Where(e => e.OccurredAt < logCutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return (purged, failed, logRows);
    }

    [LoggerMessage(EventId = 5200, Level = LogLevel.Error, Message = "Files purge failed for tenant {TenantId}; will retry on the next round")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);
}

/// <summary>Uzlaştırma seçenekleri (Migrator <c>files-reconcile [--dry-run] [--tenant &lt;id&gt;]</c>).</summary>
public sealed record ReconcileRunOptions(bool DryRun = false, Guid? TenantId = null);

/// <summary>Bir kiracının uzlaştırma raporu (yalnız sayılar; dosya adı yok).</summary>
public sealed record TenantReconcileReport(
    Guid TenantId,
    long Objects,
    long Rows,
    int MarkedMissing,
    int RestoredReady,
    int SizeMismatch,
    int OrphansFound,
    int OrphansDeleted,
    bool OrphanGuardTripped,
    int ForeignObjectsSkipped,
    int RecordMissingMarked,
    int RecordMissingCleared,
    int SweptDeleted);

public sealed record ReconcileRunResult(bool DryRun, IReadOnlyList<TenantReconcileReport> Tenants, int Failed);

/// <summary>
/// Uzlaştırma işi (<c>FilesReconciliationService</c>, Worker; <c>migrator files-reconcile</c> aynı kodu koşar): kiracı başına nesneler ↔ satırlar.
/// (1) satır var, nesne yok (ya da boyut uyuşmuyor) → <c>missing</c> (+ sayaç, <c>Error</c> günlüğü); nesne geri gelirse <c>ready</c>. (2) nesne var, satır yok ve
/// <c>LastModified &lt; now − OrphanGraceHours</c> → silinir; <b>güvenlik supabı:</b> yetim sayısı <c>MaxOrphanDeletePerRun</c> ya da nesnelerin <c>MaxOrphanDeleteFraction</c>
/// oranını aşarsa <b>hiçbir şey silinmez</b>. (3) anahtarı ayrıştırılamayan (yabancı) nesneler ve bilinmeyen kiracı önekleri <b>asla</b> silinmez. (4) kayıt-yok süpürmesi:
/// <c>IAttachmentTarget.GetExistingAsync</c> (tür başına 500'lük toplu sorgu); kayıt yoksa <c>record_missing_since</c>, geri gelirse temizlenir,
/// <c>RecordMissingGraceDays</c> dolunca dosya yumuşak silinir (sistem kullanıcısıyla). <c>--dry-run</c> yalnız rapor yazar.
/// </summary>
public sealed partial class FilesReconciliationJob(
    IServiceScopeFactory scopeFactory,
    IOptions<FilesOptions> options,
    TimeProvider clock,
    ILogger<FilesReconciliationJob> logger)
{
    private const int TargetBatch = 500;
    private const int PageSize = 1000;

    public async Task<ReconcileRunResult> RunAsync(ReconcileRunOptions run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        IReadOnlyList<TenantInfo> tenants;
        using (var scope = scopeFactory.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAllAsync(ct).ConfigureAwait(false);
        }

        var reports = new List<TenantReconcileReport>();
        var failed = 0;
        foreach (var tenant in tenants.Where(t => run.TenantId is null || t.Id == run.TenantId))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var _ = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().BeginScope(tenant.Id);
                reports.Add(await ReconcileTenantAsync(scope.ServiceProvider, tenant.Id, run.DryRun, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenant.Id);
            }
        }

        return new ReconcileRunResult(run.DryRun, reports, failed);
    }

    private async Task<TenantReconcileReport> ReconcileTenantAsync(IServiceProvider services, Guid tenantId, bool dryRun, CancellationToken ct)
    {
        var settings = options.Value;
        var db = services.GetRequiredService<FilesDbContext>();
        var storage = services.GetRequiredService<IFileStorage>();
        var now = clock.GetUtcNow();

        // Satırlar (yalnız gerekli kolonlar): yaşayan + yumuşak silinmiş (nesnesi henüz temizlenmemiş olabilir).
        var rows = await db.Attachments.AsNoTracking()
            .Select(a => new RowInfo(a.Id, a.StorageKey, a.State, a.SizeBytes))
            .ToDictionaryAsync(r => r.StorageKey, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orphans = new List<ObjectKey>();
        var toMissing = new HashSet<Guid>();
        var toReady = new HashSet<Guid>();
        int mismatch = 0, foreign = 0;
        long objects = 0;

        await foreach (var item in storage.ListAsync(tenantId, ct).ConfigureAwait(false))
        {
            objects++;
            if (item.Key is not { } key || key.TenantId != tenantId)
            {
                // Yabancı/ayrıştırılamayan nesne: asla silinmez, yalnız uyarı.
                foreign++;
                continue;
            }

            if (rows.TryGetValue(item.RawKey, out var row))
            {
                seen.Add(item.RawKey);
                if (row.State == FileState.Ready && row.SizeBytes != item.Size)
                {
                    mismatch++;
                    toMissing.Add(row.Id);
                }
                else if (row.State == FileState.Missing && row.SizeBytes == item.Size)
                {
                    toReady.Add(row.Id);
                }
            }
            else if (item.LastModified < now.AddHours(-settings.Reconcile.OrphanGraceHours))
            {
                orphans.Add(key);
            }
        }

        foreach (var (rawKey, row) in rows)
        {
            if (!seen.Contains(rawKey) && row.State == FileState.Ready)
            {
                toMissing.Add(row.Id);
            }
        }

        // Güvenlik supabı: yanlış geri yükleme sonrası toplu silmeyi önler.
        var guardTripped = orphans.Count > 0
            && (orphans.Count > settings.Reconcile.MaxOrphanDeletePerRun || orphans.Count > objects * settings.Reconcile.MaxOrphanDeleteFraction);
        var orphansDeleted = 0;
        if (guardTripped)
        {
            FilesMetrics.ReconcileGuardTripped.Add(1);
            LogGuardTripped(logger, tenantId, orphans.Count, objects);
        }
        else if (!dryRun)
        {
            foreach (var key in orphans)
            {
                if (await storage.DeleteAsync(key, ct).ConfigureAwait(false))
                {
                    orphansDeleted++;
                }
            }

            FilesMetrics.ReconcileOrphansDeleted.Add(orphansDeleted);
        }

        if (toMissing.Count > 0)
        {
            FilesMetrics.ReconcileMissing.Add(toMissing.Count);
            LogMissing(logger, tenantId, toMissing.Count);
        }

        if (!dryRun && (toMissing.Count > 0 || toReady.Count > 0))
        {
            var affected = toMissing.Concat(toReady).ToList();
            var tracked = await db.Attachments.Where(a => affected.Contains(a.Id)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var file in tracked)
            {
                if (toMissing.Contains(file.Id))
                {
                    file.MarkMissing();
                }
                else if (toReady.Contains(file.Id))
                {
                    file.MarkReady();
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var sweep = await SweepMissingRecordsAsync(services, db, dryRun, now, ct).ConfigureAwait(false);
        return new TenantReconcileReport(
            tenantId,
            objects,
            rows.Count,
            toMissing.Count,
            toReady.Count,
            mismatch,
            orphans.Count,
            orphansDeleted,
            guardTripped,
            foreign,
            sweep.Marked,
            sweep.Cleared,
            sweep.Deleted);
    }

    /// <summary>Kayıt-yok süpürmesi: dosyası olan (tür, kayıt) çiftleri için hedefin <c>GetExistingAsync</c>'ı (tür başına 500'lük toplu sorgu).</summary>
    private async Task<(int Marked, int Cleared, int Deleted)> SweepMissingRecordsAsync(IServiceProvider services, FilesDbContext db, bool dryRun, DateTimeOffset now, CancellationToken ct)
    {
        var targets = services.GetServices<IAttachmentTarget>().GroupBy(t => t.RecordType, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var outbox = services.GetRequiredService<IIntegrationEventOutbox>();
        var graceCutoff = now.UtcDateTime.AddDays(-options.Value.Purge.RecordMissingGraceDays);
        int marked = 0, cleared = 0, deleted = 0;

        // Kimlikler önce (16 bayt/satır), satırlar 1000'lik parçalarla izlenerek işlenir (parça sonrası izleyici temizlenir).
        var allIds = await db.Attachments.AsNoTracking().Where(a => a.State != FileState.Deleted).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var idChunk in allIds.Chunk(PageSize))
        {
            var pageIds = idChunk;
            var page = await db.Attachments.Where(a => pageIds.Contains(a.Id) && a.State != FileState.Deleted).ToListAsync(ct).ConfigureAwait(false);
            foreach (var group in page.GroupBy(f => f.RecordType, StringComparer.Ordinal))
            {
                if (!targets.TryGetValue(group.Key, out var target))
                {
                    continue;
                }

                foreach (var batch in group.Chunk(TargetBatch))
                {
                    var ids = batch.Select(f => f.RecordId).Distinct().ToArray();
                    var existing = await target.GetExistingAsync(ids, ct).ConfigureAwait(false);
                    foreach (var file in batch)
                    {
                        if (existing.Contains(file.RecordId))
                        {
                            if (file.RecordMissingSince is not null)
                            {
                                cleared++;
                                if (!dryRun)
                                {
                                    file.ClearRecordMissing();
                                }
                            }

                            continue;
                        }

                        if (file.RecordMissingSince is null)
                        {
                            marked++;
                            if (!dryRun)
                            {
                                file.MarkRecordMissing(now.UtcDateTime);
                            }
                        }
                        else if (file.RecordMissingSince < graceCutoff)
                        {
                            deleted++;
                            if (!dryRun && file.SoftDelete(null, now.UtcDateTime))
                            {
                                outbox.Enqueue(new FileDeleted(file.TenantId, file.Id, file.RecordType, file.RecordId, null));
                            }
                        }
                    }
                }
            }

            if (!dryRun)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            db.ChangeTracker.Clear();
        }

        return (marked, cleared, deleted);
    }

    private sealed record RowInfo(Guid Id, string StorageKey, FileState State, long SizeBytes);

    [LoggerMessage(EventId = 5210, Level = LogLevel.Error, Message = "Files reconciliation failed for tenant {TenantId}")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(EventId = 5211, Level = LogLevel.Error, Message = "Files reconciliation safety valve tripped for tenant {TenantId}: {Orphans} orphan objects out of {Objects}; nothing was deleted")]
    private static partial void LogGuardTripped(ILogger logger, Guid tenantId, int orphans, long objects);

    [LoggerMessage(EventId = 5212, Level = LogLevel.Error, Message = "Files reconciliation found {Count} rows without objects for tenant {TenantId} (state set to missing)")]
    private static partial void LogMissing(ILogger logger, Guid tenantId, int count);
}
