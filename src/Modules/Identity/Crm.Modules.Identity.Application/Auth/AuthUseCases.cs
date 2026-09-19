using Crm.Modules.Identity.Application.Roles;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Domain.Memberships;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Modules.Identity.Domain.Users;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Kernel.Results;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Crm.Modules.Identity.Application.Auth;

// ---------------------------------------------------------------------------------------------------------------------
// Kayıt (Zoho tarzı self sign-up, K1): yeni organizasyon + sistem rolleri + kullanıcı Administrator olarak.
// ---------------------------------------------------------------------------------------------------------------------

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
        RuleFor(x => x.Password).Password(options.Value.MinPasswordLength);
        RuleFor(x => x.Locale).SupportedLocale();
    }
}

public sealed class SignUpHandler(
    IUserRepository users,
    ITenantRepository tenants,
    IRoleRepository roles,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    IPermissionCatalog catalog,
    ISecretGenerator secrets,
    SessionIssuer sessions,
    IOptions<IdentityOptions> options,
    TimeProvider clock) : ICommandHandler<SignUpCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(SignUpCommand command, CancellationToken cancellationToken)
    {
        if (await users.GetByEmailAsync(command.Email, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error.Conflict(IdentityErrors.EmailTaken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var slug = await UniqueSlugAsync(Tenant.SlugFrom(command.OrganizationName), cancellationToken).ConfigureAwait(false);
        var tenant = Tenant.Create(command.OrganizationName, slug, command.Locale, options.Value.DefaultTimeZone);
        tenants.Add(tenant);

        // Kiracı verisi açıkça yeni organizasyonun TenantId'siyle yazılır (anonim istek: kiracı bağlamı yok).
        var seeded = SeedSystemRoles(tenant.Id, catalog, roles);

        var user = User.Create(command.Email, command.DisplayName, command.Locale, hasher.Hash(command.Password));
        user.SetDefaultTenant(tenant.Id);
        user.RecordSuccessfulLogin(now);
        users.Add(user);

        var administrator = seeded[SystemRoleCodes.Administrator];
        memberships.Add(Membership.Create(tenant.Id, user.Id, administrator.Id, now));

        return sessions.Issue(user, new SessionContext(tenant, administrator), rotateFrom: null, command.DeviceInfo, command.IpAddress);
    }

    /// <summary>Tüm sistem rollerini katalogdaki izinlerle oluşturur.</summary>
    public static IReadOnlyDictionary<string, Role> SeedSystemRoles(Guid tenantId, IPermissionCatalog catalog, IRoleRepository roles)
    {
        var result = new Dictionary<string, Role>(StringComparer.Ordinal);
        foreach (var code in SystemRoleCodes.All)
        {
            var role = Role.CreateSystem(tenantId, code, SystemRoleDefinitions.PermissionsFor(code, catalog.All));
            roles.Add(role);
            result[code] = role;
        }

        return result;
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var candidate = baseSlug;
        for (var attempt = 0; attempt < IdentityDefaults.SlugSuffixAttempts; attempt++)
        {
            if (!await tenants.SlugExistsAsync(candidate, ct).ConfigureAwait(false))
            {
                return candidate;
            }

            candidate = string.Concat(baseSlug, IdentityLimits.SlugSeparator.ToString(), secrets.NewSuffix());
        }

        return string.Concat(baseSlug, IdentityLimits.SlugSeparator.ToString(), Guid.NewGuid().ToString("N")[..8]);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Giriş: e-posta + parola; kullanıcının varsayılan (en son kullanılan) veya ilk aktif organizasyonunda oturum açılır.
// ---------------------------------------------------------------------------------------------------------------------

public sealed record LoginCommand(string Email, string Password, string? DeviceInfo, string? IpAddress) : ICommand<AuthResponse>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class LoginHandler(
    IUserRepository users,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    SessionIssuer sessions,
    IIdentityUnitOfWork unitOfWork,
    IOptions<IdentityOptions> options,
    TimeProvider clock) : ICommandHandler<LoginCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await users.GetByEmailAsync(command.Email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.Unauthorized(IdentityErrors.InvalidCredentials);
        }

        var canSignIn = user.CanSignIn(now);
        if (canSignIn.IsFailure)
        {
            return canSignIn.Error;
        }

        if (!hasher.Verify(user.PasswordHash, command.Password))
        {
            user.RecordFailedAccess(now, new LockoutPolicy(options.Value.MaxFailedAccessAttempts, TimeSpan.FromMinutes(options.Value.LockoutMinutes)));

            // Hata sonucu UnitOfWorkBehaviour'da kaydedilmez; kilitleme sayacı yine de kalıcı olmalı.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Error.Unauthorized(IdentityErrors.InvalidCredentials);
        }

        var active = await memberships.ListActiveOfUserAcrossTenantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var ordered = active.OrderByDescending(m => m.TenantId == user.DefaultTenantId).ThenBy(m => m.JoinedAt).Select(m => m.TenantId);

        foreach (var tenantId in ordered)
        {
            if (await sessions.ResolveAsync(user.Id, tenantId, cancellationToken).ConfigureAwait(false) is { } session)
            {
                user.RecordSuccessfulLogin(now);
                user.SetDefaultTenant(tenantId);
                return sessions.Issue(user, session, rotateFrom: null, command.DeviceInfo, command.IpAddress);
            }
        }

        return Error.Forbidden(IdentityErrors.NoActiveOrganization);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Yenileme: refresh token rotasyonu; iptal edilmiş token tekrar gelirse tüm aile kapatılır (reuse detection).
// ---------------------------------------------------------------------------------------------------------------------

public sealed record RefreshTokenCommand(string RefreshToken, string? DeviceInfo, string? IpAddress) : ICommand<AuthResponse>;

public sealed class RefreshTokenValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenValidator() => RuleFor(x => x.RefreshToken).NotEmpty();
}

public sealed class RefreshTokenHandler(
    IRefreshTokenRepository refreshTokens,
    IUserRepository users,
    ISecretGenerator secrets,
    SessionIssuer sessions,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<RefreshTokenCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await refreshTokens.GetByHashAsync(secrets.Hash(command.RefreshToken), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return Error.Unauthorized(IdentityErrors.InvalidRefreshToken);
        }

        if (!existing.IsActive(now))
        {
            if (existing.RevokedAt is not null)
            {
                // Reuse detection: kullanılmış/iptal edilmiş token tekrar geldi → aynı aile tamamen kapatılır ve kalıcılaştırılır.
                foreach (var token in await refreshTokens.GetFamilyAsync(existing.FamilyId, cancellationToken).ConfigureAwait(false))
                {
                    token.Revoke(now);
                }

                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return Error.Unauthorized(IdentityErrors.InvalidRefreshToken);
        }

        var user = await users.GetByIdAsync(existing.UserId, cancellationToken).ConfigureAwait(false);
        var session = user is null || user.CanSignIn(now).IsFailure
            ? null
            : await sessions.ResolveAsync(user.Id, existing.OrganizationId, cancellationToken).ConfigureAwait(false);

        if (user is null || session is null)
        {
            existing.Revoke(now);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Error.Unauthorized(IdentityErrors.InvalidRefreshToken);
        }

        return sessions.Issue(user, session, rotateFrom: existing, command.DeviceInfo, command.IpAddress);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Çıkış: verilen refresh token'ın ailesi (oturum) iptal edilir. Bilinmeyen token için de 204 (bilgi sızdırılmaz).
// ---------------------------------------------------------------------------------------------------------------------

public sealed record LogoutCommand(string RefreshToken) : ICommand;

public sealed class LogoutValidator : AbstractValidator<LogoutCommand>
{
    public LogoutValidator() => RuleFor(x => x.RefreshToken).NotEmpty();
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
// ---------------------------------------------------------------------------------------------------------------------

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

        var session = await sessions.ResolveAsync(user.Id, command.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        user.SetDefaultTenant(session.Tenant.Id);
        return sessions.Issue(user, session, rotateFrom: null, command.DeviceInfo, command.IpAddress);
    }
}
