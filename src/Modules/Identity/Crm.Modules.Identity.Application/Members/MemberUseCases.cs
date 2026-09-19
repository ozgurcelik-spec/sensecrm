using Crm.Modules.Identity.Contracts;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Domain.Memberships;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Users;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;
using FluentValidation.Results;

namespace Crm.Modules.Identity.Application.Members;

[RequiresPermission(OrgPermissions.UsersRead)]
public sealed record ListMembersQuery : IQuery<IReadOnlyList<MemberDto>>;

public sealed class ListMembersHandler(IIdentityReadStore readStore) : IQueryHandler<ListMembersQuery, IReadOnlyList<MemberDto>>
{
    public async Task<Result<IReadOnlyList<MemberDto>>> Handle(ListMembersQuery query, CancellationToken cancellationToken) =>
        Result.Success(await readStore.ListMembersAsync(cancellationToken).ConfigureAwait(false));
}

/// <summary>
/// Yetki devri koruması (M7): çağıran kendinde olmayan bir izin/rolü başkasına veremez. İzin kümesi çağıranın etkin izinlerinin alt
/// kümesi olmalıdır; <c>Administrator</c> sistem rolündeki çağıran istisnadır (tüm izinlere sahiptir).
/// </summary>
public sealed class DelegationGuard(ICurrentUser user, IMembershipRepository memberships, IRoleRepository roles, IPermissionService permissions)
{
    public async Task<bool> CanGrantAsync(IReadOnlyCollection<string> requested, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } callerId)
        {
            return false;
        }

        if (requested.Count == 0)
        {
            return true;
        }

        var held = await permissions.GetPermissionsAsync(callerId, cancellationToken).ConfigureAwait(false);
        if (requested.All(held.Contains))
        {
            return true;
        }

        var membership = await memberships.GetByUserAsync(callerId, cancellationToken).ConfigureAwait(false);
        var administrator = await roles.GetByCodeAsync(SystemRoleCodes.Administrator, cancellationToken).ConfigureAwait(false);
        return membership is { IsActive: true } && administrator is not null && membership.RoleId == administrator.Id;
    }
}

/// <summary>
/// Organizasyona üye ekleme (H4). Gövde <c>{ email, displayName, roleId }</c>; yönetici parola SEÇMEZ.
/// <list type="bullet">
/// <item>E-posta yeni ise hesap sunucu üretimi tek seferlik geçici parolayla açılır (yanıtta bir kez döner), <c>MustChangePassword</c> = true,
/// üyelik hemen aktiftir.</item>
/// <item>E-posta mevcut bir hesapsa üyelik <b>bekleyen davet</b> olarak açılır (otomatik katılım yok); hesap sahibi
/// <c>GET /me/invitations</c> ile görür, kabul/red eder. Yanıt başka organizasyona ait hesap verisi (ad, kimlik) sızdırmaz.</item>
/// </list>
/// Verilen rolün izinleri çağıranın izinlerinin alt kümesi olmalıdır (M7). Hesap zaten üyeyse (aktif veya bekleyen) <c>member.exists</c>.
/// </summary>
[RequiresPermission(OrgPermissions.UsersManage)]
public sealed record AddMemberCommand(string Email, string? DisplayName, Guid RoleId) : ICommand<AddMemberResultDto>;

public sealed class AddMemberValidator : AbstractValidator<AddMemberCommand>
{
    public AddMemberValidator()
    {
        RuleFor(x => x.Email).Email();
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.DisplayName).MaximumLength(IdentityLimits.DisplayNameMaxLength);
    }
}

