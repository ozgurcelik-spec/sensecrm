using FluentValidation;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Identity.Domain;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Identity.Application.Organization;

/// <summary>Aktif organizasyonun bilgileri; organizasyonun her aktif üyesi okuyabilir.</summary>
[AnyAuthenticatedUser("Her aktif üye kendi organizasyonunun temel bilgisini görür (kiracı filtresi)")]
public sealed record GetOrganizationQuery : IQuery<OrganizationDto>;

public sealed class GetOrganizationHandler(ICurrentUser currentUser, ITenantContext tenant, ITenantRepository tenants, IMembershipRepository memberships)
    : IQueryHandler<GetOrganizationQuery, OrganizationDto>
{
    public async Task<Result<OrganizationDto>> Handle(GetOrganizationQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || !tenant.IsResolved)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var membership = await memberships.GetByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (membership is not { IsActive: true })
        {
            return Error.Forbidden(ErrorCodes.Forbidden);
        }

        var organization = await tenants.GetByIdAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        return organization is null
            ? Error.NotFound(ErrorCodes.NotFound)
            : new OrganizationDto(organization.Id, organization.Name, organization.Slug, organization.DefaultLocale, organization.TimeZone);
    }
}

[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record UpdateOrganizationCommand(string Name, string DefaultLocale, string TimeZone) : ICommand;

public sealed class UpdateOrganizationValidator : AbstractValidator<UpdateOrganizationCommand>
{
    public UpdateOrganizationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(IdentityLimits.OrganizationNameMaxLength);
        RuleFor(x => x.DefaultLocale).SupportedLocale();
        RuleFor(x => x.TimeZone).ValidTimeZone();
    }
}

public sealed class UpdateOrganizationHandler(ITenantContext tenant, ITenantRepository tenants, IIntegrationEventOutbox outbox, TimeProvider clock) : ICommandHandler<UpdateOrganizationCommand>
{
    public async Task<Result> Handle(UpdateOrganizationCommand command, CancellationToken cancellationToken)
    {
        var organization = await tenants.GetByIdAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        if (organization is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        organization.Update(command.Name, command.DefaultLocale, command.TimeZone);

        // M7: ilk başarılı kayıtta profile_completed_at dolar (onboarding); ad değişimi Platform konsolundaki okuma kopyasına olayla taşınır.
        organization.MarkProfileCompleted(clock.GetUtcNow().UtcDateTime);
        outbox.Enqueue(new OrganizationUpdated(organization.Id, organization.Name));
        return Result.Success();
    }
}

/// <summary>Birleşik izin kataloğu (tüm modüller): anahtar + grup (org | crm). Görünen adlar istemcide çevrilir (K8).</summary>
[AnyAuthenticatedUser("İzin kataloğu (anahtar listesi) hassas değildir; istemci izin ekranı ve rol editörü için")]
public sealed record ListPermissionsQuery : IQuery<IReadOnlyList<PermissionDto>>;

public sealed class ListPermissionsHandler(IPermissionCatalog catalog) : IQueryHandler<ListPermissionsQuery, IReadOnlyList<PermissionDto>>
{
    public Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListPermissionsQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<PermissionDto> result = catalog.All.Select(p => new PermissionDto(p.Key, p.Group)).ToList();
        return Task.FromResult(Result.Success(result));
    }
}
