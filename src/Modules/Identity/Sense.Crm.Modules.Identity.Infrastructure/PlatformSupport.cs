using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Modules.Identity.Domain.Memberships;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Modules.Identity.Infrastructure.Security;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Contracts.Usage;
using Sense.Crm.Shared.Infrastructure.Messaging.Behaviours;

namespace Sense.Crm.Modules.Identity.Infrastructure;

/// <summary>
/// Identity kullanım sayaçları (M7; yalnız <c>COUNT</c>, kişisel veri yok, kiracı kapsamında): <c>identity.users_active</c> (etkin üyelik),
/// <c>identity.users_pending</c> (bekleyen davet), <c>identity.profile_completed</c> (0/1). Pasif üyelik hiçbirine sayılmaz.
/// </summary>
public sealed class IdentityUsageReporter(IdentityDbContext db, ITenantContext tenant) : IUsageReporter
{
    public string Module => IdentityDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        if (!tenant.IsResolved)
        {
            return [];
        }

        var tenantId = tenant.TenantId;
        var active = await db.Memberships.AsNoTracking().LongCountAsync(m => m.Status == MembershipStatus.Active && m.IsActive, ct).ConfigureAwait(false);
        var pending = await db.Memberships.AsNoTracking().LongCountAsync(m => m.Status == MembershipStatus.Pending, ct).ConfigureAwait(false);
        var completed = await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.ProfileCompletedAt != null, ct).ConfigureAwait(false);
        return
        [
            new UsageMetric("identity.users_active", active),
            new UsageMetric("identity.users_pending", pending),
            new UsageMetric("identity.profile_completed", completed ? 1 : 0),
        ];
    }
}

/// <summary>Platform yöneticisi doğrulayıcısı (D5): hesap aktif <b>ve</b> bayrak veritabanında var (JWT bayrağına güvenilmez).</summary>
public sealed class PlatformAdminVerifier(IdentityDbContext db) : IPlatformAdminVerifier
{
    public Task<bool> IsPlatformAdminAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive && u.IsPlatformAdmin, ct);
}

/// <summary>
/// KVKK imhası, Identity hesap adımı (sıra 50 — genel modül adımından <b>önce</b>, çünkü üyelikler silinmeden hangi hesapların yalnız bu kiracıya ait olduğu bilinmelidir):
/// tek transaction'da (a) <b>başka hiçbir kiracıda üyeliği olmayan</b> ve <b>platform yöneticisi olmayan</b> üyelerin hesapları belirlenir, (b) o hesapların refresh token'ları,
/// (c) bu kiracının tüm üyelikleri, (d) belirlenen hesaplar silinir. Başka kiracıda üyeliği olan (ortak) hesap <b>kalır</b>; yalnız bu kiracıdaki üyeliği gider.
/// Ham SQL kiracı-körüdür (envanterde listeli); kiracı kimliği her zaman parametredir. Idempotenttir.
/// </summary>
public sealed class IdentityAccountEraser(IdentityDbContext db) : ITenantDataEraser
{
    public string Name => "identity-accounts";

