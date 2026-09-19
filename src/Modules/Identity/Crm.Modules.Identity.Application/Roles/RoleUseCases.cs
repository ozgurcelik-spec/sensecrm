using Crm.Modules.Identity.Contracts;
using Crm.Modules.Identity.Domain;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Identity.Application.Roles;

[RequiresPermission(OrgPermissions.UsersRead)]
public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>;

public sealed class ListRolesHandler(IIdentityReadStore readStore) : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public async Task<Result<IReadOnlyList<RoleDto>>> Handle(ListRolesQuery query, CancellationToken cancellationToken) =>
        Result.Success(await readStore.ListRolesAsync(cancellationToken).ConfigureAwait(false));
}

internal static class RoleRules
{
    public static void Apply<T>(AbstractValidator<T> validator, Func<T, string?> name, Func<T, IReadOnlyList<string>?> permissions, IPermissionCatalog catalog)
    {
        validator.RuleFor(x => name(x)).NotEmpty().MaximumLength(IdentityLimits.RoleNameMaxLength).OverridePropertyName("Name");
        validator.RuleFor(x => permissions(x)).NotNull().OverridePropertyName("Permissions");
        validator.RuleForEach(x => permissions(x) ?? Array.Empty<string>())
            .Must(catalog.Exists).WithMessage(IdentityErrors.RoleUnknownPermission)
            .OverridePropertyName("Permissions");
    }
}

[RequiresPermission(OrgPermissions.RolesManage)]
public sealed record CreateRoleCommand(string Name, IReadOnlyList<string> Permissions) : ICommand<RoleDto>;

public sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator(IPermissionCatalog catalog) => RoleRules.Apply(this, x => x.Name, x => x.Permissions, catalog);
}

public sealed class CreateRoleHandler(ITenantContext tenant, IRoleRepository roles) : ICommandHandler<CreateRoleCommand, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        if (await roles.NameExistsAsync(command.Name.Trim(), excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.RoleNameTaken);
        }

        var role = Role.CreateCustom(tenant.TenantId, command.Name, command.Permissions);
        roles.Add(role);
        return new RoleDto(role.Id, role.Name, role.IsSystem, role.Permissions, 0);
    }
}

[RequiresPermission(OrgPermissions.RolesManage)]
public sealed record UpdateRoleCommand(Guid Id, string Name, IReadOnlyList<string> Permissions) : ICommand;

public sealed class UpdateRoleValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleValidator(IPermissionCatalog catalog) => RoleRules.Apply(this, x => x.Name, x => x.Permissions, catalog);
}

public sealed class UpdateRoleHandler(ITenantContext tenant, IRoleRepository roles, IPermissionCacheInvalidator permissionCache) : ICommandHandler<UpdateRoleCommand>
{
    public async Task<Result> Handle(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (role.IsSystem)
        {
            return Error.Rule(IdentityErrors.RoleSystemReadOnly);
        }

        if (await roles.NameExistsAsync(command.Name.Trim(), role.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IdentityErrors.RoleNameTaken);
        }

        var result = role.Update(command.Name, command.Permissions);
        if (result.IsSuccess)
        {
            await permissionCache.InvalidateTenantAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

[RequiresPermission(OrgPermissions.RolesManage)]
public sealed record DeleteRoleCommand(Guid Id) : ICommand;

public sealed class DeleteRoleHandler(IRoleRepository roles, IMembershipRepository memberships) : ICommandHandler<DeleteRoleCommand>
{
    public async Task<Result> Handle(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var deletable = role.EnsureDeletable(await memberships.CountWithRoleAsync(role.Id, cancellationToken).ConfigureAwait(false));
        if (deletable.IsFailure)
        {
            return deletable;
        }

        roles.Remove(role);
        return Result.Success();
    }
}
