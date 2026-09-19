using Crm.Modules.Identity.Contracts;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Domain.Memberships;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Modules.Identity.Domain.Tokens;
using Crm.Modules.Identity.Domain.Users;
using Crm.Shared.Contracts.Context;
using Microsoft.EntityFrameworkCore;

namespace Crm.Modules.Identity.Infrastructure.Persistence;

public sealed class TenantRepository(IdentityDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct) => db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct) => db.Tenants.AnyAsync(t => t.Slug == slug, ct);

    public void Add(Tenant tenant) => db.Tenants.Add(tenant);
}

public sealed class UserRepository(IdentityDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) => db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = User.Normalize(email);
        return db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
    }

    public void Add(User user) => db.Users.Add(user);
}

public sealed class MembershipRepository(IdentityDbContext db) : IMembershipRepository
{
    public Task<Membership?> GetByUserAsync(Guid userId, CancellationToken ct) => db.Memberships.FirstOrDefaultAsync(m => m.UserId == userId, ct);

    public async Task<IReadOnlyList<Membership>> ListActiveOfUserAcrossTenantsAsync(Guid userId, CancellationToken ct) =>
        // Bilinçli kiracı filtresi aşımı (K2): yalnız verilen kullanıcının kendi üyelikleri; giriş/organizasyon listesi içindir.
        await db.Memberships.IgnoreQueryFilters([Shared.Infrastructure.Persistence.ModuleDbContext.TenantFilter])
            .Where(m => m.UserId == userId && m.IsActive)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync(ct);

    public Task<int> CountActiveWithRoleAsync(Guid roleId, CancellationToken ct) => db.Memberships.CountAsync(m => m.RoleId == roleId && m.IsActive, ct);

    public Task<int> CountWithRoleAsync(Guid roleId, CancellationToken ct) => db.Memberships.CountAsync(m => m.RoleId == roleId, ct);

    public void Add(Membership membership) => db.Memberships.Add(membership);
}

public sealed class RoleRepository(IdentityDbContext db) : IRoleRepository
{
    public Task<Role?> GetByIdAsync(Guid id, CancellationToken ct) => db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<Role?> GetByCodeAsync(string code, CancellationToken ct) => db.Roles.FirstOrDefaultAsync(r => r.IsSystem && r.Code == code, ct);

    public Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct)
    {
        var lowered = name.ToLower();
        return db.Roles.AnyAsync(r => r.Name.ToLower() == lowered && (excludeId == null || r.Id != excludeId), ct);
    }

    public void Add(Role role) => db.Roles.Add(role);

    public void Remove(Role role) => db.Roles.Remove(role);
}

public sealed class RefreshTokenRepository(IdentityDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct) => db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<RefreshToken>> GetFamilyAsync(Guid familyId, CancellationToken ct) =>
        await db.RefreshTokens.Where(t => t.FamilyId == familyId).ToListAsync(ct);

    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);
}

/// <summary><see cref="IMemberLookup"/>: aktif kiracıda aktif üyelik var mı (kiracı filtresi altında).</summary>
public sealed class MemberLookup(IdentityDbContext db, ITenantContext tenant) : IMemberLookup
{
    public Task<bool> IsActiveMemberAsync(Guid userId, CancellationToken cancellationToken = default) =>
        tenant.IsResolved ? db.Memberships.AnyAsync(m => m.UserId == userId && m.IsActive, cancellationToken) : Task.FromResult(false);

    public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (!tenant.IsResolved || userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = userIds.Distinct().ToArray();
        return await (from m in db.Memberships.AsNoTracking()
                      where ids.Contains(m.UserId)
                      join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                      select new { u.Id, u.DisplayName })
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
    }
}

/// <summary><see cref="ITenantDirectory"/>: organizasyonlar küresel tablodur (kiracı filtresi yok); yalnız sistem işleri kullanır.</summary>
public sealed class TenantDirectory(IdentityDbContext db) : ITenantDirectory
{
    public async Task<IReadOnlyList<TenantInfo>> ListAllAsync(CancellationToken cancellationToken = default) =>
        await db.Tenants.AsNoTracking().Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TenantInfo(t.Id, t.Name, t.DefaultLocale, t.TimeZone))
            .ToListAsync(cancellationToken);

    public Task<TenantInfo?> FindAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        db.Tenants.AsNoTracking().Where(t => t.Id == tenantId)
            .Select(t => new TenantInfo(t.Id, t.Name, t.DefaultLocale, t.TimeZone))
            .FirstOrDefaultAsync(cancellationToken);
}
