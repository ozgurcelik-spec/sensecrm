namespace Sense.Crm.Modules.Identity.Contracts;

/// <summary>Bir platform yöneticisi hesabının özeti (kişisel veri: yalnız e-posta ve görünen ad; yalnız platform yöneticisine sunulur).</summary>
public sealed record PlatformAdminInfo(Guid UserId, string Email, string DisplayName, bool IsActive, DateTimeOffset? LastLoginAt);

/// <summary>
/// Platform yöneticisi dizini (kiracı filtresi dışı; Platform yaşam döngüsü kuralları ve konsol içindir). "Aktif platform yöneticisi" = hesap aktif
/// <b>ve</b> <c>is_platform_admin</c> bayrağı var. Bir kiracının "aktif platform yöneticisi üyesi" = o kiracıda aktif (bekleyen değil, pasifleştirilmemiş)
/// üyeliği olan aktif platform yöneticisi.
/// </summary>
public interface IPlatformAdminDirectory
{
    /// <summary>Kiracıda en az bir aktif platform yöneticisi üyesi var mı (silinemez/askıya alınamaz/imha edilemez kuralı).</summary>
    Task<bool> HasActivePlatformAdminAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Aktif platform yöneticisi üyesi olan tüm kiracı kimlikleri (Migrator backfill: <c>is_system</c> işaretlemesi).</summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithActivePlatformAdminAsync(CancellationToken ct = default);

    /// <summary>Verilen kullanıcının aktif üyeliği olan kiracılar (create-platform-admin: terfi eden hesabın kiracılarını sistem işaretlemek için).</summary>
    Task<IReadOnlyList<Guid>> ListActiveTenantsOfUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Bayrağı olan tüm hesaplar (pasif olanlar dahil), e-postaya göre sıralı.</summary>
    Task<IReadOnlyList<PlatformAdminInfo>> ListAsync(CancellationToken ct = default);
}

/// <summary>Platform yöneticisi yetkisini geri alma sonucu.</summary>
public enum PlatformAdminRevocation
{
    Revoked = 0,
    NotFound = 1,
    NotAPlatformAdmin = 2,

    /// <summary>Son aktif platform yöneticisi geri alınamaz/pasifleştirilemez (kilitlenmeyi önler).</summary>
    LastActiveAdmin = 3,
}

/// <summary>
/// Platform yöneticisi yaşam döngüsü (M6): yetkiyi (bayrağı) geri alır, isteğe bağlı hesabı pasifleştirir ve <b>tüm refresh token ailelerini iptal eder</b>
/// (oturumlar kapanır; access token ≤ 15 dk yaşasa da platform yetkisi her istekte veritabanından doğrulandığından anında düşer). Son aktif platform
/// yöneticisi geri alınamaz. Kendi saveChanges'ini yapar (Identity modülünün birim işi).
/// </summary>
public interface IPlatformAdminManager
{
    Task<PlatformAdminRevocation> RevokeAsync(Guid userId, bool deactivateAccount, CancellationToken ct = default);
}

/// <summary>Step-up yeniden doğrulama sonucu (<see cref="IStepUpAuthenticator"/>).</summary>
public enum StepUpOutcome
{
    Verified = 0,

    /// <summary>Parola verilmedi.</summary>
    PasswordRequired = 1,

    /// <summary>Parola yanlış (hesabın hatalı deneme sayacı ve (IP, hesap) sayacı artar).</summary>
    Invalid = 2,

    /// <summary>Deneme sınırı aşıldı ya da hesap kilitli: parola doğrulanmadan reddedildi.</summary>
    RateLimited = 3,
}

/// <summary>
/// Yıkıcı platform komutları için <b>step-up yeniden kimlik doğrulama</b>: çağıran platform yöneticisinin kendi parolası sunucuda doğrulanır
/// (çalınmış access token tek başına yetmez). Yanlış denemeler hesabın kilit sayacına ve (IP, hesap) hız sınırına yazılır; kilit/sınır aşılınca 429.
/// </summary>
public interface IStepUpAuthenticator
{
    Task<StepUpOutcome> VerifyAsync(Guid userId, string? currentPassword, string? ipAddress, CancellationToken ct = default);
}
