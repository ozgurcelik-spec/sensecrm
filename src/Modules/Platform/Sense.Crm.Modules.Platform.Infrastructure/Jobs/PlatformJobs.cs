using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Application.Provisioning;
using Sense.Crm.Modules.Platform.Contracts;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Plans;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Retention;

namespace Sense.Crm.Modules.Platform.Infrastructure.Jobs;

/// <summary>Plan senkronu sonucu.</summary>
public sealed record PlanSyncResult(int Created, int Updated, int Deactivated, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// <c>Platform:Plans</c> → <c>platform.plans</c> idempotent upsert (Migrator <c>migrate</c> ve <c>sync-plans</c>). Yapılandırma doğrulanır
/// (<see cref="PlanCatalogValidator"/>); hata → hiçbir şey yazılmaz, çağıran (Migrator) ≠ 0 çıkış verir. Yapılandırmadan kalkan plan <c>is_active = false</c> olur
/// (mevcut atamalar çalışır, yeni atama <c>platform.plan_not_found</c>).
/// </summary>
public sealed class PlanSynchronizer(PlatformDbContext db, IOptions<PlatformOptions> options)
{
    public async Task<PlanSyncResult> SyncAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var errors = PlanCatalogValidator.Validate(settings);
        if (errors.Count > 0)
        {
            return new PlanSyncResult(0, 0, 0, errors);
        }

        var existing = await db.Plans.ToDictionaryAsync(p => p.Id, StringComparer.Ordinal, ct).ConfigureAwait(false);
        int created = 0, updated = 0, deactivated = 0;
        foreach (var definition in settings.Plans)
        {
            var limits = new PlanLimits(definition.Limits.MaxUsers, new Dictionary<string, int?>(definition.Limits.MaxRecords, StringComparer.Ordinal), definition.Limits.MaxWebhooks, definition.Limits.MaxApiKeys);
            var modules = GatedModules.All.ToDictionary(m => m, m => definition.Modules.TryGetValue(m, out var on) && on, StringComparer.Ordinal);
            if (existing.Remove(definition.Code, out var plan))
            {
                if (plan.Apply(definition.Name, definition.Description, definition.SortOrder, definition.TrialDays, limits, modules))
                {
                    updated++;
                }
            }
            else
            {
                db.Plans.Add(Plan.Create(definition.Code, definition.Name, definition.Description, definition.SortOrder, definition.TrialDays, limits, modules));
                created++;
            }
        }

        foreach (var removed in existing.Values)
        {
            if (removed.Deactivate())
            {
                deactivated++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PlanSyncResult(created, updated, deactivated, []);
    }
}

/// <summary>
/// Backfill (Migrator <c>migrate</c> sonu ve <c>backfill</c>): <c>ITenantDirectory.ListAllAsync()</c> ile satırı olmayan her kiracıya <c>internal</c> plan,
/// <c>source = backfill</c>, denemesiz, <c>onboarding_dismissed_at = now</c> yazar (mevcut kiracılar kısıtlanmaz, onboarding görmez). Idempotenttir.
/// </summary>
public sealed class AccountBackfill(ITenantDirectory directory, ITenantAccountRepository accounts, AccountProvisioner provisioner, PlatformDbContext db)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var existing = await accounts.GetExistingIdsAsync(ct).ConfigureAwait(false);
        var created = 0;
        foreach (var info in await directory.ListAllAsync(ct).ConfigureAwait(false))
        {
            if (!existing.Contains(info.Id) && await provisioner.EnsureBackfilledAsync(info, isSystem: false, ct).ConfigureAwait(false))
            {
                created++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return created;
    }
}

/// <summary>
/// <c>erase-deleted-tenants</c> (Migrator): mezar taşı olan (<c>deleted</c>) her kiracı için tüm imha adımlarını yeniden koşar. Yedekten geri yükleme sonrası imha edilmiş
/// kiracı verisi yeniden görünürse tekrar silinir; imha adımları idempotent olduğundan zaten temiz bir kiracıda bir şey yapmaz. Mezar taşına dokunmaz.
/// </summary>
public sealed class DeletedTenantsReplay(PlatformDbContext db, IEnumerable<ITenantDataEraser> erasers, IOptions<PlatformOptions> options)
{
    public async Task<long> RunAsync(CancellationToken ct)
    {
        var tenantIds = await db.TenantAccounts.AsNoTracking()
            .Where(a => a.Status == StoredTenantStatuses.Deleted && !a.IsSystem)
            .Select(a => a.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        long total = 0;
        var ordered = erasers.OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        foreach (var tenantId in tenantIds)
        {
            foreach (var eraser in ordered)
            {
                total += (await eraser.EraseAsync(tenantId, options.Value.Deletion.ChunkSize, ct).ConfigureAwait(false)).Total;
            }
        }

        return total;
    }
}

/// <summary>Anlık görüntü işi sonucu.</summary>
public sealed record UsageSnapshotRun(int Written, int Failed, int SnapshotsPurged, int AuditPurged);

/// <summary>
/// Günlük kullanım anlık görüntüsü (Worker <c>UsageSnapshotService</c> her <c>Platform:Usage:SnapshotPollMinutes</c> dakikada çağırır): bugün (UTC) için anlık görüntüsü
/// olmayan ve durumu <c>active|suspended</c> olan (silme bekleyen/silinmiş <b>hariç</b>) her hesap için sırayla kiracı kapsamında tüm reporter'ları çalıştırıp
/// idempotent upsert yazar. Bir kiracıda hata: günlüğe yazılır, satır yazılmaz, diğerleri sürer, sonraki turda yeniden denenir. Eski günler geriye dönük
/// <b>üretilemez</b>. Aynı işte saklama temizliği: anlık görüntü &gt; <c>Usage:RetentionDays</c>, platform denetimi &gt; <c>Audit:RetentionDays</c>.
/// </summary>
public sealed partial class UsageSnapshotJob(IServiceScopeFactory scopes, IOptions<PlatformOptions> options, TimeProvider clock, ILogger<UsageSnapshotJob> logger)
{
    public async Task<UsageSnapshotRun> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        List<Guid> tenantIds;
        using (var listScope = scopes.CreateScope())
        {
            var db = listScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenantIds = await db.TenantAccounts.AsNoTracking()
                .Where(a => (a.Status == StoredTenantStatuses.Active || a.Status == StoredTenantStatuses.Suspended)
                    && !db.UsageSnapshots.Any(s => s.TenantId == a.Id && s.Day == today))
                .OrderBy(a => a.Id)
                .Select(a => a.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        int written = 0, failed = 0;
        foreach (var tenantId in tenantIds)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var meter = scope.ServiceProvider.GetRequiredService<IUsageMeter>();
                var writer = scope.ServiceProvider.GetRequiredService<IUsageSnapshotWriter>();
                var usage = await meter.CollectAsync(tenantId, ct).ConfigureAwait(false);
                await writer.UpsertAsync(tenantId, today, usage, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                written++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogTenantFailed(logger, ex, tenantId);
            }
        }

        var (snapshots, audit) = await PurgeAsync(now, today, ct).ConfigureAwait(false);
        return new UsageSnapshotRun(written, failed, snapshots, audit);
    }

    private async Task<(int Snapshots, int Audit)> PurgeAsync(DateTime now, DateOnly today, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var usageCutoff = today.AddDays(-options.Value.Usage.RetentionDays);
        var auditCutoff = now.AddDays(-options.Value.Audit.RetentionDays);
        var snapshots = await db.UsageSnapshots.Where(s => s.Day < usageCutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var audit = await db.AuditEntries.Where(e => e.OccurredAt < auditCutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return (snapshots, audit);
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Usage snapshot failed for tenant {TenantId}; will retry on the next round")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);
}

/// <summary>Silme işi sonucu.</summary>
public sealed record TenantErasureRun(int Completed, int Failed);

/// <summary>
/// KVKK imha işi (Worker <c>TenantErasureService</c> her <c>Platform:Deletion:PollMinutes</c> dakikada çağırır). Kuyruk: (a) <c>scheduled</c> ve
/// <c>scheduled_for &lt;= now</c>; (b) <c>running|failed</c> ve <c>attempts &lt; MaxAttempts</c> (üstel bekleme). Akış: talep <c>running</c>; adımlar
/// (<see cref="ITenantDataEraser"/>) <c>Order</c> sırasıyla, her biri idempotent; tamamlanan adım <c>erased_steps</c>'e yazılır, yeniden çalıştırma tamamlananları atlar. Adım hatası
/// <c>attempts++</c> + <c>last_error</c> + <c>failed</c>; <c>MaxAttempts</c> sonrası <c>failed</c> kalır ve <c>deletion.failed</c> denetimi + günlük uyarısı yazılır.
/// Son adım mezar taşı: <c>usage_snapshots</c> silinir, hesap <c>deleted</c> + ad/slug redakte, <c>platform_audit_entries.target_tenant_name</c> redakte, talep
/// <c>completed</c> + rapor, <c>TenantErased</c> olayı, <c>deletion.completed</c> denetimi. Sistem kiracısı asla imha edilmez.
/// </summary>
public sealed partial class TenantErasureJob(IServiceScopeFactory scopes, IOptions<PlatformOptions> options, TimeProvider clock, ILogger<TenantErasureJob> logger)
{
    public const string TombstoneStep = "platform-tombstone";

    public async Task<TenantErasureRun> RunOnceAsync(CancellationToken ct)
    {
        var settings = options.Value.Deletion;
        var now = clock.GetUtcNow().UtcDateTime;

        List<Guid> due;
        using (var listScope = scopes.CreateScope())
        {
            var db = listScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidates = await db.DeletionRequests.AsNoTracking()
                .Where(r => (r.Status == DeletionStatuses.Scheduled && r.ScheduledFor <= now)
                    || ((r.Status == DeletionStatuses.Running || r.Status == DeletionStatuses.Failed) && r.Attempts < settings.MaxAttempts))
                .OrderBy(r => r.ScheduledFor)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            due = candidates.Where(r => r.Status != DeletionStatuses.Failed || BackoffElapsed(r.Attempts, r.ModifiedDate ?? r.CreatedAt, now)).Select(r => r.Id).ToList();
        }

        int completed = 0, failed = 0;
        foreach (var requestId in due)
        {
            if (await ProcessAsync(requestId, ct).ConfigureAwait(false))
            {
                completed++;
            }
            else
            {
                failed++;
            }
        }

        return new TenantErasureRun(completed, failed);
    }

    /// <summary>Başarısız denemeden sonra üstel bekleme: 1 dk × 2^(attempts−1), en çok 30 dk.</summary>
    public static bool BackoffElapsed(int attempts, DateTime lastChangeUtc, DateTime nowUtc)
    {
        var minutes = Math.Min(Math.Pow(2, Math.Max(attempts - 1, 0)), 30);
        return nowUtc >= lastChangeUtc.AddMinutes(minutes);
    }

    private async Task<bool> ProcessAsync(Guid requestId, CancellationToken ct)
    {
        var settings = options.Value.Deletion;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PlatformDbContext>();
        var audit = sp.GetRequiredService<IPlatformAudit>();
        var outbox = sp.GetRequiredService<IIntegrationEventOutbox>();
        var cache = sp.GetRequiredService<IEntitlementCache>();

        var request = await db.DeletionRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct).ConfigureAwait(false);
        if (request is null)
        {
            return true;
        }

        var tenantId = request.TenantId;
        var account = await db.TenantAccounts.FirstOrDefaultAsync(a => a.Id == tenantId, ct).ConfigureAwait(false);
        if (account is null || account.IsSystem)
        {
            LogSkipped(logger, tenantId);
            return true;
        }

        request.Start(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var erasers = sp.GetServices<ITenantDataEraser>().OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        foreach (var eraser in erasers)
        {
            if (request.HasCompleted(eraser.Name))
            {
                continue;
            }

            try
            {
                var report = await eraser.EraseAsync(tenantId, settings.ChunkSize, ct).ConfigureAwait(false);
                request.StepCompleted(eraser.Name, report.DeletedRows);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(db, audit, request, account, eraser.Name, ex, settings, ct).ConfigureAwait(false);
                return false;
            }
        }

        try
        {
            await TombstoneAsync(db, audit, outbox, request, account, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var fresh = await db.DeletionRequests.FirstAsync(r => r.Id == requestId, ct).ConfigureAwait(false);
            var freshAccount = await db.TenantAccounts.FirstAsync(a => a.Id == tenantId, ct).ConfigureAwait(false);
            await RecordFailureAsync(db, audit, fresh, freshAccount, TombstoneStep, ex, settings, ct).ConfigureAwait(false);
            return false;
        }

        await cache.InvalidateAsync(tenantId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task TombstoneAsync(PlatformDbContext db, IPlatformAudit audit, IIntegrationEventOutbox outbox, Domain.Deletion.DeletionRequest request, Domain.Accounts.TenantAccount account, CancellationToken ct)
    {
        var tenantId = account.TenantId;
        var now = clock.GetUtcNow();
        var snapshots = await db.UsageSnapshots.Where(s => s.TenantId == tenantId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var redacted = await db.AuditEntries.Where(e => e.TargetTenantId == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.TargetTenantName, PlatformLimits.DeletedNamePlaceholder), ct)
            .ConfigureAwait(false);

        var deleted = account.MarkDeleted(now.UtcDateTime);
        if (deleted.IsFailure)
        {
            throw new InvalidOperationException(deleted.Error.Code);
        }

        request.StepCompleted(TombstoneStep, new Dictionary<string, long> { ["platform.usage_snapshots"] = snapshots, ["platform.platform_audit_entries(redacted)"] = redacted });
        request.Complete(now.UtcDateTime);

        audit.Record(
            PlatformAuditActions.DeletionCompleted,
            account,
            null,
            new Dictionary<string, object?> { ["status"] = new { old = AccountStatuses.PendingDeletion, @new = AccountStatuses.Deleted }, ["requestId"] = request.Id, ["report"] = ParseReport(request.Report) });
        outbox.Enqueue(new TenantErased(tenantId, now));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(
        PlatformDbContext db,
        IPlatformAudit audit,
        Domain.Deletion.DeletionRequest request,
        Domain.Accounts.TenantAccount account,
        string step,
        Exception exception,
        DeletionOptions settings,
        CancellationToken ct)
    {
        // Kişisel veri içermez: yalnız istisna türü + kısaltılmış ileti (imha adımları kiracı kimliğiyle parametreli SQL çalıştırır).
        var message = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
        request.Fail($"{step}: {exception.GetType().Name}: {message}");
        if (request.Attempts >= settings.MaxAttempts)
        {
            audit.Record(
                PlatformAuditActions.DeletionFailed,
                account,
                null,
                new Dictionary<string, object?> { ["requestId"] = request.Id, ["step"] = step, ["attempts"] = request.Attempts });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        LogFailed(logger, exception, account.TenantId, step, request.Attempts);
    }

    private static JsonElement ParseReport(string? report)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(report) ? "{}" : report);
        return document.RootElement.Clone();
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Error, Message = "Tenant erasure failed for {TenantId} at step {Step} (attempt {Attempts})")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenantId, string step, int attempts);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Warning, Message = "Tenant erasure skipped for {TenantId}: account missing or system tenant")]
    private static partial void LogSkipped(ILogger logger, Guid tenantId);
}

/// <summary>
/// <c>audit.audit_log_entries WHERE tenant_id = @t</c> (kişisel veri barındıran <c>changes</c> dahil) parça parça siler (sıra 900). Kiracı kimliği parametredir.
/// </summary>
public sealed class AuditTenantDataEraser(PlatformDbContext db) : ITenantDataEraser
{
    public string Name => "audit";

    public int Order => 900;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        var chunk = Math.Max(chunkSize, 1);
        long total = 0;
        while (true)
        {
            var affected = await db.Database.ExecuteSqlAsync(
                $"DELETE FROM audit.audit_log_entries WHERE ctid IN (SELECT ctid FROM audit.audit_log_entries WHERE tenant_id = {tenantId} LIMIT {chunk})",
                ct).ConfigureAwait(false);
            total += affected;
            if (affected < chunk)
            {
                return new EraseReport(new Dictionary<string, long> { ["audit.audit_log_entries"] = total });
            }
        }
    }
}
