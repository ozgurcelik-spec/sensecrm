namespace Crm.Modules.Identity.Application;

/// <summary>Identity ayarları (appsettings "Identity" bölümü). Domain bu değerleri parametre olarak alır.</summary>
public sealed class IdentityOptions
{
    public int MaxFailedAccessAttempts { get; set; } = IdentityDefaults.MaxFailedAccessAttempts;

    public int LockoutMinutes { get; set; } = IdentityDefaults.LockoutMinutes;

    public int MinPasswordLength { get; set; } = IdentityDefaults.MinPasswordLength;

    /// <summary>Access token ömrü (K6: 15 dk).</summary>
    public int AccessTokenMinutes { get; set; } = IdentityDefaults.AccessTokenMinutes;

    public int RefreshTokenDays { get; set; } = IdentityDefaults.RefreshTokenDays;

    public string DefaultTimeZone { get; set; } = IdentityDefaults.DefaultTimeZone;
}

public static class IdentityDefaults
{
    public const int MaxFailedAccessAttempts = 5;
    public const int LockoutMinutes = 15;
    public const int MinPasswordLength = 8;
    public const int AccessTokenMinutes = 15;
    public const int RefreshTokenDays = 30;
    public const string DefaultTimeZone = "Europe/Istanbul";
    public const int TokenBytes = 32;
    public const int SlugSuffixAttempts = 5;
}
