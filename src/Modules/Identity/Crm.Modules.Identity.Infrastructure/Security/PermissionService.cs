using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Infrastructure.Persistence;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Modules;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure.Caching;
using Crm.Shared.Infrastructure.Messaging.Behaviours;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Crm.Modules.Identity.Infrastructure.Security;

/// <summary>
/// Kullanıcının aktif organizasyondaki etkin izin seti: aktif üyelik → rol → izinler (K7, basit RBAC; veri kapsamı, birim yetkisi ve
/// kullanıcı bazlı override katmanları bilinçli olarak yok). HybridCache'te kiracı+kullanıcı anahtarıyla
/// saklanır; üyelik değişiminde kullanıcı, rol değişiminde organizasyon etiketiyle geçersizlenir.
/// </summary>
public sealed class PermissionService(
    IdentityDbContext db,
    HybridCache cache,
    ITenantContext tenant,
    IOptions<CachingOptions> caching) : IPermissionService, IPermissionCacheInvalidator
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    public async Task<bool> HasAsync(Guid userId, string permission, CancellationToken cancellationToken = default) =>
        (await GetPermissionsAsync(userId, cancellationToken).ConfigureAwait(false)).Contains(permission);

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!tenant.IsResolved)
        {
            return Empty;
        }

        var snapshot = await cache.GetOrCreateInContextAsync(
            Key(tenant.TenantId, userId),
            async ct => await LoadAsync(userId, ct).ConfigureAwait(false),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(caching.Value.PermissionExpirationMinutes) },
            [TenantTag(tenant.TenantId)],
            cancellationToken).ConfigureAwait(false);

        return new HashSet<string>(snapshot.Permissions, StringComparer.Ordinal);
    }

    public Task InvalidateUserAsync(Guid tenantId, Guid userId, CancellationToken ct) => cache.RemoveAsync(Key(tenantId, userId), ct).AsTask();

    public Task InvalidateTenantAsync(Guid tenantId, CancellationToken ct) => cache.RemoveByTagAsync(TenantTag(tenantId), ct).AsTask();

    private async Task<PermissionSnapshot> LoadAsync(Guid userId, CancellationToken ct)
    {
        // Kiracı bağlamı kaybolursa filtre boş kiracıyla çalışır ve boş set önbelleğe yazılır; sessizce devam etmek yerine hata.
        if (!tenant.IsResolved)
        {
            throw new InvalidOperationException(PermissionServiceMessages.TenantContextLost);
        }

        var permissions = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Join(db.Roles.AsNoTracking(), m => m.RoleId, r => r.Id, (m, r) => r.Permissions)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new PermissionSnapshot(permissions?.ToArray() ?? []);
    }

    private static string Key(Guid tenantId, Guid userId) => CacheKeys.Join(CacheKeyPrefixes.Permission, tenantId.ToString("N"), userId.ToString("N"));

    private static string TenantTag(Guid tenantId) => CacheKeys.Join(CacheKeyPrefixes.PermissionTenant, tenantId.ToString("N"));

    public sealed record PermissionSnapshot(string[] Permissions);
}

internal static class PermissionServiceMessages
{
    public const string TenantContextLost = "Permission snapshot requested without a resolved tenant context.";
}

public static class CacheKeyPrefixes
{
    public const string Permission = "perm";
    public const string PermissionTenant = "perm-tenant";
}

/// <summary>Tüm modüllerin izin katalogları (IModule.Permissions) birleşimi; anahtara göre tekilleştirilir.</summary>
public sealed class PermissionCatalog : IPermissionCatalog
{
    private readonly Lazy<IReadOnlyList<Permission>> _all;
    private readonly Lazy<HashSet<string>> _keys;

    public PermissionCatalog(IReadOnlyList<IModule> modules)
    {
        _all = new Lazy<IReadOnlyList<Permission>>(() =>
            modules.SelectMany(m => m.Permissions)
                .GroupBy(p => p.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(p => p.Group, StringComparer.Ordinal)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .ToList());
        _keys = new Lazy<HashSet<string>>(() => _all.Value.Select(p => p.Key).ToHashSet(StringComparer.Ordinal));
    }

    public IReadOnlyList<Permission> All => _all.Value;

    public bool Exists(string permission) => _keys.Value.Contains(permission);
}
