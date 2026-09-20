using FluentValidation;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Application.Provisioning;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Modules.Identity.Domain.Memberships;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Observability;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Identity.Application.Auth;

// ---------------------------------------------------------------------------------------------------------------------
// Kayıt (Zoho tarzı self sign-up, K1): yeni organizasyon + sistem rolleri + kullanıcı Administrator olarak.
// ---------------------------------------------------------------------------------------------------------------------

[AnyAuthenticatedUser("Anonim kimlik akışı: kayıt (Registration:Mode ile denetlenir)")]
[TenantStatusExempt("Anonim kimlik akışı: istek bir Bearer başlığı taşısa bile kiracı durumundan bağımsızdır")]
public sealed record SignUpCommand(
    string OrganizationName,
    string DisplayName,
    string Email,
    string Password,
    string Locale,
    string? DeviceInfo,
    string? IpAddress) : ICommand<AuthResponse>;

public sealed class SignUpValidator : AbstractValidator<SignUpCommand>
{
    public SignUpValidator(IOptions<IdentityOptions> options)
    {
        RuleFor(x => x.OrganizationName).NotEmpty().MaximumLength(IdentityLimits.OrganizationNameMaxLength);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(IdentityLimits.DisplayNameMaxLength);
        RuleFor(x => x.Email).Email();
        RuleFor(x => x.Password).MeetsPasswordPolicy(options.Value.MinPasswordLength, x => x.Email);
        RuleFor(x => x.Locale).SupportedLocale();
    }
}

