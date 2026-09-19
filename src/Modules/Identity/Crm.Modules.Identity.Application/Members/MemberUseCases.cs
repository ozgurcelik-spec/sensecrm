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
using Microsoft.Extensions.Options;

namespace Crm.Modules.Identity.Application.Members;

[RequiresPermission(OrgPermissions.UsersRead)]
public sealed record ListMembersQuery : IQuery<IReadOnlyList<MemberDto>>;

public sealed class ListMembersHandler(IIdentityReadStore readStore) : IQueryHandler<ListMembersQuery, IReadOnlyList<MemberDto>>
{
    public async Task<Result<IReadOnlyList<MemberDto>>> Handle(ListMembersQuery query, CancellationToken cancellationToken) =>
        Result.Success(await readStore.ListMembersAsync(cancellationToken).ConfigureAwait(false));
}

/// <summary>
/// Organizasyona üye ekleme. E-postanın hesabı varsa yalnız üyelik eklenir (parola/ad yok sayılır); yoksa hesap açılır
/// (e-posta ile davet sonraki aşamada). Hesap zaten üyeyse <c>member.exists</c>.
/// </summary>
[RequiresPermission(OrgPermissions.UsersManage)]
public sealed record AddMemberCommand(string Email, string? DisplayName, string? Password, Guid RoleId) : ICommand<Guid>;

public sealed class AddMemberValidator : AbstractValidator<AddMemberCommand>
{
    public AddMemberValidator()
    {
        RuleFor(x => x.Email).Email();
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.DisplayName).MaximumLength(IdentityLimits.DisplayNameMaxLength);
        RuleFor(x => x.Password).MaximumLength(IdentityLimits.PasswordMaxLength);
    }
}

public sealed class AddMemberHandler(
    ITenantContext tenant,
    ITenantRepository tenants,
    IUserRepository users,
    IRoleRepository roles,
    IMembershipRepository memberships,
    IPasswordHasher hasher,
    IOptions<IdentityOptions> options,
    TimeProvider clock) : ICommandHandler<AddMemberCommand, Guid>
{
    public async Task<Result<Guid>> Handle(AddMemberCommand command, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(command.RoleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var user = await users.GetByEmailAsync(command.Email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            ValidateNewAccount(command, options.Value.MinPasswordLength);
            var organization = await tenants.GetByIdAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
            user = User.Create(command.Email, command.DisplayName!, organization?.DefaultLocale ?? Shared.Contracts.Configuration.Cultures.TurkishLanguage, hasher.Hash(command.Password!));
            users.Add(user);
        }
        else if (await memberships.GetByUserAsync(user.Id, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error.Conflict(IdentityErrors.MemberExists);
        }

        memberships.Add(Membership.Create(tenant.TenantId, user.Id, role.Id, clock.GetUtcNow().UtcDateTime));
        return user.Id;
    }

    /// <summary>Yeni hesap için ad ve parola zorunlu; aynı "validation" sözleşmesiyle (errors) döner.</summary>
    private static void ValidateNewAccount(AddMemberCommand command, int minPasswordLength)
    {
        var failures = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            failures.Add(new ValidationFailure(nameof(command.DisplayName), IdentityErrors.Required));
        }

        if (string.IsNullOrEmpty(command.Password))
        {
            failures.Add(new ValidationFailure(nameof(command.Password), IdentityErrors.Required));
        }
        else if (command.Password.Length < minPasswordLength)
        {
            failures.Add(new ValidationFailure(nameof(command.Password), IdentityErrors.PasswordTooShort)
            {
                FormattedMessagePlaceholderValues = new Dictionary<string, object> { ["MinLength"] = minPasswordLength },
            });
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }
}

/// <summary>Üyeliğin rolünü ve/veya aktifliğini değiştirir; organizasyonda en az bir aktif Administrator kalmalıdır.</summary>
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
    IRoleRepository roles,
    IMembershipRepository memberships,
    IPermissionCacheInvalidator permissionCache) : ICommandHandler<UpdateMemberCommand>
{
    public async Task<Result> Handle(UpdateMemberCommand command, CancellationToken cancellationToken)
    {
        // Kiracı filtresi: başka organizasyonun üyesi burada hiç bulunmaz → not_found (varlık sızdırılmaz).
        var membership = await memberships.GetByUserAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var targetRoleId = command.RoleId ?? membership.RoleId;
        if (command.RoleId is { } roleId && await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var targetActive = command.IsActive ?? membership.IsActive;
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

/// <summary>Eklenen üyenin satırı (yalnız POST /organization/members yanıtı için; aynı yetkiyle).</summary>
[RequiresPermission(OrgPermissions.UsersManage)]
public sealed record GetMemberQuery(Guid UserId) : IQuery<MemberDto>;

public sealed class GetMemberHandler(IIdentityReadStore readStore) : IQueryHandler<GetMemberQuery, MemberDto>
{
    public async Task<Result<MemberDto>> Handle(GetMemberQuery query, CancellationToken cancellationToken) =>
        await readStore.GetMemberAsync(query.UserId, cancellationToken).ConfigureAwait(false) is { } member
            ? member
            : Error.NotFound(ErrorCodes.NotFound);
}
