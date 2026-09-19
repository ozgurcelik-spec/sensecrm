using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Modules.Identity.Domain.Users;
using Crm.Shared.Contracts.Persistence;
using Crm.Shared.Contracts.Security;

namespace Crm.Modules.Identity.Application;

/// <summary>Identity'nin SaveChanges portu: başarısız sonuçla dönen ama durum yazması gereken akışlar
/// (hatalı parola sayacı, refresh token yeniden kullanım tespiti) için. Normal komutları UnitOfWorkBehaviour kaydeder.</summary>
public interface IIdentityUnitOfWork : IUnitOfWork;

/// <summary>Parola hash'leme (Infrastructure: ASP.NET Core PasswordHasher, PBKDF2).</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

/// <summary>Token hash'leme (SHA-256) ve rastgele token üretimi.</summary>
public interface ISecretGenerator
{
    string NewToken(int bytes = IdentityDefaults.TokenBytes);

    string Hash(string token);

    /// <summary>Kısa rastgele sonek (slug çakışmalarında).</summary>
    string NewSuffix();
}

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

/// <summary>JWT üretimi (RS256). Token aktif organizasyonu (`tid`) taşır (K6).</summary>
public interface ITokenService
{
    AccessToken IssueAccessToken(User user, Tenant tenant, Role role);

    TimeSpan RefreshTokenLifetime { get; }
}

/// <summary>Tüm modüllerin katkı verdiği birleşik izin kataloğu.</summary>
public interface IPermissionCatalog
{
    IReadOnlyList<Permission> All { get; }

    bool Exists(string permission);
}

/// <summary>İzin cache'ini geçersiz kılma (kullanıcı veya organizasyon bazlı).</summary>
public interface IPermissionCacheInvalidator
{
    Task InvalidateUserAsync(Guid tenantId, Guid userId, CancellationToken ct);

    Task InvalidateTenantAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Okuma tarafı (sorgu handler'ları): projeksiyonlar Infrastructure'da EF ile, kiracı filtresi altında üretilir.</summary>
public interface IIdentityReadStore
{
    Task<IReadOnlyList<MemberDto>> ListMembersAsync(CancellationToken ct);

    Task<MemberDto?> GetMemberAsync(Guid userId, CancellationToken ct);

    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct);

    Task<IReadOnlyList<OrganizationSummaryDto>> ListOrganizationsOfUserAsync(Guid userId, CancellationToken ct);

    Task<AuditPageDto> GetAuditPageAsync(int page, int pageSize, CancellationToken ct);
}
