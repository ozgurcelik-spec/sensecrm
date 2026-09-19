namespace Crm.Modules.Identity.Domain;

/// <summary>
/// Identity hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları). Ortak kodlar (validation, forbidden,
/// not_found) <c>Crm.Shared.Kernel.Results.ErrorCodes</c>'tadır.
/// </summary>
public static class IdentityErrors
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string InvalidRefreshToken = "auth.invalid_refresh_token";
    public const string EmailTaken = "auth.email_taken";
    public const string LockedOut = "auth.locked_out";
    public const string UserDisabled = "auth.user_disabled";

    /// <summary>Kendi kendine kayıt kapalı (Registration:Mode=disabled; Production varsayılanı).</summary>
    public const string SignupDisabled = "auth.signup_disabled";

    /// <summary>Parola doğru ama kullanıcının aktif üyeliği olan organizasyon yok.</summary>
    public const string NoActiveOrganization = "auth.no_active_organization";

    public const string RoleSystemReadOnly = "role.system_readonly";
    public const string RoleInUse = "role.in_use";
    public const string RoleNameTaken = "role.name_taken";
    public const string RoleUnknownPermission = "role.unknown_permission";

    public const string MemberLastAdmin = "member.last_admin";
    public const string MemberExists = "member.exists";

    public const string InvalidLocale = "validation.locale";
    public const string InvalidTimeZone = "validation.time_zone";
    public const string PasswordTooShort = "validation.password_too_short";
    public const string Required = "validation.required";

    /// <summary>Mesaj parametre adları.</summary>
    public static class Args
    {
        public const string Minutes = "minutes";
        public const string Count = "count";
        public const string Permission = "permission";
    }
}

/// <summary>Domain sınırları (uzunluklar vb.). Ayar değil, veri modeli kısıtı.</summary>
public static class IdentityLimits
{
    public const int SlugMinLength = 3;
    public const int SlugMaxLength = 40;
    public const int DisplayNameMaxLength = 100;
    public const int OrganizationNameMaxLength = 200;
    public const int RoleNameMaxLength = 100;
    public const int RoleCodeMaxLength = 50;
    public const int EmailMaxLength = 254;
    public const int PasswordMaxLength = 128;
    public const int LocaleMaxLength = 10;
    public const int TimeZoneMaxLength = 60;
    public const int DeviceInfoMaxLength = 300;
    public const char SlugSeparator = '-';
}