public sealed class AddMemberHandler(
    ITenantContext tenant,
    ITenantRepository tenants,
    IUserRepository users,
    IRoleRepository roles,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    ISecretGenerator secrets,
    DelegationGuard delegation,
    IPermissionCacheInvalidator permissionCache,
    TimeProvider clock) : ICommandHandler<AddMemberCommand, AddMemberResultDto>
{
    public async Task<Result<AddMemberResultDto>> Handle(AddMemberCommand command, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(command.RoleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!await delegation.CanGrantAsync(role.Permissions, cancellationToken).ConfigureAwait(false))
        {
            return Error.Forbidden(IdentityErrors.RolePermissionEscalation);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await users.GetByEmailAsync(command.Email, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (await memberships.GetByUserAsync(existing.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return Error.Conflict(IdentityErrors.MemberExists);
            }

            // Otomatik katılım yok: hesap sahibi kabul edene kadar bekleyen davet (hiçbir yetki vermez).
            memberships.Add(Membership.Invite(tenant.TenantId, existing.Id, role.Id, now));
            await permissionCache.InvalidateUserAsync(tenant.TenantId, existing.Id, cancellationToken).ConfigureAwait(false);
            return new AddMemberResultDto(UserId: null, command.Email.Trim(), role.Id, role.Name, MemberStatuses.Pending, TemporaryPassword: null);
        }

        EnsureDisplayName(command);
        var organization = await tenants.GetByIdAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var temporaryPassword = secrets.NewPassword();
        var user = User.Create(
            command.Email,
            command.DisplayName!,
            organization?.DefaultLocale ?? Shared.Contracts.Configuration.Cultures.TurkishLanguage,
            hasher.Hash(temporaryPassword),
            mustChangePassword: true);
        users.Add(user);
        memberships.Add(Membership.Create(tenant.TenantId, user.Id, role.Id, now));
        return new AddMemberResultDto(user.Id, user.Email, role.Id, role.Name, MemberStatuses.Active, temporaryPassword);
    }

    /// <summary>Yeni hesap için ad zorunlu; aynı "validation" sözleşmesiyle (errors) döner.</summary>
    private static void EnsureDisplayName(AddMemberCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            throw new ValidationException([new ValidationFailure(nameof(command.DisplayName), IdentityErrors.Required)]);
        }
    }
}

/// <summary>
/// Üyeliğin rolünü ve/veya aktifliğini değiştirir; organizasyonda en az bir aktif Administrator kalmalıdır. Kullanıcı kendi rolünü
/// değiştiremez / kendini pasifleştiremez; kendinde olmayan izinleri içeren bir rol atayamaz/etkinleştiremez (M7).
/// </summary>
[RequiresPermission(OrgPermissions.UsersManage)]
public sealed record UpdateMemberCommand(Guid UserId, Guid? RoleId, bool? IsActive) : ICommand;

public sealed class UpdateMemberValidator : AbstractValidator<UpdateMemberCommand>
{
    public UpdateMemberValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RoleId).NotEqual(Guid.Empty).When(x => x.RoleId is not null);
    }
}

public sealed class UpdateMemberHandler(
    ITenantContext tenant,
    ICurrentUser currentUser,
    IRoleRepository roles,
    IMembershipRepository memberships,
    DelegationGuard delegation,
    IPermissionCacheInvalidator permissionCache) : ICommandHandler<UpdateMemberCommand>
{
    public async Task<Result> Handle(UpdateMemberCommand command, CancellationToken cancellationToken)
    {
        // Kiracı filtresi: başka organizasyonun üyesi burada hiç bulunmaz → not_found (varlık sızdırılmaz).
        var membership = await memberships.GetByUserAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (membership is null || membership.IsPending)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var changesRole = command.RoleId is { } requested && requested != membership.RoleId;
        var deactivates = command.IsActive == false && membership.IsActive;
        if (currentUser.UserId == membership.UserId && (changesRole || deactivates))
        {
            return Error.Rule(IdentityErrors.MemberCannotModifySelf);
        }

        var targetRoleId = command.RoleId ?? membership.RoleId;
        var targetRole = await roles.GetByIdAsync(targetRoleId, cancellationToken).ConfigureAwait(false);
        if (targetRole is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var targetActive = command.IsActive ?? membership.IsActive;
        var grantsAccess = changesRole || (targetActive && !membership.IsActive);
        if (grantsAccess && !await delegation.CanGrantAsync(targetRole.Permissions, cancellationToken).ConfigureAwait(false))
        {
            return Error.Forbidden(IdentityErrors.RolePermissionEscalation);
        }

        var administrator = await roles.GetByCodeAsync(SystemRoleCodes.Administrator, cancellationToken).ConfigureAwait(false);
        var wasActiveAdmin = membership.IsActive && membership.RoleId == administrator?.Id;
        var staysActiveAdmin = targetActive && targetRoleId == administrator?.Id;
        if (wasActiveAdmin && !staysActiveAdmin
            && await memberships.CountActiveWithRoleAsync(administrator!.Id, cancellationToken).ConfigureAwait(false) <= 1)
        {
            return Error.Rule(IdentityErrors.MemberLastAdmin);
        }

        membership.ChangeRole(targetRoleId);
        membership.SetActive(targetActive);
        await permissionCache.InvalidateUserAsync(tenant.TenantId, membership.UserId, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