public sealed class SignUpHandler(
    IUserRepository users,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    OrganizationProvisioner provisioner,
    SessionIssuer sessions,
    TimeProvider clock) : ICommandHandler<SignUpCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(SignUpCommand command, CancellationToken cancellationToken)
    {
        if (await users.GetByEmailAsync(command.Email, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error.Conflict(IdentityErrors.EmailTaken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var provisioned = await provisioner.CreateAsync(command.OrganizationName, command.Locale, cancellationToken).ConfigureAwait(false);

        var user = User.Create(command.Email, command.DisplayName, command.Locale, hasher.Hash(command.Password));
        user.SetDefaultTenant(provisioned.Tenant.Id);
        user.RecordSuccessfulLogin(now);
        users.Add(user);

        memberships.Add(Membership.Create(provisioned.Tenant.Id, user.Id, provisioned.Administrator.Id, now));

        return sessions.Issue(user, new SessionContext(provisioned.Tenant, provisioned.Administrator), command.DeviceInfo, command.IpAddress);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Giriş: e-posta + parola; kullanıcının varsayılan (en son kullanılan) veya ilk aktif organizasyonunda oturum açılır.
//
// Sertleştirme (M3):
//  - Zamanlama: kullanıcı yoksa da bir sahte hash doğrulanır (yanıt süresi hesap varlığını sızdırmaz).
//  - Sıra: parola HER ZAMAN önce doğrulanır; locked_out / user_disabled yalnız parola DOĞRU olduğunda söylenir, aksi hâlde
//    genel auth.invalid_credentials döner (saldırgan hesabın kilitli/pasif olduğunu öğrenemez).
//  - Kilit: hesap kilidi eşiği (Identity:MaxFailedAccessAttempts, varsayılan 10) tek bir IP'nin IP+hesap eşiğinden (5) yüksektir;
//    tek IP bilinen bir e-postayı tek başına kilitleyemez. Kilitliyken yanlış deneme kilidi UZATMAZ. Ek olarak e-posta anahtarlı
//    hız kovası (RateLimiting:LoginEmail) ve IP başına auth hız sınırı (RateLimiting:Auth) vardır.
// ---------------------------------------------------------------------------------------------------------------------

[AnyAuthenticatedUser("Anonim kimlik akışı: giriş")]
[TenantStatusExempt("Anonim kimlik akışı: istek bir Bearer başlığı taşısa bile kiracı durumundan bağımsızdır")]
public sealed record LoginCommand(string Email, string Password, string? DeviceInfo, string? IpAddress) : ICommand<AuthResponse>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(IdentityLimits.EmailMaxLength);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(IdentityLimits.PasswordMaxLength);
    }
}

public sealed class LoginHandler(
    IUserRepository users,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    ILoginThrottle throttle,
    SessionIssuer sessions,
    IIdentityUnitOfWork unitOfWork,
    IOptions<IdentityOptions> options,
    TimeProvider clock) : ICommandHandler<LoginCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var normalizedEmail = User.Normalize(command.Email);

        // E-posta anahtarlı ikinci hız kovası + IP+hesap engeli: parola doğrulamasına (pahalı) girmeden reddedilir.
        if (!throttle.TryAcquireEmailBucket(normalizedEmail) || throttle.IsBlocked(command.IpAddress, normalizedEmail))
        {
            CrmMetrics.LoginOutcome(LoginOutcomes.RateLimited);
            return new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests);
        }

        var user = await users.GetByEmailAsync(command.Email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            hasher.VerifyDummy(command.Password);
            throttle.RecordFailure(command.IpAddress, normalizedEmail);
            CrmMetrics.LoginOutcome(LoginOutcomes.InvalidCredentials);
            return Error.Unauthorized(IdentityErrors.InvalidCredentials);
        }

        var verification = hasher.Check(user.PasswordHash, command.Password);
        if (verification == PasswordVerification.Failed)
        {
            throttle.RecordFailure(command.IpAddress, normalizedEmail);
            if (!user.IsLockedOut(now))
            {
                user.RecordFailedAccess(now, new LockoutPolicy(options.Value.MaxFailedAccessAttempts, TimeSpan.FromMinutes(options.Value.LockoutMinutes)));
                if (user.IsLockedOut(now))
                {
                    CrmMetrics.LockoutStarted();
                }

                // Hata sonucu UnitOfWorkBehaviour'da kaydedilmez; kilitleme sayacı yine de kalıcı olmalı.
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            CrmMetrics.LoginOutcome(LoginOutcomes.InvalidCredentials);
            return Error.Unauthorized(IdentityErrors.InvalidCredentials);
        }

        // Parola doğru: artık kilit/pasif durumu söylenebilir.
        var canSignIn = user.CanSignIn(now);
        if (canSignIn.IsFailure)
        {
            CrmMetrics.LoginOutcome(canSignIn.Error.Code == IdentityErrors.LockedOut ? LoginOutcomes.LockedOut : LoginOutcomes.Inactive);
            return canSignIn.Error;
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.RehashPassword(hasher.Hash(command.Password)); // L5: eski/düşük yinelemeli hash yükseltilir.
        }

        var active = await memberships.ListActiveOfUserAcrossTenantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var ordered = active.OrderByDescending(m => m.TenantId == user.DefaultTenantId).ThenBy(m => m.JoinedAt).Select(m => m.TenantId);

        // M7: erişimi "none" olan (askı blocked, silme bekleyen) kiracılar atlanır; hepsi engelliyse 403 tenant.suspended.
        string? blockedReason = null;
        foreach (var tenantId in ordered)
        {
            var resolution = await sessions.ResolveDetailedAsync(user.Id, tenantId, cancellationToken).ConfigureAwait(false);
            if (resolution.Session is { } session)
            {
                throttle.Reset(command.IpAddress, normalizedEmail);
                user.RecordSuccessfulLogin(now);
                user.SetDefaultTenant(tenantId);
                CrmMetrics.LoginOutcome(LoginOutcomes.Success);
                return sessions.Issue(user, session, command.DeviceInfo, command.IpAddress);
            }

            blockedReason ??= resolution.BlockedReason;
        }

        CrmMetrics.LoginOutcome(blockedReason is not null ? LoginOutcomes.Suspended : LoginOutcomes.NoOrganization);
        return blockedReason is not null ? EntitlementErrors.Suspended(blockedReason) : Error.Forbidden(IdentityErrors.NoActiveOrganization);
    }
}

