using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Persistence;
using Sense.Crm.Shared.Contracts.Usage;
using Sense.Crm.Shared.Infrastructure.Caching;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Kernel.Time;

namespace Sense.Crm.Modules.Platform.Infrastructure.Entitlements;

internal static class EntitlementCacheKeys
{
    public const string Prefix = "ent";
    public const string UsagePrefix = "usage";

    public static string Entitlements(Guid tenantId) => $"{Prefix}:{tenantId:N}";

    public static string Usage(Guid tenantId) => $"{UsagePrefix}:{tenantId:N}";
}

/// <summary>Varlık önbelleği geçersiz kılma (aynı süreçte anında; çok kopyada diğerleri ≤ <c>Platform:Entitlements:CacheSeconds</c> + L2 süresi bayat kalır).</summary>
public sealed class EntitlementCache(HybridCache cache) : IEntitlementCache
{
    public async Task InvalidateAsync(Guid tenantId, CancellationToken ct)
    {
        await cache.RemoveAsync(EntitlementCacheKeys.Entitlements(tenantId), ct).ConfigureAwait(false);
        await cache.RemoveAsync(EntitlementCacheKeys.Usage(tenantId), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="ITenantEntitlements"/> uygulaması. Ham durum (hesap + plan + istisna → <see cref="EntitlementSnapshot"/>) HybridCache'te
/// <c>ent:{tenantId}</c> anahtarıyla <c>Platform:Entitlements:CacheSeconds</c> (30 sn) tutulur; <b>etkin durum her çağrıda <c>now</c> ile</b> hesaplanır
/// (deneme bitişi önbellek süresine takılmaz). Hesap satırı yoksa (olay henüz işlenmedi) <c>Platform:Signup:PlanCode</c> planı + deneme ile
/// <c>source = lazy</c> satırı açılır (<c>INSERT … ON CONFLICT DO NOTHING</c>) → yeni kayıt limitsiz "boşluk" bırakmaz.
/// </summary>
public sealed class TenantEntitlementsService(
    PlatformDbContext db,
    HybridCache cache,
    ITenantDirectory directory,
    IOptions<PlatformOptions> options,
    TimeProvider clock) : ITenantEntitlements
{
    public async Task<EntitlementSnapshot> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var seconds = options.Value.Entitlements.CacheSeconds;
        if (seconds <= 0)
        {
            return await LoadAsync(tenantId, ct).ConfigureAwait(false);
        }

        var ttl = TimeSpan.FromSeconds(seconds);
        return await cache.GetOrCreateInContextAsync(
            EntitlementCacheKeys.Entitlements(tenantId),
            async token => await LoadAsync(tenantId, token).ConfigureAwait(false),
            new HybridCacheEntryOptions { Expiration = ttl, LocalCacheExpiration = ttl },
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<EntitlementSnapshot> LoadAsync(Guid tenantId, CancellationToken ct)
    {
        var info = await directory.FindAsync(tenantId, ct).ConfigureAwait(false);
        var account = await db.TenantAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == tenantId, ct).ConfigureAwait(false);
        if (account is null)
        {
            if (info is null)
            {
                return Unknown(tenantId);
            }

            await CreateLazyAsync(tenantId, info, ct).ConfigureAwait(false);
            account = await db.TenantAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == tenantId, ct).ConfigureAwait(false);
            if (account is null)
            {
                return Unknown(tenantId);
            }
        }

        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == account.PlanCode, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Plan '{account.PlanCode}' of tenant {tenantId} is not in the catalog.");
        return EntitlementMath.ToSnapshot(account, plan, info?.TimeZone ?? TenantCalendar.UtcId);
    }

    /// <summary>Bilinmeyen/yok kiracı: erişim yok (silinmiş gibi).</summary>
    private static EntitlementSnapshot Unknown(Guid tenantId) =>
        new(
            tenantId,
            "unknown",
            "unknown",
            StoredTenantStatuses.Deleted,
            SuspensionMode: null,
            TrialEndsAt: null,
            TrialEndsOn: null,
            TimeZone: TenantCalendar.UtcId,
            GatedModules.All.ToDictionary(m => m, _ => false, StringComparer.Ordinal),
            MaxUsers: null,
            new Dictionary<string, int?>());

    private async Task CreateLazyAsync(Guid tenantId, TenantInfo info, CancellationToken ct)
    {
        var planCode = options.Value.Signup.PlanCode;
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planCode, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Signup plan '{planCode}' is not in the catalog (run the Migrator).");

        var now = clock.GetUtcNow();
        var timeZone = info.TimeZone;
        DateOnly? trialOn = plan.TrialDays is { } days ? TenantCalendar.For(timeZone).Today(now).AddDays(days) : null;
        DateTime? trialAt = trialOn is { } on ? EntitlementMath.TrialEndsAtUtc(on, timeZone) : null;
        var createdAt = (info.CreatedAt ?? now).UtcDateTime;
        var slug = string.IsNullOrWhiteSpace(info.Slug) ? "t-" + tenantId.ToString("N")[..12] : info.Slug;
        var nowUtc = now.UtcDateTime;
        var active = StoredTenantStatuses.Active;
        var lazy = AccountSources.Lazy;

        await db.Database.ExecuteSqlAsync(
            $$"""
            INSERT INTO platform.tenant_accounts
                (tenant_id, name, slug, plan_code, status, source, is_system, trial_ends_on, trial_ends_at, onboarding_done, tenant_created_at, created_at)
            VALUES
                ({{tenantId}}, {{info.Name}}, {{slug}}, {{plan.Code}}, {{active}}, {{lazy}}, FALSE, {{trialOn}}::date, {{trialAt}}::timestamptz, '{}'::text[], {{createdAt}}, {{nowUtc}})
            ON CONFLICT DO NOTHING
            """,
            ct).ConfigureAwait(false);
    }
}

/// <summary><see cref="IPlanCatalog"/>: yalnız etkin (<c>is_active</c>) planlar atanabilir.</summary>
public sealed class PlanCatalog(PlatformDbContext db, IOptions<PlatformOptions> options) : IPlanCatalog
{
    public string ProvisioningPlanCode => options.Value.Provisioning.DefaultPlanCode;

    public Task<bool> IsAssignableAsync(string planCode, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(planCode) ? Task.FromResult(false) : db.Plans.AsNoTracking().AnyAsync(p => p.Id == planCode && p.IsActive, ct);
}

/// <summary>
/// Kullanım sayımı: tüm <see cref="IUsageReporter"/>'ları <b>kiracı kapsamında</b> (<c>BeginScope</c>, filtre atlamadan) çağırır. Kayıt sayıları
/// <c>Platform:Usage:CacheSeconds</c> (300 sn) önbellekli (yumuşak limit: önbellek süresi içinde sınır bir miktar aşılabilir, bilinçli); kullanıcı sayısı
/// kesin ve önbelleksizdir.
/// </summary>
public sealed class UsageMeter(
    IEnumerable<IUsageReporter> reporters,
    ITenantContextSetter tenantSetter,
    ITenantContext tenant,
    HybridCache cache,
    IOptions<PlatformOptions> options,
    TimeProvider clock) : IUsageMeter
{
    private const string IdentityModule = "identity";
    private const string UsersActiveKey = "identity.users_active";
    private const string UsersPendingKey = "identity.users_pending";

    public async Task<UsageCollection> CollectAsync(Guid tenantId, CancellationToken ct)
    {
        var metrics = new Dictionary<string, long>(StringComparer.Ordinal);
        using (tenantSetter.BeginScope(tenantId))
        {
            foreach (var reporter in reporters)
            {
                foreach (var metric in await reporter.ReportAsync(ct).ConfigureAwait(false))
                {
                    metrics[metric.Key] = metric.Value;
                }
            }
        }

        var active = (int)metrics.GetValueOrDefault(UsersActiveKey);
        var pending = (int)metrics.GetValueOrDefault(UsersPendingKey);
        metrics.Remove(UsersActiveKey);
        metrics.Remove(UsersPendingKey);
        return new UsageCollection(active, pending, metrics);
    }

    public async Task<(int Active, int Pending)> CountUsersAsync(CancellationToken ct)
    {
        var reporter = reporters.FirstOrDefault(r => string.Equals(r.Module, IdentityModule, StringComparison.Ordinal));
        if (reporter is null)
        {
            return (0, 0);
        }

        var metrics = await reporter.ReportAsync(ct).ConfigureAwait(false);
        return ((int)(metrics.FirstOrDefault(m => m.Key == UsersActiveKey)?.Value ?? 0), (int)(metrics.FirstOrDefault(m => m.Key == UsersPendingKey)?.Value ?? 0));
    }

    public async Task<long> GetStorageBytesAsync(CancellationToken ct)
    {
        var reporter = reporters.FirstOrDefault(r => string.Equals(r.Module, EntitlementMath.FilesModule, StringComparison.Ordinal));
        if (reporter is null)
        {
            return 0;
        }

        var metrics = await reporter.ReportAsync(ct).ConfigureAwait(false);
        return metrics.FirstOrDefault(m => m.Key == EntitlementMath.StorageBytesKey)?.Value ?? 0;
    }

    public async Task<RecordCounts> GetRecordCountsAsync(CancellationToken ct)
    {
        var tenantId = tenant.TenantId;
        var seconds = options.Value.Usage.CacheSeconds;
        if (seconds <= 0)
        {
            return await ComputeAsync(tenantId, ct).ConfigureAwait(false);
        }

        var ttl = TimeSpan.FromSeconds(seconds);
        return await cache.GetOrCreateInContextAsync(
            EntitlementCacheKeys.Usage(tenantId),
            async token => await ComputeAsync(tenantId, token).ConfigureAwait(false),
            new HybridCacheEntryOptions { Expiration = ttl, LocalCacheExpiration = ttl },
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<RecordCounts> ComputeAsync(Guid tenantId, CancellationToken ct)
    {
        var usage = await CollectAsync(tenantId, ct).ConfigureAwait(false);
        return new RecordCounts(
            clock.GetUtcNow(),
            new Dictionary<string, long>(usage.Records, StringComparer.Ordinal),
            usage.Metrics.GetValueOrDefault(EntitlementMath.StorageBytesKey),
            usage.Metrics.GetValueOrDefault(EntitlementMath.FileCountKey));
    }
}

/// <summary>
/// <see cref="ILimitGuard"/>: <c>users</c> (<b>sert</b>) — kesin, önbelleksiz sayım (etkin + bekleyen davetli; pasif saymaz); sayımdan önce Identity'nin açık
/// transaction'ında <c>pg_advisory_xact_lock(hashtextextended('limit:users:'||tenantId, 0))</c> alınır → eşzamanlı üye eklemeler kiracı başına sıraya girer,
/// limit asla aşılmaz. <c>records</c> (<b>yumuşak</b>) — <c>Platform:Usage:CacheSeconds</c> önbellekli sayım. Sonlu olmayan (<c>null</c>) limit için sayım
/// <b>hiç yapılmaz</b> (<c>internal</c> plan sıfır ek sorgu). <c>used + delta &gt; max</c> → <c>402 plan.limit_exceeded</c>.
/// </summary>
public sealed class LimitGuard(
    ITenantContext tenant,
    ITenantEntitlements entitlements,
    IUsageMeter meter,
    IServiceProvider services) : ILimitGuard
{
    private const string IdentityModule = "identity";

    public async Task<Result> EnsureAsync(LimitDemand demand, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demand);
        if (!tenant.IsResolved)
        {
            return Result.Success();
        }

        var snapshot = await entitlements.GetAsync(tenant.TenantId, ct).ConfigureAwait(false);
        switch (demand.Key)
        {
            case LimitKeys.Users:
                return await EnsureUsersAsync(snapshot, demand, ct).ConfigureAwait(false);
            case LimitKeys.Records when demand.Module is { } module:
                return await EnsureRecordsAsync(snapshot, module, demand.Delta, ct).ConfigureAwait(false);
            case LimitKeys.Storage:
                return await EnsureStorageAsync(snapshot, demand, ct).ConfigureAwait(false);
            default:
                return Result.Success();
        }
    }

    private async Task<Result> EnsureUsersAsync(EntitlementSnapshot snapshot, LimitDemand demand, CancellationToken ct)
    {
        if (snapshot.MaxUsers is not { } max)
        {
            return Result.Success();
        }

        var identity = services.GetServices<IModuleUnitOfWork>().FirstOrDefault(u => string.Equals(u.ModuleName, IdentityModule, StringComparison.OrdinalIgnoreCase));
        if (identity is not null)
        {
            await identity.AcquireAdvisoryLockAsync("limit:users:" + tenant.TenantId.ToString("D"), ct).ConfigureAwait(false);
        }

        var (active, pending) = await meter.CountUsersAsync(ct).ConfigureAwait(false);
        var used = active + pending;
        return used + demand.Delta > max ? EntitlementErrors.Exceeded(LimitKeys.Users, null, max, used) : Result.Success();
    }

    /// <summary>
    /// <c>storage</c> (M8C, <b>sert</b>): kesin, önbelleksiz <c>files.storage_bytes</c>; sayımdan önce <b>Files</b> UnitOfWork'ünün açık transaction'ında
    /// <c>pg_advisory_xact_lock(hashtextextended('limit:storage:'||tenantId, 0))</c> alınır (eşzamanlı yüklemeler kiracı başına sıraya girer; kilit commit'e kadar
    /// tutulur, sayım aynı bağlantıda yapılır). Sınırsız (<c>null</c>) kotada kilit ve sayım <b>hiç yapılmaz</b>. <c>Delta</c> bayttır.
    /// </summary>
    private async Task<Result> EnsureStorageAsync(EntitlementSnapshot snapshot, LimitDemand demand, CancellationToken ct)
    {
        if (snapshot.MaxStorageBytes is not { } max)
        {
            return Result.Success();
        }

        var files = services.GetServices<IModuleUnitOfWork>().FirstOrDefault(u => string.Equals(u.ModuleName, EntitlementMath.FilesModule, StringComparison.OrdinalIgnoreCase));
        if (files is not null)
        {
            await files.AcquireAdvisoryLockAsync("limit:storage:" + tenant.TenantId.ToString("D"), ct).ConfigureAwait(false);
        }

        var used = await meter.GetStorageBytesAsync(ct).ConfigureAwait(false);
        return used + demand.Delta > max ? EntitlementErrors.Exceeded(LimitKeys.Storage, EntitlementMath.FilesModule, max, used) : Result.Success();
    }

    private async Task<Result> EnsureRecordsAsync(EntitlementSnapshot snapshot, string module, int delta, CancellationToken ct)
    {
        if (snapshot.MaxRecordsOf(module) is not { } max)
        {
            return Result.Success();
        }

        var counts = await meter.GetRecordCountsAsync(ct).ConfigureAwait(false);
        var used = counts.Records.GetValueOrDefault(module);
        return used + delta > max ? EntitlementErrors.Exceeded(LimitKeys.Records, module, max, used) : Result.Success();
    }
}

/// <summary>Kullanım anlık görüntüsü yazma: <c>INSERT … ON CONFLICT (tenant_id, day) DO UPDATE</c> (günde birden çok çalışma tek satır).</summary>
public sealed class UsageSnapshotWriter(PlatformDbContext db) : IUsageSnapshotWriter
{
    public async Task UpsertAsync(Guid tenantId, DateOnly day, UsageCollection usage, DateTime takenAtUtc, CancellationToken ct)
    {
        var metrics = System.Text.Json.JsonSerializer.Serialize(usage.Metrics);
        var active = usage.UsersActive;
        var pending = usage.UsersPending;
        await db.Database.ExecuteSqlAsync(
            $$"""
            INSERT INTO platform.usage_snapshots (tenant_id, day, users_active, users_pending, metrics, taken_at)
            VALUES ({{tenantId}}, {{day}}, {{active}}, {{pending}}, {{metrics}}::jsonb, {{takenAtUtc}})
            ON CONFLICT (tenant_id, day) DO UPDATE SET
                users_active = EXCLUDED.users_active,
                users_pending = EXCLUDED.users_pending,
                metrics = EXCLUDED.metrics,
                taken_at = EXCLUDED.taken_at
            """,
            ct).ConfigureAwait(false);
    }
}
