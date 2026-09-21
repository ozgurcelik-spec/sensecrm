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
using Sense.Crm.Shared.Contracts.Observability;
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
            var limits = new PlanLimits(definition.Limits.MaxUsers, new Dictionary<string, int?>(definition.Limits.MaxRecords, StringComparer.Ordinal), definition.Limits.MaxWebhooks, definition.Limits.MaxApiKeys, definition.Limits.MaxStorageMb);
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
public sealed class AccountBackfill(ITenantDirectory directory, ITenantAccountRepository accounts, AccountProvisioner provisioner, IPlatformAdminDirectory admins, PlatformDbContext db)
{
    /// <summary>Son çalıştırmada <c>is_system</c> olarak işaretlenen (yeni açılan ya da var olan) hesap sayısı (C-SEC2 H1).</summary>
    public int LastMarkedSystem { get; private set; }

    /// <returns>Yeni açılan hesap sayısı.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // H1: eski kurulumlarda (M7 öncesi/backfill ile açılmış) platform işletim organizasyonu is_system = false yazılmış olabilir; aktif platform yöneticisi üyesi olan
        // her kiracı hesabı (yeni ya da var olan) is_system = true işaretlenir. İdempotenttir.
        var adminTenants = (await admins.ListTenantsWithActivePlatformAdminAsync(ct).ConfigureAwait(false)).ToHashSet();
        var existing = await accounts.GetExistingIdsAsync(ct).ConfigureAwait(false);
        var created = 0;
        var marked = 0;
        foreach (var info in await directory.ListAllAsync(ct).ConfigureAwait(false))
        {
            if (!existing.Contains(info.Id) && await provisioner.EnsureBackfilledAsync(info, adminTenants.Contains(info.Id), ct).ConfigureAwait(false))
            {
                created++;
                if (adminTenants.Contains(info.Id))
                {
                    marked++;
                }
            }
        }

        foreach (var tenantId in adminTenants.Where(existing.Contains))
        {
            var account = await accounts.GetAsync(tenantId, ct).ConfigureAwait(false);
            if (account is { IsSystem: false })
            {
                account.MarkSystem();
                marked++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        LastMarkedSystem = marked;
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
        var ordered = TenantErasureSteps.Resolve(erasers);
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

        CrmMetrics.UsageSnapshotTenants("written", written);
        CrmMetrics.UsageSnapshotTenants("failed", failed);

        var (snapshots, audit) = await PurgeAsync(now, today, ct).ConfigureAwait(false);
        return new UsageSnapshotRun(written, failed, snapshots, audit);
    }

    private async Task<(int Snapshots, int Audit)> PurgeAsync(DateTime now, DateOnly today, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var usageCutoff = today.AddDays(-options.Value.Usage.RetentionDays);

        // M3: platform denetimi salt-eklemelidir; saklama temizliği yalnız <c>retention</c> işaretiyle (SET LOCAL) ve veritabanı tetikleyicisinin taban yaşının (30 gün) üstündeki
        // satırlar için silebilir. Yapılandırma tabanın altındaysa taban uygulanır (aksi halde tetikleyici silmeyi reddederdi).
        var auditCutoff = now.AddDays(-Math.Max(options.Value.Audit.RetentionDays, PlatformLimits.MinAuditRetentionDays));
        var snapshots = await db.UsageSnapshots.Where(s => s.Day < usageCutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var audit = 0;
        await db.ExecuteInTransactionAsync(
            async token =>
            {
                await AuditMaintenance.SetLocalAsync(db, AuditMaintenance.Retention, token).ConfigureAwait(false);
                audit = await db.AuditEntries.Where(e => e.OccurredAt < auditCutoff).ExecuteDeleteAsync(token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return (snapshots, audit);
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Usage snapshot failed for tenant {TenantId}; will retry on the next round")]
    private static partial void LogTenantFailed(ILogger logger, Exception exception, Guid tenantId);
}

