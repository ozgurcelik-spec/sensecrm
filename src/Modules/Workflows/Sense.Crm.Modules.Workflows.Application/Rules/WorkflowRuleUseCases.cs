using System.Text.Json;
using FluentValidation;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Workflows.Contracts;
using Sense.Crm.Modules.Workflows.Domain;
using Sense.Crm.Modules.Workflows.Domain.Rules;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Workflows.Application.Rules;

/// <summary>Tüm kuralların listesi (sayfalanmaz; organizasyon başına az sayıda kural beklenir), en eski önce.</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record ListRulesQuery : IQuery<IReadOnlyList<RuleDto>>;

public sealed class ListRulesHandler(IWorkflowReadStore store) : IQueryHandler<ListRulesQuery, IReadOnlyList<RuleDto>>
{
    public async Task<Result<IReadOnlyList<RuleDto>>> Handle(ListRulesQuery query, CancellationToken cancellationToken) =>
        Result.Success(await store.ListRulesAsync(cancellationToken).ConfigureAwait(false));
}

[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record GetRuleQuery(Guid Id) : IQuery<RuleDto>;

public sealed class GetRuleHandler(IWorkflowReadStore store) : IQueryHandler<GetRuleQuery, RuleDto>
{
    public async Task<Result<RuleDto>> Handle(GetRuleQuery query, CancellationToken cancellationToken) =>
        await store.GetRuleAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } rule
            ? rule
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Kural alanları (oluşturma ve güncelleme ortak doğrulaması); parametre hataları <c>params.&lt;alan&gt;</c> altındadır.</summary>
public interface IRuleFields
{
    string Name { get; }

    WorkflowRuleKind? Kind { get; }

    JsonElement? Params { get; }
}

public abstract class RuleFieldsValidator<T> : AbstractValidator<T>
    where T : IRuleFields
{
    protected RuleFieldsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(WorkflowLimits.NameMaxLength);
        RuleFor(x => x.Kind).NotNull();
        RuleFor(x => x.Params).Custom((value, context) =>
        {
            if (context.InstanceToValidate.Kind is not { } kind)
            {
                return;
            }

            foreach (var error in RuleParamsParser.Parse(kind, value).Errors)
            {
                context.AddFailure(error.Field.Length == 0 ? "params" : "params." + error.Field, error.Message);
            }
        });
    }
}

/// <summary>Kural rol kimliklerinin aktif organizasyonda var olduğunu doğrular (<c>workflow.role_not_found</c>, 400).</summary>
public sealed class RuleRoleVerifier(IRoleMemberLookup roles)
{
    public async Task<Result> VerifyAsync(RuleParams ruleParams, CancellationToken ct)
    {
        var roleId = ruleParams switch
        {
            LeadAssignmentParams lead => lead.AssigneeRoleId,
            DealApprovalParams deal => deal.ApproverRoleId,
            _ => Guid.Empty,
        };

        return roleId != Guid.Empty && await roles.RoleExistsAsync(roleId, ct).ConfigureAwait(false)
            ? Result.Success()
            : Error.Validation(WorkflowsErrors.RoleNotFound);
    }
}

/// <summary>Yeni kural; <c>isEnabled</c> verilmezse etkin başlar. Rol kiracıda bulunmalı (<c>workflow.role_not_found</c>).</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateRuleCommand(string Name, WorkflowRuleKind? Kind, JsonElement? Params, bool? IsEnabled) : ICommand<Guid>, IRuleFields;

public sealed class CreateRuleValidator : RuleFieldsValidator<CreateRuleCommand>;

public sealed class CreateRuleHandler(IWorkflowRuleRepository rules, RuleRoleVerifier roles, ITenantContext tenant) : ICommandHandler<CreateRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateRuleCommand command, CancellationToken cancellationToken)
    {
        var kind = command.Kind!.Value;
        var ruleParams = RuleParamsParser.Parse(kind, command.Params).Params!;

        var verified = await roles.VerifyAsync(ruleParams, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        var rule = WorkflowRule.Create(tenant.TenantId, command.Name, kind, ruleParams, command.IsEnabled ?? true);
        rules.Add(rule);
        return rule.Id;
    }
}

/// <summary>Tam değiştirme (PUT): ad, tür ve parametreler; etkin durumu değişmez (enable/disable uçları).</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record UpdateRuleCommand(Guid Id, string Name, WorkflowRuleKind? Kind, JsonElement? Params) : ICommand, IRuleFields;

public sealed class UpdateRuleValidator : RuleFieldsValidator<UpdateRuleCommand>
{
    public UpdateRuleValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateRuleHandler(IWorkflowRuleRepository rules, RuleRoleVerifier roles) : ICommandHandler<UpdateRuleCommand>
{
    public async Task<Result> Handle(UpdateRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = await rules.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var kind = command.Kind!.Value;
        var ruleParams = RuleParamsParser.Parse(kind, command.Params).Params!;
        var verified = await roles.VerifyAsync(ruleParams, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            return verified;
        }

        rule.Update(command.Name, kind, ruleParams);
        return Result.Success();
    }
}

/// <summary>Kuralı yumuşak siler; geçmiş yürütmeler kural adını/türünü kendi üzerinde taşır.</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record DeleteRuleCommand(Guid Id) : ICommand;

public sealed class DeleteRuleHandler(IWorkflowRuleRepository rules) : ICommandHandler<DeleteRuleCommand>
{
    public async Task<Result> Handle(DeleteRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = await rules.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        rules.Remove(rule);
        return Result.Success();
    }
}

/// <summary>Kuralı etkinleştirir (idempotent).</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record EnableRuleCommand(Guid Id) : ICommand;

public sealed class EnableRuleHandler(IWorkflowRuleRepository rules) : ICommandHandler<EnableRuleCommand>
{
    public async Task<Result> Handle(EnableRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = await rules.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        rule.SetEnabled(true);
        return Result.Success();
    }
}

/// <summary>Kuralı devre dışı bırakır (idempotent); çalışan yürütmeler etkilenmez.</summary>
[RequiresPermission(WorkflowsPermissions.Manage)]
public sealed record DisableRuleCommand(Guid Id) : ICommand;

public sealed class DisableRuleHandler(IWorkflowRuleRepository rules) : ICommandHandler<DisableRuleCommand>
{
    public async Task<Result> Handle(DisableRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = await rules.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        rule.SetEnabled(false);
        return Result.Success();
    }
}
