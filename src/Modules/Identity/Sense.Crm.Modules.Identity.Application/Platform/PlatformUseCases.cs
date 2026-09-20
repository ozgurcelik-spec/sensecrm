using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Application.Provisioning;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Modules.Identity.Domain.Memberships;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Identity.Application.Platform;

// ---------------------------------------------------------------------------------------------------------------------
// Platform yöneticisi: herkese açık kayıt kapalıyken (Production varsayılanı) organizasyon açma yolu.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Yeni organizasyon + ilk yöneticisi. <see cref="AdminPassword"/> null ise tek seferlik parola üretilir ve yanıtta bir kez döner.
/// Yönetici e-postası mevcut bir hesapsa hesap değişmez (parola/ad yok sayılır) ve hesap sahibinin onayı olmadan yönetici YAPILMAZ:
/// yeni organizasyona <b>bekleyen</b> Administrator daveti eklenir (H4-f; hesap sahibi <c>/me/invitations</c> ile kabul eder). Yeni hesap
/// (parola üretilmiş ya da platform yöneticisince verilmiş) geçici paroladır: <c>MustChangePassword</c> = true.
/// </summary>
[PlatformAdminOnly]
public sealed record CreateOrganizationCommand(
    string OrganizationName,
    string AdminDisplayName,
    string AdminEmail,
    string? AdminPassword,
    string Locale,
    string? PlanCode = null,
    DateOnly? TrialEndsOn = null) : ICommand<CreatedOrganizationDto>;

/// <param name="AdminAccountCreated">false: e-posta zaten bir hesaba aitti; yalnız bekleyen Administrator daveti eklendi.</param>
/// <param name="AdminInvitationPending">true: yönetici mevcut bir hesap; organizasyonda hesap sahibi kabul edene kadar aktif yönetici yoktur.</param>
/// <param name="GeneratedPassword">Yalnız parola verilmeden yeni hesap açıldıysa; bir daha okunamaz.</param>
public sealed record CreatedOrganizationDto(
    Guid OrganizationId,
    string Name,
    string Slug,
    Guid AdminUserId,
    string AdminEmail,
    bool AdminAccountCreated,
    string? GeneratedPassword,
    bool AdminInvitationPending = false,
    string? PlanCode = null);

public sealed class CreateOrganizationValidator : AbstractValidator<CreateOrganizationCommand>
{
    public CreateOrganizationValidator(IOptions<IdentityOptions> options)
    {
        RuleFor(x => x.OrganizationName).NotEmpty().MaximumLength(IdentityLimits.OrganizationNameMaxLength);
        RuleFor(x => x.AdminDisplayName).NotEmpty().MaximumLength(IdentityLimits.DisplayNameMaxLength);
        RuleFor(x => x.AdminEmail).Email();
        When(x => x.AdminPassword is not null, () => RuleFor(x => x.AdminPassword).MeetsPasswordPolicy(options.Value.MinPasswordLength, x => x.AdminEmail));
        RuleFor(x => x.Locale).SupportedLocale();
    }
}