/// <summary>Giriş sonucu metrik değerleri (<c>crm.auth.logins{outcome}</c>; sabit küme, düşük kardinalite).</summary>
internal static class LoginOutcomes
{
    public const string Success = "success";
    public const string InvalidCredentials = "invalid_credentials";
    public const string RateLimited = "rate_limited";
    public const string LockedOut = "locked_out";
    public const string Inactive = "inactive";
    public const string NoOrganization = "no_organization";
    public const string Suspended = "suspended";
}

// ---------------------------------------------------------------------------------------------------------------------
// Yenileme: refresh token rotasyonu (atomik); iptal edilmiş token tekrar gelirse tüm aile kapatılır (reuse detection), kısa bir
// eşzamanlılık toleransı hariç (aynı istemciden, döndürmeden hemen sonra gelen ikinci istek hırsızlık sayılmaz). Oturum ömrü mutlaktır.
// ---------------------------------------------------------------------------------------------------------------------

[AnyAuthenticatedUser("Anonim kimlik akışı: refresh token ile oturum yenileme")]
[TenantStatusExempt("Anonim kimlik akışı: istek bir Bearer başlığı taşısa bile kiracı durumundan bağımsızdır")]
public sealed record RefreshTokenCommand(string RefreshToken, string? DeviceInfo, string? IpAddress) : ICommand<AuthResponse>;

public sealed class RefreshTokenValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(RefreshTokenLimits.MaxLength);
}

/// <summary>Refresh token girdi sınırı (base64url 32 bayt ≈ 43 karakter; makul üst sınır hash maliyetini sınırlar).</summary>
public static class RefreshTokenLimits
{
    public const int MaxLength = 256;
}

