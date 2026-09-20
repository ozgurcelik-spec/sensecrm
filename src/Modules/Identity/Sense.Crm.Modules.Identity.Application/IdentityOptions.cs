namespace Sense.Crm.Modules.Identity.Application;

/// <summary>Identity ayarları (appsettings "Identity" bölümü). Domain bu değerleri parametre olarak alır.</summary>
public sealed class IdentityOptions
{
    /// <summary>
    /// Hesap kilidi eşiği (tüm IP'lerden gelen ardışık hatalı parola sayısı). Varsayılan 10: tek bir IP'nin hesap başına eşiği
    /// (<see cref="LoginThrottleMaxFailures"/>, 5) daha düşüktür; yani tek bir saldırgan bilinen bir e-postayı tek başına kilitleyemez (M3).
    /// </summary>
    public int MaxFailedAccessAttempts { get; set; } = IdentityDefaults.MaxFailedAccessAttempts;

    public int LockoutMinutes { get; set; } = IdentityDefaults.LockoutMinutes;

    public int MinPasswordLength { get; set; } = IdentityDefaults.MinPasswordLength;

    /// <summary>Access token ömrü (K6: 15 dk).</summary>
    public int AccessTokenMinutes { get; set; } = IdentityDefaults.AccessTokenMinutes;

    /// <summary>Tek bir refresh token'ın ömrü (gün). Aile ömrü (<see cref="RefreshFamilyDays"/>) her zaman üst sınırdır.</summary>
    public int RefreshTokenDays { get; set; } = IdentityDefaults.RefreshTokenDays;

    /// <summary>Oturum (refresh token ailesi) mutlak ömrü: ilk girişten itibaren bu süre sonunda yenilemeyle uzatılamaz (M5).</summary>
    public int RefreshFamilyDays { get; set; } = IdentityDefaults.RefreshFamilyDays;

    /// <summary>
    /// Platform yöneticisi oturumunun (refresh token ailesi) mutlak ömrü, saat (C-SEC2 M4; varsayılan 8). Yalnız <c>IsPlatformAdmin</c> hesaplar için
    /// <see cref="RefreshFamilyDays"/> yerine geçerlidir; ilk girişten itibaren bu süre sonunda yeniden giriş gerekir.
    /// </summary>
    public int PlatformAdminRefreshFamilyHours { get; set; } = IdentityDefaults.PlatformAdminRefreshFamilyHours;

    /// <summary>
    /// Platform yöneticisi oturumunun <b>boşta kalma</b> süresi, dakika (varsayılan 60): tek bir refresh token'ın ömrüdür; bu süre boyunca yenileme yapılmazsa
    /// (istemci açık değil) oturum kapanır. Aile ömrü her zaman üst sınırdır.
    /// </summary>
    public int PlatformAdminRefreshIdleMinutes { get; set; } = IdentityDefaults.PlatformAdminRefreshIdleMinutes;

    /// <summary>Döndürülmüş token'ın bu süre içinde aynı istemciden yeniden gelmesi hırsızlık sayılmaz (eşzamanlı yenileme; sn).</summary>
    public int RefreshReuseGraceSeconds { get; set; } = IdentityDefaults.RefreshReuseGraceSeconds;

    /// <summary>Aynı IP + aynı hesap için pencere başına en çok hatalı giriş; aşılınca o IP+hesap çifti pencere boyunca reddedilir (M3).</summary>
    public int LoginThrottleMaxFailures { get; set; } = IdentityDefaults.LoginThrottleMaxFailures;

    public int LoginThrottleWindowMinutes { get; set; } = IdentityDefaults.LoginThrottleWindowMinutes;

    /// <summary>PBKDF2-HMAC-SHA512 yineleme sayısı (L5). Eski (düşük sayılı) hash'ler doğrulanır ve girişte yeniden hash'lenir.</summary>
    public int PasswordHashIterations { get; set; } = IdentityDefaults.PasswordHashIterations;

    public string DefaultTimeZone { get; set; } = IdentityDefaults.DefaultTimeZone;
}

public static class IdentityDefaults
{
    public const int MaxFailedAccessAttempts = 10;
    public const int LockoutMinutes = 15;
    public const int MinPasswordLength = 10;
    public const int AccessTokenMinutes = 15;
    public const int RefreshTokenDays = 30;
    public const int RefreshFamilyDays = 30;
    public const int PlatformAdminRefreshFamilyHours = 8;
    public const int PlatformAdminRefreshIdleMinutes = 60;
    public const int RefreshReuseGraceSeconds = 10;
    public const int LoginThrottleMaxFailures = 5;
    public const int LoginThrottleWindowMinutes = 15;
    public const int PasswordHashIterations = 210_000;
    public const string DefaultTimeZone = "Europe/Istanbul";
    public const int TokenBytes = 32;
    public const int SlugSuffixAttempts = 5;
    public const int GeneratedPasswordLength = 20;
    public const int PlatformAdminMinPasswordLength = 12;
}
