using Crm.Modules.Identity.Domain.Memberships;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Modules.Identity.Domain.Tokens;
using Crm.Modules.Identity.Domain.Users;

namespace Crm.Modules.Identity.Domain;

/// <summary>Repository portları (Onion: Domain tanımlar, Infrastructure implement eder).</summary>
public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);

    void Add(Tenant tenant);
}

/// <summary>Küresel kullanıcı hesapları (kiracı filtresi yok).</summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<User?> GetByEmailAsync(string email, CancellationToken ct);

    void Add(User user);
}

public interface IMembershipRepository
{
    /// <summary>Aktif kiracıdaki üyelik (kiracı filtresi altında; aktif/pasif fark etmez).</summary>
    Task<Membership?> GetByUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Kullanıcının tüm organizasyonlardaki aktif üyelikleri (giriş ve organizasyon listesi için). Kiracı filtresini
    /// bilinçli olarak aşar ve yalnız verilen kullanıcıya daraltılır; başka kullanıcıların üyeliği asla dönmez.
    /// </summary>
    Task<IReadOnlyList<Membership>> ListActiveOfUserAcrossTenantsAsync(Guid userId, CancellationToken ct);

    /// <summary>Aktif kiracıda, verilen rolde ve aktif olan üyelerin sayısı.</summary>
    Task<int> CountActiveWithRoleAsync(Guid roleId, CancellationToken ct);

    /// <summary>Aktif kiracıda rolü kullanan (aktif/pasif) üyelerin sayısı.</summary>
    Task<int> CountWithRoleAsync(Guid roleId, CancellationToken ct);

    void Add(Membership membership);
}

public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Role?> GetByCodeAsync(string code, CancellationToken ct);

    Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken ct);

    void Add(Role role);

    void Remove(Role role);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);

    Task<IReadOnlyList<RefreshToken>> GetFamilyAsync(Guid familyId, CancellationToken ct);

    void Add(RefreshToken token);
}
