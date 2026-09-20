using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Sense.Crm.Modules.Identity.Domain.Memberships;
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
