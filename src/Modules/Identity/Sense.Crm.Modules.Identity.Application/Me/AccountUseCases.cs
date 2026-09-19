using FluentValidation;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Application.Auth;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Identity.Application.Me;

// ---------------------------------------------------------------------------------------------------------------------
// Parola değiştirme (H4-d): POST /me/password. Mevcut parola doğrulanır, yeni parola politikaya uyar, güvenlik damgası yenilenir,
// kullanıcının TÜM refresh token aileleri iptal edilir (diğer cihazlar/oturumlar kapanır), MustChangePassword temizlenir ve
// aktif organizasyon için yeni bir oturum (AuthResponse) döner.
// ---------------------------------------------------------------------------------------------------------------------

[AnyAuthenticatedUser("Kullanıcı yalnız kendi parolasını değiştirir (mevcut parolayı bilmesi gerekir)")]
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword, string? DeviceInfo, string? IpAddress) : ICommand<AuthResponse>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(IdentityLimits.PasswordMaxLength);
        RuleFor(x => x.NewPassword).NotEmpty();
    }
}

public sealed class ChangePasswordHandler(
    ICurrentUser currentUser,
    ITenantContext tenant,
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ILoginThrottle throttle,
    SessionIssuer sessions,
    IIdentityUnitOfWork unitOfWork,
    IOptions<IdentityOptions> options,
    TimeProvider clock) : ICommandHandler<ChangePasswordCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !tenant.IsResolved)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.CanSignIn(now).IsFailure)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        // Çalınmış access token ile mevcut parolayı kaba kuvvetle denemeyi engelle: giriş ile aynı IP+hesap sayacı ve hesap kilidi.
        var normalizedEmail = User.Normalize(user.Email);
        if (throttle.IsBlocked(command.IpAddress, normalizedEmail))
        {
            return new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests);
        }

        if (!hasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            throttle.RecordFailure(command.IpAddress, normalizedEmail);
            user.RecordFailedAccess(now, new LockoutPolicy(options.Value.MaxFailedAccessAttempts, TimeSpan.FromMinutes(options.Value.LockoutMinutes)));
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Error.Unauthorized(IdentityErrors.InvalidCredentials);
        }

        PasswordPolicy.EnsureValid(command.NewPassword, user.Email, options.Value.MinPasswordLength, nameof(command.NewPassword));

        var session = await sessions.ResolveAsync(user.Id, tenant.TenantId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        throttle.Reset(command.IpAddress, normalizedEmail);
        user.ChangePassword(hasher.Hash(command.NewPassword));

        // SecurityStamp benzeri: tüm mevcut oturumlar (bu cihaz dahil) kapanır; yeni aile aşağıda açılır.
        await refreshTokens.RevokeAllOfUserAsync(user.Id, now, cancellationToken).ConfigureAwait(false);
        return sessions.Issue(user, session, command.DeviceInfo, command.IpAddress);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Davetler (H4-b): mevcut bir hesap organizasyona onayı olmadan katılmaz; bekleyen üyelik hesap sahibine görünür ve yalnız o kabul/red eder.
// Davet satırı başka bir kiracıdadır: okuma/yazma bilinçli olarak kiracı filtresini aşar ama HER ZAMAN (davet kimliği + hesap sahibi +
// bekliyor) üçlüsüyle daraltılır; başkasının daveti asla görünmez/kullanılamaz.
// ---------------------------------------------------------------------------------------------------------------------

[AnyAuthenticatedUser("Kullanıcı yalnız kendi bekleyen davetlerini görür")]
public sealed record ListMyInvitationsQuery : IQuery<IReadOnlyList<InvitationDto>>;

public sealed class ListMyInvitationsHandler(ICurrentUser currentUser, IIdentityReadStore readStore) : IQueryHandler<ListMyInvitationsQuery, IReadOnlyList<InvitationDto>>
{
    public async Task<Result<IReadOnlyList<InvitationDto>>> Handle(ListMyInvitationsQuery query, CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? Result.Success(await readStore.ListInvitationsOfUserAsync(userId, cancellationToken).ConfigureAwait(false))
            : Error.Unauthorized(ErrorCodes.Unauthenticated);
}

[AnyAuthenticatedUser("Kullanıcı yalnız kendisine gelen daveti kabul eder")]
public sealed record AcceptInvitationCommand(Guid InvitationId) : ICommand;

public sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationValidator() => RuleFor(x => x.InvitationId).NotEmpty();
}

public sealed class AcceptInvitationHandler(
    ICurrentUser currentUser,
    IMembershipRepository memberships,
    ITenantRepository tenants,
    ITenantContextSetter tenantSetter,
    IPermissionCacheInvalidator permissionCache,
    IIdentityUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<AcceptInvitationCommand>
{
    public async Task<Result> Handle(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var invitation = await memberships.GetPendingInvitationAsync(command.InvitationId, userId, cancellationToken).ConfigureAwait(false);
        var tenant = invitation is null ? null : await tenants.GetByIdAsync(invitation.TenantId, cancellationToken).ConfigureAwait(false);
        if (invitation is null || tenant is null || !tenant.IsActive)
        {
            return Error.NotFound(IdentityErrors.InvitationNotFound);
        }

        // Yazma, davetin ait olduğu organizasyonun kiracı kapsamında yapılır (AuditTenantInterceptor başka kiracıya yazmayı reddeder).
        using (tenantSetter.BeginScope(tenant.Id, tenant.Slug))
        {
            invitation.Accept(clock.GetUtcNow().UtcDateTime);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await permissionCache.InvalidateUserAsync(tenant.Id, userId, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}

[AnyAuthenticatedUser("Kullanıcı yalnız kendisine gelen daveti reddeder")]
public sealed record DeclineInvitationCommand(Guid InvitationId) : ICommand;

public sealed class DeclineInvitationValidator : AbstractValidator<DeclineInvitationCommand>
{
    public DeclineInvitationValidator() => RuleFor(x => x.InvitationId).NotEmpty();
}

public sealed class DeclineInvitationHandler(
    ICurrentUser currentUser,
    IMembershipRepository memberships,
    ITenantRepository tenants,
    ITenantContextSetter tenantSetter,
    IIdentityUnitOfWork unitOfWork) : ICommandHandler<DeclineInvitationCommand>
{
    public async Task<Result> Handle(DeclineInvitationCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var invitation = await memberships.GetPendingInvitationAsync(command.InvitationId, userId, cancellationToken).ConfigureAwait(false);
        var tenant = invitation is null ? null : await tenants.GetByIdAsync(invitation.TenantId, cancellationToken).ConfigureAwait(false);
        if (invitation is null || tenant is null)
        {
            return Error.NotFound(IdentityErrors.InvitationNotFound);
        }

        using (tenantSetter.BeginScope(tenant.Id, tenant.Slug))
        {
            memberships.Remove(invitation);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