public sealed class CreateOrganizationHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    ISecretGenerator secrets,
    OrganizationProvisioner provisioner,
    ITenantContextSetter tenantSetter,
    IIdentityUnitOfWork unitOfWork,
    IPlanCatalog planCatalog,
    IPlatformAuditSink platformAudit,
    ILogger<CreateOrganizationHandler> logger,
    TimeProvider clock) : ICommandHandler<CreateOrganizationCommand, CreatedOrganizationDto>
{
    public async Task<Result<CreatedOrganizationDto>> Handle(CreateOrganizationCommand command, CancellationToken cancellationToken)
    {
        // JWT'deki bayrağa güvenilmez: yetki her istekte veritabanından doğrulanır (bayrak geri alınmış/hesap kapatılmış olabilir).
        if (currentUser.UserId is not { } actorId || !currentUser.IsPlatformAdmin)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        var actor = await users.GetByIdAsync(actorId, cancellationToken).ConfigureAwait(false);
        if (actor is null || !actor.IsActive || !actor.IsPlatformAdmin)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var admin = await users.GetByEmailAsync(command.AdminEmail, cancellationToken).ConfigureAwait(false);
        if (admin is { IsActive: false })
        {
            return Error.Conflict(IdentityErrors.UserDisabled);
        }

        // M7: plan (varsa) Platform kataloğuna karşı eşzamanlı doğrulanır (bilinmeyen/pasif plan → 400 errors.planCode); atama Platform hesabı olayıyla yapılır.
        var planCode = string.IsNullOrWhiteSpace(command.PlanCode) ? null : command.PlanCode.Trim();
        if (planCode is not null && !await planCatalog.IsAssignableAsync(planCode, cancellationToken).ConfigureAwait(false))
        {
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(command.PlanCode), IdentityErrors.InvalidPlan)]);
        }

        var provisioned = await provisioner.CreateAsync(command.OrganizationName, command.Locale, cancellationToken, OrganizationOrigin.Platform, planCode, command.TrialEndsOn, actorId).ConfigureAwait(false);

        string? generatedPassword = null;
        var accountCreated = admin is null;
        if (admin is null)
        {
            generatedPassword = command.AdminPassword is null ? secrets.NewPassword() : null;
            // Parolayı kullanıcı kendisi seçmedi (üretilmiş ya da platform yöneticisince verilmiş): ilk girişte değiştirilmek zorundadır.
            admin = User.Create(command.AdminEmail, command.AdminDisplayName, command.Locale, hasher.Hash(command.AdminPassword ?? generatedPassword!), mustChangePassword: true);
            admin.SetDefaultTenant(provisioned.Tenant.Id);
            users.Add(admin);
        }

        // İstek, platform yöneticisinin kendi organizasyonunun kiracı bağlamında çalışır; yeni organizasyonun verisi (roller, üyelik,
        // denetim) başka kiracıya yazılamaz (AuditTenantInterceptor). Bu yüzden yazma, yeni organizasyonun kapsamında ve burada
        // (handler içinde) kalıcılaştırılır; UnitOfWorkBehaviour'ın sonraki SaveChanges'i işlem yapmaz.
        using (tenantSetter.BeginScope(provisioned.Tenant.Id, provisioned.Tenant.Slug))
        {
            // Mevcut hesap onayı olmadan yönetici yapılmaz: bekleyen davet; yeni hesap doğrudan aktif Administrator.
            memberships.Add(accountCreated
                ? Membership.Create(provisioned.Tenant.Id, admin.Id, provisioned.Administrator.Id, now)
                : Membership.Invite(provisioned.Tenant.Id, admin.Id, provisioned.Administrator.Id, now));
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // M7: platform denetimi (organization.created) ve yanıttaki plan (istekteki ya da Platform varsayılanı); Platform hesabı olayla açılır.
        var effectivePlan = planCode ?? planCatalog.ProvisioningPlanCode;
        try
        {
            await platformAudit.RecordAsync(
                "organization.created",
                provisioned.Tenant.Id,
                provisioned.Tenant.Name,
                new Dictionary<string, object?> { ["planCode"] = effectivePlan, ["trialEndsOn"] = command.TrialEndsOn?.ToString("yyyy-MM-dd"), ["adminAccountCreated"] = accountCreated },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // C-SEC2 L4: organizasyon zaten kalıcı; denetim satırı yazılamadıysa istek başarısız sayılmaz — OrganizationCreated olayı (aynı Identity işleminde outbox'a yazıldı, ActorUserId taşır)
            // Platform işleyicisinde satırı tamamlar. Hata günlüğe gider.
            logger.LogError(ex, "The organization.created platform audit row could not be written directly; it will be reconciled from the OrganizationCreated event");
        }

        return new CreatedOrganizationDto(
            provisioned.Tenant.Id,
            provisioned.Tenant.Name,
            provisioned.Tenant.Slug,
            admin.Id,
            admin.Email,
            accountCreated,
            generatedPassword,
            AdminInvitationPending: !accountCreated,
            PlanCode: effectivePlan);
    }
}
