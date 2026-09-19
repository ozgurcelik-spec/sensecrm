using Sense.Crm.Modules.Identity.Domain.Memberships;
using Sense.Crm.Modules.Identity.Domain.Roles;
using Sense.Crm.Modules.Identity.Domain.Tenants;
using Sense.Crm.Modules.Identity.Domain.Tokens;
using Sense.Crm.Modules.Identity.Domain.Users;

namespace Sense.Crm.Modules.Identity.Domain;

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

    /// <summary>
    /// Kullanıcının tüm organizasyonlardaki <b>bekleyen davetleri</b> (kiracı filtresini bilinçli aşar; yalnız verilen kullanıcı).
    /// Yalnız hesap sahibinin kendi daveti okunabilir.
    /// </summary>
    Task<IReadOnlyList<Membership>> ListPendingOfUserAcrossTenantsAsync(Guid userId, CancellationToken ct);

    /// <summary>Bekleyen davet: yalnız verilen kullanıcıya ait ve bekleyen üyelik; başkasının daveti/etkin üyelik null (kiracı filtresini bilinçli aşar).</summary>
    Task<Membership?> GetPendingInvitationAsync(Guid membershipId, Guid userId, CancellationToken ct);

    void Add(Membership membership);

    void Remove(Membership membership);
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

    /// <summary>
    /// Atomik dönüşüm (koşullu <c>UPDATE ... WHERE revoked_at IS NULL</c>): token hâlâ iptal edilmemişse iptal eder ve
    /// <paramref name="replacedByTokenHash"/>'i yazar. İki eşzamanlı yenilemeden yalnız biri true alır; kaybeden false.
    /// </summary>
    Task<bool> TryRotateAsync(Guid tokenId, string replacedByTokenHash, DateTime nowUtc, CancellationToken ct);

    /// <summary>Kullanıcının tüm aktif refresh token'larını iptal eder (parola değişimi: diğer tüm oturumlar kapanır). İptal edilen sayı.</summary>
    Task<int> RevokeAllOfUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct);

    void Add(RefreshToken token);
}