public sealed class RefreshTokenHandler(
    IRefreshTokenRepository refreshTokens,
    IUserRepository users,
    ISecretGenerator secrets,
    SessionIssuer sessions,
    IIdentityUnitOfWork unitOfWork,
    IOptions<IdentityOptions> options,
    TimeProvider clock) : ICommandHandler<RefreshTokenCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await refreshTokens.GetByHashAsync(secrets.Hash(command.RefreshToken), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            CrmMetrics.RefreshRejectedFor("unknown");
            return Error.Unauthorized(IdentityErrors.InvalidRefreshToken);
        }

        if (existing.RevokedAt is not null)
        {
            if (!await IsConcurrentRotationAsync(existing, command, now, cancellationToken).ConfigureAwait(false))
            {
                // Reuse detection: kullanılmış/iptal edilmiş token tekrar geldi → aynı aile tamamen kapatılır ve kalıcılaştırılır.
                await RevokeFamilyAsync(existing, now, cancellationToken).ConfigureAwait(false);
                CrmMetrics.RefreshReuseDetected();
                CrmMetrics.RefreshRejectedFor("reuse");
            }
            else
            {
                CrmMetrics.RefreshRejectedFor("concurrent");
            }

            return Error.Unauthorized(IdentityErrors.InvalidRefreshToken);
        }

        if (!existing.IsActive(now))
        {
            CrmMetrics.RefreshRejectedFor("expired");
            return Error.Unauthorized(IdentityErrors.InvalidRefreshToken); // süresi doldu (token veya aile mutlak ömrü)
        }

        var user = await users.GetByIdAsync(existing.UserId, cancellationToken).ConfigureAwait(false);
        var session = user is null || user.CanSignIn(now).IsFailure
            ? null
            : await sessions.ResolveAsync(user.Id, existing.OrganizationId, cancellationToken).ConfigureAwait(false);

        if (user is null || session is null)
        {
            existing.Revoke(now);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            CrmMetrics.RefreshRejectedFor("user_inactive");
            return Error.Unauthorized(IdentityErrors.InvalidRefreshToken);
        }

        // Atomik dönüşüm: eşzamanlı iki yenilemeden yalnız biri kazanır; kaybeden aileyi KAPATMAZ (kazananın token'ı geçerli kalır).
        var response = await sessions.RotateAsync(user, session, existing, command.DeviceInfo, command.IpAddress, cancellationToken).ConfigureAwait(false);
        return response is null ? Error.Unauthorized(IdentityErrors.InvalidRefreshToken) : response;
    }

    /// <summary>
    /// Döndürülmüş token, döndürmeden kısa süre sonra (<c>RefreshReuseGraceSeconds</c>) ve döndürmeyi yapan istemciyle (IP + kullanıcı
    /// aracısı) aynı istemciden geliyorsa eşzamanlı yenileme sayılır (aile kapatılmaz). Başka istemciden veya gecikmeli ise hırsızlık.
    /// </summary>
    private async Task<bool> IsConcurrentRotationAsync(Domain.Tokens.RefreshToken existing, RefreshTokenCommand command, DateTime now, CancellationToken ct)
    {
        if (!existing.IsRotated || existing.RevokedAt is not { } revokedAt
            || now - revokedAt > TimeSpan.FromSeconds(options.Value.RefreshReuseGraceSeconds))
        {
            return false;
        }

        var successor = await refreshTokens.GetByHashAsync(existing.ReplacedByTokenHash!, ct).ConfigureAwait(false);
        return successor is not null && successor.IsSameClient(command.DeviceInfo, command.IpAddress);
    }

    private async Task RevokeFamilyAsync(Domain.Tokens.RefreshToken existing, DateTime now, CancellationToken ct)
    {
        foreach (var token in await refreshTokens.GetFamilyAsync(existing.FamilyId, ct).ConfigureAwait(false))
        {
            token.Revoke(now);
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Çıkış: verilen refresh token'ın ailesi (oturum) iptal edilir. Bilinmeyen token için de 204 (bilgi sızdırılmaz).
// ---------------------------------------------------------------------------------------------------------------------

[AnyAuthenticatedUser("Anonim kimlik akışı: çıkış (refresh token sahibi)")]
[TenantStatusExempt("Askıdaki/engelli kiracıda da çıkış yapılabilmeli (engel ekranı)")]
public sealed record LogoutCommand(string RefreshToken) : ICommand;

public sealed class LogoutValidator : AbstractValidator<LogoutCommand>
{
    public LogoutValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(RefreshTokenLimits.MaxLength);
}

public sealed class LogoutHandler(IRefreshTokenRepository refreshTokens, ISecretGenerator secrets, TimeProvider clock) : ICommandHandler<LogoutCommand>
{
    public async Task<Result> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await refreshTokens.GetByHashAsync(secrets.Hash(command.RefreshToken), cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            foreach (var token in await refreshTokens.GetFamilyAsync(existing.FamilyId, cancellationToken).ConfigureAwait(false))
            {
                token.Revoke(now);
            }
        }

        return Result.Success();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Organizasyon değiştirme (Zoho "org switch"): kullanıcının aktif üyesi olduğu başka organizasyon için yeni oturum.
// Bekleyen (kabul edilmemiş) davet aktif organizasyon sayılmaz.
// ---------------------------------------------------------------------------------------------------------------------

[AnyAuthenticatedUser("Kimliği doğrulanmış kullanıcı yalnız kendi aktif üyeliği olan organizasyona geçer; handler üyeliği doğrular")]
[TenantStatusExempt("Askıdaki kiracıdan çalışan bir organizasyona geçiş yapılabilmeli (hedef kiracının durumu handler'da denetlenir)")]
public sealed record SwitchOrganizationCommand(Guid OrganizationId, string? DeviceInfo, string? IpAddress) : ICommand<AuthResponse>;

public sealed class SwitchOrganizationValidator : AbstractValidator<SwitchOrganizationCommand>
{
    public SwitchOrganizationValidator() => RuleFor(x => x.OrganizationId).NotEmpty();
}

public sealed class SwitchOrganizationHandler(IUserRepository users, SessionIssuer sessions, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<SwitchOrganizationCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(SwitchOrganizationCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.CanSignIn(clock.GetUtcNow().UtcDateTime).IsFailure)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var resolution = await sessions.ResolveDetailedAsync(user.Id, command.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (resolution.Session is not { } session)
        {
            return resolution.BlockedReason is { } reason ? EntitlementErrors.Suspended(reason) : Error.Forbidden(ErrorCodes.Forbidden);
        }

        user.SetDefaultTenant(session.Tenant.Id);
        return sessions.Issue(user, session, command.DeviceInfo, command.IpAddress);
    }
}
