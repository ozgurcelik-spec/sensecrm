using Sense.Crm.Modules.Identity.Domain.Roles;
using Sense.Crm.Modules.Identity.Domain.Tenants;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Shared.Contracts.Persistence;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Identity.Application;

/// <summary>Identity'nin SaveChanges portu: başarısız sonuçla dönen ama durum yazması gereken akışlar
/// (hatalı parola sayacı, refresh token yeniden kullanım tespiti) için. Normal komutları UnitOfWorkBehaviour kaydeder.</summary>
public interface IIdentityUnitOfWork : IUnitOfWork;

public enum PasswordVerification
{
    Failed,
    Success,

    /// <summary>Parola doğru ama hash eski/düşük yineleme sayılı: çağıran yeniden hash'leyip kaydetmelidir (L5).</summary>
    SuccessRehashNeeded,
}

/// <summary>Parola hash'leme (Infrastructure: ASP.NET Core PasswordHasher, PBKDF2-HMAC-SHA512, yapılandırılabilir yineleme).</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerification Check(string hash, string password);

    /// <summary>Kullanıcı bulunamadığında da aynı maliyetle bir doğrulama yapar (zamanlama farkından hesap keşfini önler; M3). Sonuç her zaman başarısızdır.</summary>
    void VerifyDummy(string password);
}

/// <summary>Sonuç kısayolu: hash doğru mu (yeniden hash gerekse de doğru sayılır).</summary>
public static class PasswordHasherExtensions
{
    public static bool Verify(this IPasswordHasher hasher, string hash, string password) => hasher.Check(hash, password) != PasswordVerification.Failed;
}

/// <summary>
/// Giriş azaltma (M3), bellek içi ve tek örnek için tasarlanmıştır (bkz. runbook: çok örnekli kurulumda paylaşımlı depo gerekir).
/// İki bağımsız koruma: (1) e-posta anahtarlı hız kovası (cömert; tüm IP'ler), (2) aynı IP + aynı hesap için hatalı deneme sayacı
/// (o IP+hesap çifti eşiği aşınca pencere boyunca reddedilir). Hesap kilidi ayrıca kullanıcı kaydındadır.
/// </summary>
public interface ILoginThrottle
{
    /// <summary>E-posta kovasından bir deneme hakkı ister; kova doluysa false.</summary>
    bool TryAcquireEmailBucket(string normalizedEmail);

    /// <summary>Bu IP + hesap çifti hatalı deneme eşiğini aştı mı (aşıldıysa parola doğrulanmadan reddedilir).</summary>
    bool IsBlocked(string? ip, string normalizedEmail);

    void RecordFailure(string? ip, string normalizedEmail);

    void Reset(string? ip, string normalizedEmail);
}

/// <summary>Token hash'leme (SHA-256) ve rastgele token üretimi.</summary>
public interface ISecretGenerator
{
    string NewToken(int bytes = IdentityDefaults.TokenBytes);

    string Hash(string token);

    /// <summary>Tek seferlik parola (okunabilir, karışabilen karakterler hariç); yalnız bir kez gösterilir.</summary>
    string NewPassword(int length = IdentityDefaults.GeneratedPasswordLength);

    /// <summary>Kısa rastgele sonek (slug çakışmalarında).</summary>
    string NewSuffix();
}

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

/// <summary>JWT üretimi (RS256). Token aktif organizasyonu (`tid`) taşır (K6).</summary>
public interface ITokenService
{
    AccessToken IssueAccessToken(User user, Tenant tenant, Role role);

    TimeSpan RefreshTokenLifetime { get; }

    /// <summary>Oturum ailesinin mutlak ömrü (M5).</summary>
    TimeSpan RefreshFamilyLifetime { get; }

    /// <summary>Kullanıcıya özgü tek refresh token ömrü (boşta kalma): platform yöneticisi için kısa (C-SEC2 M4), diğerleri <see cref="RefreshTokenLifetime"/>.</summary>
    TimeSpan RefreshTokenLifetimeFor(User user);

    /// <summary>Kullanıcıya özgü oturum ailesi mutlak ömrü: platform yöneticisi için kısa (varsayılan 8 saat), diğerleri <see cref="RefreshFamilyLifetime"/>.</summary>
    TimeSpan RefreshFamilyLifetimeFor(User user);
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

    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct);

    Task<IReadOnlyList<OrganizationSummaryDto>> ListOrganizationsOfUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Hesap sahibinin bekleyen davetleri (kiracılar arası, yalnız verilen kullanıcı; tenant filtresi bilinçli aşılır).</summary>
    Task<IReadOnlyList<InvitationDto>> ListInvitationsOfUserAsync(Guid userId, CancellationToken ct);

    Task<AuditPageDto> GetAuditPageAsync(int page, int pageSize, CancellationToken ct);

    Task<AuditPageDto> GetEntityAuditPageAsync(string entityType, string entityId, int page, int pageSize, CancellationToken ct);
}
