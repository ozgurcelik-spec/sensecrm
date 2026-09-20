using FluentValidation;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Platform.Application.Console;

// ---------------------------------------------------------------------------------------------------------------------
// C-SEC2: step-up yeniden kimlik doğrulama, kiracı koruma kuralı ve platform yöneticisi yaşam döngüsü.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <b>Step-up</b> (H2): yıkıcı platform komutları (silme talebi, <c>blocked</c> askı, imha yeniden denemesi, platform yöneticisi geri alma) çağıran platform yöneticisinin
/// <b>kendi parolasının</b> sunucuda doğrulanmasını ister; çalınmış bir access token tek başına yetmez. Hatalar 401 DEĞİL (oturum düşmesin): eksik/yanlış parola 422,
/// deneme sınırı 429. Ayrıntı: docs/security/hardening-report.md (C-SEC2 H2 ve kabul edilen risk: MFA/ikinci onaylayıcı yok).
/// </summary>
public interface IStepUpGuard
{
    Task<Result> RequireAsync(string? currentPassword, CancellationToken ct);
}

public sealed class StepUpGuard(ICurrentUser user, IStepUpAuthenticator authenticator) : IStepUpGuard
{
    public async Task<Result> RequireAsync(string? currentPassword, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        return await authenticator.VerifyAsync(userId, currentPassword, user.IpAddress, ct).ConfigureAwait(false) switch
        {
            StepUpOutcome.Verified => Result.Success(),
            StepUpOutcome.PasswordRequired => Error.Rule(PlatformErrors.StepUpRequired),
            StepUpOutcome.RateLimited => new Error(PlatformErrors.StepUpRateLimited, ErrorType.TooManyRequests),
            _ => Error.Rule(PlatformErrors.StepUpFailed),
        };
    }
}

/// <summary>
/// Kiracı koruma kuralı (H1): sistem (işletim) kiracısı <b>ya da aktif bir platform yöneticisi üyesi olan</b> kiracı asla askıya alınamaz, engellenemez, silme sürecine
/// alınamaz ya da imha edilemez. <c>is_system</c> bayrağı eski kurulumlarda eksik olabileceğinden kural ayrıca üyelik dizininden dinamik sorgulanır.
/// </summary>
public static class TenantProtection
{
    public static async Task<bool> IsProtectedAsync(TenantAccount account, IPlatformAdminDirectory admins, CancellationToken ct) =>
        account.IsSystem || await admins.HasActivePlatformAdminAsync(account.TenantId, ct).ConfigureAwait(false);

    /// <summary>Yazılan kiracı adı (kırpılmış, harf duyarlı) sunucudaki adla eşleşiyor mu (yıkıcı komutlarda zorunlu onay).</summary>
    public static bool NameMatches(string? typed, string actual) =>
        !string.IsNullOrWhiteSpace(typed) && string.Equals(typed.Trim(), actual.Trim(), StringComparison.Ordinal);
}

// ---------------------------------------------------------------------------------------------------------------------
// Başarısız imhayı yeniden dene (L3).
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>POST …/deletion-request/retry</c>: yalnız <c>failed</c> talep; <c>currentPassword*</c> (step-up). Deneme sayacı sıfırlanır, Worker sonraki turda sürdürür.</summary>
[PlatformAdminOnly]
public sealed record RetryDeletionCommand(Guid TenantId, string? CurrentPassword) : ICommand;

public sealed class RetryDeletionValidator : AbstractValidator<RetryDeletionCommand>
{
    public RetryDeletionValidator() => RuleFor(x => x.TenantId).NotEmpty();
}

public sealed class RetryDeletionHandler(
    ITenantAccountRepository accounts,
    IDeletionRequestRepository requests,
    IStepUpGuard stepUp,
    IPlatformAudit audit) : ICommandHandler<RetryDeletionCommand>
{
    public async Task<Result> Handle(RetryDeletionCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var request = await requests.GetActiveAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        if (request is null || request.Status != DeletionStatuses.Failed)
        {
            return Error.Conflict(PlatformErrors.DeletionNotRetryable);
        }

        var verified = await stepUp.RequireAsync(command.CurrentPassword, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            return verified;
        }

        var retried = request.Retry();
        if (retried.IsFailure)
        {
            return retried;
        }

        audit.Record(
            PlatformAuditActions.DeletionRetried,
            account,
            null,
            new Dictionary<string, object?> { ["requestId"] = request.Id, ["lastError"] = request.LastError });
        return Result.Success();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Platform yöneticileri (M6): listele, yetkiyi geri al (step-up).
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>GET /platform/admins</c>: bayrağı olan hesaplar (pasif dahil), e-postaya göre sıralı.</summary>
[PlatformAdminOnly]
public sealed record ListPlatformAdminsQuery : IQuery<IReadOnlyList<PlatformAdminDto>>;

public sealed class ListPlatformAdminsHandler(IPlatformAdminDirectory admins) : IQueryHandler<ListPlatformAdminsQuery, IReadOnlyList<PlatformAdminDto>>
{
    public async Task<Result<IReadOnlyList<PlatformAdminDto>>> Handle(ListPlatformAdminsQuery query, CancellationToken cancellationToken)
    {
        var list = await admins.ListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<PlatformAdminDto>>(list.Select(a => new PlatformAdminDto(a.UserId, a.Email, a.DisplayName, a.IsActive, a.LastLoginAt)).ToList());
    }
}

/// <summary>
/// <c>POST /platform/admins/{userId}/revoke</c>: platform yöneticisi yetkisini geri alır (isteğe bağlı hesabı da pasifleştirir), tüm oturumlarını kapatır.
/// <b>Son aktif platform yöneticisi</b> geri alınamaz (409). <c>currentPassword*</c> (step-up).
/// </summary>
[PlatformAdminOnly]
public sealed record RevokePlatformAdminCommand(Guid UserId, string? CurrentPassword, bool Deactivate) : ICommand;

public sealed class RevokePlatformAdminValidator : AbstractValidator<RevokePlatformAdminCommand>
{
    public RevokePlatformAdminValidator() => RuleFor(x => x.UserId).NotEmpty();
}

public sealed class RevokePlatformAdminHandler(IStepUpGuard stepUp, IPlatformAdminManager manager, IPlatformAudit audit) : ICommandHandler<RevokePlatformAdminCommand>
{
    public async Task<Result> Handle(RevokePlatformAdminCommand command, CancellationToken cancellationToken)
    {
        var verified = await stepUp.RequireAsync(command.CurrentPassword, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            return verified;
        }

        var outcome = await manager.RevokeAsync(command.UserId, command.Deactivate, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case PlatformAdminRevocation.NotFound:
                return Error.NotFound(ErrorCodes.NotFound);
            case PlatformAdminRevocation.NotAPlatformAdmin:
                return Error.Conflict(PlatformErrors.NotAPlatformAdmin);
            case PlatformAdminRevocation.LastActiveAdmin:
                return Error.Conflict(PlatformErrors.LastPlatformAdmin);
            default:
                // Denetim: hedef hesap kimliği (kişisel veri değil); kimlik değişikliği zaten kendi kaydında (platform_audit_entries yalnız kimlik + eylem).
                audit.Record(
                    PlatformAuditActions.PlatformAdminRevoked,
                    null,
                    null,
                    new Dictionary<string, object?> { ["userId"] = command.UserId, ["deactivated"] = command.Deactivate });
                return Result.Success();
        }
    }
}
