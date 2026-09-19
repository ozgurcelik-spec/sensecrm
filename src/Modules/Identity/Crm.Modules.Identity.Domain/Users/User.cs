using System.Globalization;
using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Kernel.ValueObjects;

namespace Crm.Modules.Identity.Domain.Users;

/// <summary>Kilitleme politikası (ayarlardan gelir; domain değerleri parametre olarak alır).</summary>
public readonly record struct LockoutPolicy(int MaxFailedAttempts, TimeSpan Duration);

/// <summary>
/// Küresel kullanıcı hesabı (K1, Zoho modeli): kiracıya ait değildir; organizasyonlara <see cref="Memberships.Membership"/>
/// ile bağlanır ve aralarında geçiş yapar. E-posta tüm sistemde benzersizdir.
/// </summary>
public sealed class User : AggregateRoot<Guid>
{
    private User()
    {
    }

    private User(Guid id, EmailAddress email, string displayName, string locale, string passwordHash, bool mustChangePassword) : base(id)
    {
        Email = email.Value;
        NormalizedEmail = Normalize(email.Value);
        DisplayName = displayName;
        Locale = locale;
        PasswordHash = passwordHash;
        SecurityStamp = NewStamp();
        IsActive = true;
        MustChangePassword = mustChangePassword;
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>Kullanıcının dil tercihi (tr | en, K8).</summary>
    public string Locale { get; private set; } = string.Empty;

    /// <summary>Parola değişiminde yenilenir.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    public int FailedAccessCount { get; private set; }

    public DateTime? LockoutEndUtc { get; private set; }

    public DateTime? LastLoginAt { get; private set; }

    /// <summary>Ürün (platform) ekibi hesabı; POST /platform/organizations ile organizasyon açabilir; kiracı verisine ek erişim vermez.</summary>
    public bool IsPlatformAdmin { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Parola yönetici tarafından üretilmiş geçici paroladır; değiştirilene kadar yalnız <c>POST /me/password</c> vb. çalışır.</summary>
    public bool MustChangePassword { get; private set; }

    /// <summary>Girişte açılacak organizasyon: en son geçiş yapılan (hâlâ aktif üyeyse).</summary>
    public Guid? DefaultTenantId { get; private set; }

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    /// <summary>
    /// <paramref name="mustChangePassword"/>: yönetici tarafından açılan hesapta parola sunucu üretimidir ve tek seferliktir; kullanıcı
    /// değiştirene kadar yalnız parola değiştirme uçları çalışır (H4).
    /// </summary>
    public static User Create(string email, string displayName, string locale, string passwordHash, bool mustChangePassword = false)
    {
        var user = new User(
            Guid.CreateVersion7(),
            EmailAddress.Of(email),
            Guard.MaxLength(Guard.NotEmpty(displayName), IdentityLimits.DisplayNameMaxLength),
            Guard.NotEmpty(locale),
            Guard.NotEmpty(passwordHash),
            mustChangePassword);
        user.Raise(new UserCreated(user.Id, user.Email));
        return user;
    }

    /// <summary>
    /// Parolayı değiştirir: <c>SecurityStamp</c> yenilenir, <c>MustChangePassword</c> temizlenir, kilit/sayaç sıfırlanır. Diğer
    /// oturumların (refresh token aileleri) iptali çağıranın işidir.
    /// </summary>
    public void ChangePassword(string newPasswordHash)
    {
        PasswordHash = Guard.NotEmpty(newPasswordHash);
        SecurityStamp = NewStamp();
        MustChangePassword = false;
        FailedAccessCount = 0;
        LockoutEndUtc = null;
    }

    public Result CanSignIn(DateTime nowUtc)
    {
        if (!IsActive)
        {
            return Error.Unauthorized(IdentityErrors.UserDisabled);
        }

        if (LockoutEndUtc is { } end && end > nowUtc)
        {
            var minutes = Math.Ceiling((end - nowUtc).TotalMinutes).ToString(CultureInfo.InvariantCulture);
            return Error.Unauthorized(IdentityErrors.LockedOut, (IdentityErrors.Args.Minutes, minutes));
        }

        return Result.Success();
    }

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;

    /// <summary>Parola değişmeden hash'i günceller (yineleme sayısı yükseltmesi, L5); güvenlik damgası değişmez.</summary>
    public void RehashPassword(string newPasswordHash) => PasswordHash = Guard.NotEmpty(newPasswordHash);

    public void RecordFailedAccess(DateTime nowUtc, LockoutPolicy policy)
    {
        FailedAccessCount++;
        if (FailedAccessCount >= policy.MaxFailedAttempts)
        {
            LockoutEndUtc = nowUtc.Add(policy.Duration);
            FailedAccessCount = 0;
        }
    }

    public void RecordSuccessfulLogin(DateTime nowUtc)
    {
        FailedAccessCount = 0;
        LockoutEndUtc = null;
        LastLoginAt = nowUtc;
    }

    public void UpdateProfile(string? displayName, string? locale)
    {
        if (displayName is not null)
        {
            DisplayName = Guard.MaxLength(Guard.NotEmpty(displayName), IdentityLimits.DisplayNameMaxLength);
        }

        if (locale is not null)
        {
            Locale = Guard.NotEmpty(locale);
        }
    }

    public void SetDefaultTenant(Guid tenantId) => DefaultTenantId = tenantId;

    /// <summary>
    /// Hesabı platform yöneticisi yapar (Production'da kendi kendine kayıt kapalıyken organizasyon açabilen ürün/işletim ekibi hesabı).
    /// İdempotenttir; yalnız işletim aracı (Migrator <c>create-platform-admin</c>) çağırır, HTTP'den yükseltme yolu yoktur.
    /// </summary>
    public void GrantPlatformAdmin() => IsPlatformAdmin = true;

    private static string NewStamp() => Guid.NewGuid().ToString("N");
}

public sealed record UserCreated(Guid UserId, string Email) : DomainEvent;
