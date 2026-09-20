using FluentValidation;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Sales.Domain;
using Sense.Crm.Modules.Sales.Domain.Pipelines;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Sales.Application.Pipelines;

/// <summary>Huni listesi. Organizasyonda hiç huni yoksa (kayıt olayı işlenmeden) varsayılan tembel olarak tohumlanır.</summary>
[RequiresPermission(SalesPermissions.DealsRead)]
public sealed record ListPipelinesQuery : IQuery<IReadOnlyList<PipelineDto>>;

public sealed class ListPipelinesHandler(ISalesReadStore store, DefaultPipelineResolver defaultPipeline) : IQueryHandler<ListPipelinesQuery, IReadOnlyList<PipelineDto>>
{
    public async Task<Result<IReadOnlyList<PipelineDto>>> Handle(ListPipelinesQuery query, CancellationToken cancellationToken)
    {
        await defaultPipeline.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(await store.ListPipelinesAsync(cancellationToken).ConfigureAwait(false));
    }
}

[RequiresPermission(SalesPermissions.DealsRead)]
public sealed record GetPipelineQuery(Guid Id) : IQuery<PipelineDto>;

public sealed class GetPipelineHandler(ISalesReadStore store) : IQueryHandler<GetPipelineQuery, PipelineDto>
{
    public async Task<Result<PipelineDto>> Handle(GetPipelineQuery query, CancellationToken cancellationToken) =>
        await store.GetPipelineAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } pipeline
            ? pipeline
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Yeni huni varsayılan aşamalarla (organizasyon dilinde adlarla) açılır; organizasyonda huni yoksa varsayılan olur.</summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
[NoPlanLimit("Satış hunisi yapılandırmadır, kayıt değildir (kayıt limitine sayılmaz)")]
public sealed record CreatePipelineCommand(string Name) : ICommand<Guid>;

public sealed class CreatePipelineValidator : AbstractValidator<CreatePipelineCommand>
{
    public CreatePipelineValidator() => RuleFor(x => x.Name).Required(SalesLimits.NameMaxLength);
}

public sealed class CreatePipelineHandler(IPipelineRepository pipelines, ITenantDirectory directory, ITenantContext tenant) : ICommandHandler<CreatePipelineCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreatePipelineCommand command, CancellationToken cancellationToken)
    {
        var info = await directory.FindAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var locale = info?.DefaultLocale ?? Cultures.TurkishLanguage;
        var isDefault = !await pipelines.AnyAsync(cancellationToken).ConfigureAwait(false);

        var created = Pipeline.Create(tenant.TenantId, command.Name, isDefault, DefaultPipelineTemplate.Stages(locale));
        if (created.IsFailure)
        {
            return created.Error;
        }

        pipelines.Add(created.Value);
        return created.Value.Id;
    }
}

/// <summary>
/// Ad ve varsayılan işareti. Varsayılan huni "varsayılan değil" yapılamaz (<c>pipeline.default_required</c>); başka bir
/// hunini varsayılan yapmak eskisini varsayılan olmaktan çıkarır.
/// </summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record UpdatePipelineCommand(Guid Id, string Name, bool IsDefault) : ICommand;

public sealed class UpdatePipelineValidator : AbstractValidator<UpdatePipelineCommand>
{
    public UpdatePipelineValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).Required(SalesLimits.NameMaxLength);
    }
}

public sealed class UpdatePipelineHandler(IPipelineRepository pipelines) : ICommandHandler<UpdatePipelineCommand>
{
    public async Task<Result> Handle(UpdatePipelineCommand command, CancellationToken cancellationToken)
    {
        var pipeline = await pipelines.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (pipeline.IsDefault && !command.IsDefault)
        {
            return Error.Rule(SalesErrors.DefaultPipelineRequired);
        }

        if (!pipeline.IsDefault && command.IsDefault && await pipelines.GetDefaultAsync(cancellationToken).ConfigureAwait(false) is { } previous)
        {
            previous.SetDefault(false);
        }

        pipeline.Rename(command.Name);
        pipeline.SetDefault(command.IsDefault);
        return Result.Success();
    }
}

public sealed record StageInput(Guid? Id, string Name, int Probability, StageKind Kind);

/// <summary>
/// Aşama kümesini toptan günceller (dizi sırası = <c>order</c>). Tam bir won ve bir lost aşama şart (yoksa <c>validation</c>);
/// fırsat içeren aşamayı silmek <c>pipeline.stage_in_use</c>.
/// </summary>
[RequiresPermission(OrgPermissions.SettingsManage)]
public sealed record ReplaceStagesCommand(Guid Id, IReadOnlyList<StageInput> Stages) : ICommand;

public sealed class ReplaceStagesValidator : AbstractValidator<ReplaceStagesCommand>
{
    public ReplaceStagesValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Stages).NotNull();
        RuleForEach(x => x.Stages).ChildRules(stage =>
        {
            stage.RuleFor(s => s.Name).Required(SalesLimits.StageNameMaxLength);
            stage.RuleFor(s => s.Probability).InclusiveBetween(0, SalesLimits.MaxProbability);
            stage.RuleFor(s => s.Kind).IsInEnum();
        });
        RuleFor(x => x.Stages)
            .Must(stages => Pipeline.ValidateStages(stages.Select(ToDefinition).ToList()).IsSuccess)
            .When(x => x.Stages is not null)
            .WithMessage(SalesErrors.InvalidStageSet);
    }

    internal static StageDefinition ToDefinition(StageInput stage) => new(stage.Id, stage.Name, stage.Probability, stage.Kind);
}

public sealed class ReplaceStagesHandler(IPipelineRepository pipelines) : ICommandHandler<ReplaceStagesCommand>
{
    public async Task<Result> Handle(ReplaceStagesCommand command, CancellationToken cancellationToken)
    {
        var pipeline = await pipelines.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var used = await pipelines.GetStageIdsInUseAsync(pipeline.Id, cancellationToken).ConfigureAwait(false);
        var replaced = pipeline.ReplaceStages(command.Stages.Select(ReplaceStagesValidator.ToDefinition).ToList(), used);
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        pipelines.RemoveStages(replaced.Value);
        return Result.Success();
    }
}

/// <summary>
/// Yeni organizasyon olayı (Identity kayıt): varsayılan satış hunisini organizasyon dilinde tohumlar (idempotent).
/// Outbox üzerinden Worker'da (veya Worker yokken API başlangıcı/tembel yolla) çalışır.
/// </summary>
[EntitlementExempt("Varsayılan satış hunisi plandan bağımsız tohumlanır (modül sonradan açılınca hazır olsun)")]
public sealed class OrganizationCreatedHandler(IDefaultPipelineSeeder seeder) : IIntegrationEventHandler<OrganizationCreated>
{
    public async Task Handle(OrganizationCreated integrationEvent, CancellationToken cancellationToken) =>
        await seeder.EnsureAsync(integrationEvent.TenantId, integrationEvent.DefaultLocale, cancellationToken).ConfigureAwait(false);
}