    public int Order => 50;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        var report = new Dictionary<string, long>(StringComparer.Ordinal);
        await db.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                CREATE TEMP TABLE erase_doomed_users ON COMMIT DROP AS
                SELECT DISTINCT m.user_id
                FROM identity.memberships m
                JOIN identity.users u ON u.id = m.user_id
                WHERE m.tenant_id = {tenantId}
                  AND NOT u.is_platform_admin
                  AND NOT EXISTS (SELECT 1 FROM identity.memberships o WHERE o.user_id = m.user_id AND o.tenant_id <> {tenantId})
                """,
                token).ConfigureAwait(false);
            report["identity.refresh_tokens(accounts)"] = await db.Database.ExecuteSqlAsync(
                $"DELETE FROM identity.refresh_tokens WHERE user_id IN (SELECT user_id FROM erase_doomed_users)", token).ConfigureAwait(false);
            report["identity.memberships"] = await db.Database.ExecuteSqlAsync(
                $"DELETE FROM identity.memberships WHERE tenant_id = {tenantId}", token).ConfigureAwait(false);
            report["identity.users(only-this-tenant)"] = await db.Database.ExecuteSqlAsync(
                $"DELETE FROM identity.users WHERE id IN (SELECT user_id FROM erase_doomed_users)", token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return new EraseReport(report);
    }
}

/// <summary>
/// KVKK imhası, Identity kiracı adımı (sıra 800): bu kiracıya bağlı kalan refresh token'lar (ortak hesaplarınki), <c>identity.tenants</c> satırı; izin önbelleği
/// (<c>perm-tenant:{T}</c> etiketi) geçersiz kılınır. Roller/üyelikler genel modül adımıyla (100) ve <see cref="IdentityAccountEraser"/> ile gitmiştir.
/// </summary>
public sealed class IdentityTenantEraser(IdentityDbContext db, HybridCache cache) : ITenantDataEraser
{
    public string Name => "identity-tenant";

    public int Order => 800;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        var tokens = await db.Database.ExecuteSqlAsync($"DELETE FROM identity.refresh_tokens WHERE organization_id = {tenantId}", ct).ConfigureAwait(false);
        var tenants = await db.Database.ExecuteSqlAsync($"DELETE FROM identity.tenants WHERE id = {tenantId}", ct).ConfigureAwait(false);
        await cache.RemoveByTagAsync(CacheKeys.Join(CacheKeyPrefixes.PermissionTenant, tenantId.ToString("N")), ct).ConfigureAwait(false);
        return new EraseReport(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["identity.refresh_tokens(tenant)"] = tokens,
            ["identity.tenants"] = tenants,
        });
    }
}

/// <summary>
/// <see cref="IPlatformAdminDirectory"/> (C-SEC2 H1/M6): kiracı filtresi bilinçli aşılır; yalnız <b>aktif hesap + platform yöneticisi bayrağı + aktif üyelik</b> sayılır.
/// Salt okunur, kişisel veri yalnız <see cref="ListAsync"/>'te (konsol).
/// </summary>
public sealed class PlatformAdminDirectory(IdentityDbContext db) : IPlatformAdminDirectory
{
    public Task<bool> HasActivePlatformAdminAsync(Guid tenantId, CancellationToken ct = default) =>
        db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.IsActive && m.Status == MembershipStatus.Active)
            .AnyAsync(m => db.Users.Any(u => u.Id == m.UserId && u.IsActive && u.IsPlatformAdmin), ct);

    public async Task<IReadOnlyList<Guid>> ListTenantsWithActivePlatformAdminAsync(CancellationToken ct = default) =>
        await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.IsActive && m.Status == MembershipStatus.Active && db.Users.Any(u => u.Id == m.UserId && u.IsActive && u.IsPlatformAdmin))
            .Select(m => m.TenantId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> ListActiveTenantsOfUserAsync(Guid userId, CancellationToken ct = default) =>
        await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive && m.Status == MembershipStatus.Active)
            .Select(m => m.TenantId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PlatformAdminInfo>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Users.AsNoTracking().Where(u => u.IsPlatformAdmin)
            .OrderBy(u => u.NormalizedEmail)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.IsActive, u.LastLoginAt })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new PlatformAdminInfo(r.Id, r.Email, r.DisplayName, r.IsActive, r.LastLoginAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null)).ToList();
    }
}

/// <summary>
/// <see cref="IPlatformAdminManager"/> (M6): tek transaction'da (a) bayrağı kaldırır, (b) isteğe bağlı hesabı pasifleştirir, (c) tüm refresh token'ları iptal eder.
/// <b>Son aktif platform yöneticisi</b> geri alınamaz. "Son" sayımı ile yazma arasındaki yarışı kapatmak için işlem, istişari işlem kilidi (<c>pg_advisory_xact_lock</c>) altında yapılır.
/// </summary>
public sealed class PlatformAdminManager(IdentityDbContext db, TimeProvider clock) : IPlatformAdminManager
{
    private const string LockKey = "identity.platform-admins";

    public async Task<PlatformAdminRevocation> RevokeAsync(Guid userId, bool deactivateAccount, CancellationToken ct = default)
    {
        var outcome = PlatformAdminRevocation.Revoked;
        await db.ExecuteInTransactionAsync(async token =>
        {
            await db.AcquireAdvisoryLockAsync(LockKey, token).ConfigureAwait(false);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, token).ConfigureAwait(false);
            if (user is null)
            {
                outcome = PlatformAdminRevocation.NotFound;
                return;
            }

            if (!user.IsPlatformAdmin)
            {
                outcome = PlatformAdminRevocation.NotAPlatformAdmin;
                return;
            }

            // Bu hesap aktif bir yönetici ise başka en az bir aktif yönetici kalmalı.
            if (user.IsActive && !await db.Users.AnyAsync(u => u.Id != userId && u.IsActive && u.IsPlatformAdmin, token).ConfigureAwait(false))
            {
                outcome = PlatformAdminRevocation.LastActiveAdmin;
                return;
            }

            user.RevokePlatformAdmin();
            if (deactivateAccount)
            {
                user.Deactivate();
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            var now = clock.GetUtcNow().UtcDateTime;
            await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return outcome;
    }
}

/// <summary>
/// <see cref="IStepUpAuthenticator"/> (H2): giriş ile aynı korumalar — hesap kilidi, (IP, hesap) hatalı deneme eşiği ve kalıcı hata sayacı. Yanlış parola hesabın
/// <c>FailedAccessCount</c>'unu artırır (eşikte hesap kilitlenir); böylece çalınmış bir access token ile parola kaba kuvvetle denenemez. Parola ve hash asla loglanmaz.
/// </summary>
public sealed class StepUpAuthenticator(
    IdentityDbContext db,
    IPasswordHasher hasher,
    ILoginThrottle throttle,
    IOptions<IdentityOptions> options,
    TimeProvider clock) : IStepUpAuthenticator
{
    public async Task<StepUpOutcome> VerifyAsync(Guid userId, string? currentPassword, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            return StepUpOutcome.PasswordRequired;
        }

        if (currentPassword.Length > IdentityLimits.PasswordMaxLength)
        {
            return StepUpOutcome.Invalid;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            return StepUpOutcome.Invalid;
        }

        var normalized = User.Normalize(user.Email);
        if (user.IsLockedOut(now) || throttle.IsBlocked(ipAddress, normalized))
        {
            return StepUpOutcome.RateLimited;
        }

        if (!hasher.Verify(user.PasswordHash, currentPassword))
        {
            throttle.RecordFailure(ipAddress, normalized);
            user.RecordFailedAccess(now, new LockoutPolicy(options.Value.MaxFailedAccessAttempts, TimeSpan.FromMinutes(options.Value.LockoutMinutes)));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return StepUpOutcome.Invalid;
        }

        throttle.Reset(ipAddress, normalized);
        return StepUpOutcome.Verified;
    }
}
