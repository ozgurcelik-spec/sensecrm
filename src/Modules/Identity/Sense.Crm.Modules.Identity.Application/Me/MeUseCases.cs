using FluentValidation;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Identity.Application.Me;

/// <summary>Oturum açmış kullanıcı: profil, aktif organizasyon, rol, izinler ve üyesi olduğu organizasyonlar.</summary>
[AnyAuthenticatedUser("Kullanıcı yalnız kendi profilini okur")]
[TenantStatusExempt("Web engel ekranı/banner için askıdaki ve silme bekleyen kiracıda da çalışır")]
public sealed record GetMeQuery : IQuery<MeDto>;

public sealed class GetMeHandler(
    ICurrentUser currentUser,
    ITenantContext tenant,
    IUserRepository users,
    ITenantRepository tenants,
    IMembershipRepository memberships,
    IRoleRepository roles,
    IIdentityReadStore readStore,
    ITenantEntitlements entitlements,
    TimeProvider clock) : IQueryHandler<GetMeQuery, MeDto>
{
    public async Task<Result<MeDto>> Handle(GetMeQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !tenant.IsResolved)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        var organization = await tenants.GetByIdAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        if (user is null || organization is null)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var membership = await memberships.GetByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var role = membership is { IsActive: true } ? await roles.GetByIdAsync(membership.RoleId, cancellationToken).ConfigureAwait(false) : null;
        if (role is null)
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        var organizations = await readStore.ListOrganizationsOfUserAsync(userId, cancellationToken).ConfigureAwait(false);

        // M7: abonelik özeti (plan, etkin durum, erişim, deneme, modül bayrakları); etkin durum her çağrıda saatten hesaplanır.
        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var (status, access) = snapshot.Evaluate(now);
        var subscription = new MeSubscriptionDto(
            snapshot.PlanCode,
            snapshot.PlanName,
            status,
            access,
            snapshot.TrialEndsOn,
            snapshot.TrialDaysLeft(status, now),
            GatedModules.All.ToDictionary(m => m, m => snapshot.IsModuleEnabled(m), StringComparer.Ordinal));

        return new MeDto(
            new MeUserDto(user.Id, user.Email, user.DisplayName, user.Locale, user.IsPlatformAdmin, user.MustChangePassword),
            new OrganizationDto(organization.Id, organization.Name, organization.Slug, organization.DefaultLocale, organization.TimeZone),
            new RoleRefDto(role.Id, role.Name),
            role.Permissions,
            organizations,
            subscription);
    }
}

/// <summary>Profil güncelleme: görünen ad ve dil tercihi (tr | en). Verilmeyen alan değişmez.</summary>
[AnyAuthenticatedUser("Kullanıcı yalnız kendi profilini günceller")]
[TenantStatusExempt("Kullanıcı-düzeyi profil (kiracı verisi değil)")]
public sealed record UpdateMeCommand(string? DisplayName, string? Locale) : ICommand;

public sealed class UpdateMeValidator : AbstractValidator<UpdateMeCommand>
{
    public UpdateMeValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(IdentityLimits.DisplayNameMaxLength).When(x => x.DisplayName is not null);
        RuleFor(x => x.Locale).SupportedLocale().When(x => x.Locale is not null);
    }
}

public sealed class UpdateMeHandler(ICurrentUser currentUser, IUserRepository users) : ICommandHandler<UpdateMeCommand>
{
    public async Task<Result> Handle(UpdateMeCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        user.UpdateProfile(command.DisplayName, command.Locale);
        return Result.Success();
    }
}
