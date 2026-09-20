using FluentValidation;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Sales.Domain;
using Sense.Crm.Modules.Sales.Domain.Deals;
using Sense.Crm.Modules.Sales.Domain.Pipelines;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Sales.Application.Deals;

[RequiresPermission(SalesPermissions.DealsRead)]
public sealed record ListDealsQuery(PagedQuery Paging, DealFilter Filter) : IQuery<PagedResult<DealDto>>;

public sealed class ListDealsHandler(ISalesReadStore store) : IQueryHandler<ListDealsQuery, PagedResult<DealDto>>
{
    public async Task<Result<PagedResult<DealDto>>> Handle(ListDealsQuery query, CancellationToken cancellationToken) =>
        await store.ListDealsAsync(query.Paging, query.Filter, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(SalesPermissions.DealsRead)]
public sealed record GetDealQuery(Guid Id) : IQuery<DealDto>;

public sealed class GetDealHandler(ISalesReadStore store) : IQueryHandler<GetDealQuery, DealDto>
{
    public async Task<Result<DealDto>> Handle(GetDealQuery query, CancellationToken cancellationToken) =>
        await store.GetDealAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } deal
            ? deal
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Kanban panosu: huninin (verilmezse varsayılanın) her aşaması için toplam tutar, adet ve en çok 100 fırsat özeti.</summary>
[RequiresPermission(SalesPermissions.DealsRead)]
public sealed record GetDealBoardQuery(Guid? PipelineId, Guid? OwnerUserId) : IQuery<DealBoardDto>;

public sealed class GetDealBoardHandler(ISalesReadStore store, DefaultPipelineResolver defaultPipeline)
    : IQueryHandler<GetDealBoardQuery, DealBoardDto>
{
    public async Task<Result<DealBoardDto>> Handle(GetDealBoardQuery query, CancellationToken cancellationToken)
    {
        var pipelineId = query.PipelineId;
        if (pipelineId is null)
        {
            pipelineId = (await defaultPipeline.GetDefaultAsync(cancellationToken).ConfigureAwait(false))?.Id;
        }

        if (pipelineId is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        return await store.GetBoardAsync(pipelineId.Value, query.OwnerUserId, cancellationToken).ConfigureAwait(false) is { } board
            ? board
            : Error.NotFound(ErrorCodes.NotFound);
    }
}

/// <summary>Fırsat alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IDealFields
{
    string Name { get; }

    Guid AccountId { get; }

    decimal? Amount { get; }

    string? Currency { get; }

    string? LostReason { get; }
}

public abstract class DealFieldsValidator<T> : AbstractValidator<T>
    where T : IDealFields
{
    protected DealFieldsValidator()
    {
        RuleFor(x => x.Name).Required(SalesLimits.NameMaxLength);
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.Amount).OptionalAmount();
        RuleFor(x => x.Currency).OptionalCurrency();
        RuleFor(x => x.LostReason).Optional(SalesLimits.LostReasonMaxLength);
    }
}

/// <summary>
/// Yeni fırsat. Pipeline verilmezse varsayılan, aşama verilmezse pipeline'ın ilk açık aşaması; para birimi varsayılan TRY.
/// </summary>
[RequiresPermission(SalesPermissions.DealsWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateDealCommand(
    string Name,
    Guid AccountId,
    Guid? ContactId,
    Guid? PipelineId,
    Guid? StageId,
    decimal? Amount,
    string? Currency,
    DateOnly? ClosingDate,
    Guid? OwnerUserId,
    string? LostReason) : ICommand<Guid>, IDealFields;

public sealed class CreateDealValidator : DealFieldsValidator<CreateDealCommand>;

public sealed class CreateDealHandler(
    IDealRepository deals,
    IAccountRepository accounts,
    IContactRepository contacts,
    IPipelineRepository pipelines,
    DefaultPipelineResolver defaultPipeline,
    OwnerResolver owners,
    ITenantContext tenant,
    TimeProvider clock) : ICommandHandler<CreateDealCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateDealCommand command, CancellationToken cancellationToken)
    {
        var pipeline = command.PipelineId is { } pipelineId
            ? await pipelines.GetByIdAsync(pipelineId, cancellationToken).ConfigureAwait(false)
            : await defaultPipeline.GetDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        PipelineStage? stage = null;
        if (command.StageId is { } stageId)
        {
            stage = pipeline.Stages.FirstOrDefault(s => s.Id == stageId);
            if (stage is null)
            {
                return Error.NotFound(SalesErrors.StageNotFound);
            }
        }

        // Kiracı filtresi: başka organizasyonun firma/kişisi burada bulunmaz → not_found.
        if (await accounts.GetByIdAsync(command.AccountId, cancellationToken).ConfigureAwait(false) is null
            || (command.ContactId is { } contactId && await contacts.GetByIdAsync(contactId, cancellationToken).ConfigureAwait(false) is null))
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        var created = Deal.Create(
            tenant.TenantId,
            command.Name,
            command.AccountId,
            owner.Value,
            pipeline,
            stage,
            command.ContactId,
            command.Amount,
            command.Currency,
            command.ClosingDate,
            command.LostReason,
            clock.GetUtcNow().UtcDateTime);
        if (created.IsFailure)
        {
            return created.Error;
        }

        deals.Add(created.Value);
        return created.Value.Id;
    }
}

/// <summary>Aşama dışındaki alanlar. Aşama yalnız <see cref="MoveDealStageCommand"/> ile değişir.</summary>
[RequiresPermission(SalesPermissions.DealsWrite)]
public sealed record UpdateDealCommand(
    Guid Id,
    string Name,
    Guid AccountId,
    Guid? ContactId,
    decimal? Amount,
    string? Currency,
    DateOnly? ClosingDate,
    Guid? OwnerUserId,
    string? LostReason) : ICommand, IDealFields;

public sealed class UpdateDealValidator : DealFieldsValidator<UpdateDealCommand>
{
    public UpdateDealValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateDealHandler(IDealRepository deals, IAccountRepository accounts, IContactRepository contacts, OwnerResolver owners)
    : ICommandHandler<UpdateDealCommand>
{
    public async Task<Result> Handle(UpdateDealCommand command, CancellationToken cancellationToken)
    {
        var deal = await deals.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (deal is null
            || await accounts.GetByIdAsync(command.AccountId, cancellationToken).ConfigureAwait(false) is null
            || (command.ContactId is { } contactId && await contacts.GetByIdAsync(contactId, cancellationToken).ConfigureAwait(false) is null))
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var owner = await owners.ResolveAsync(command.OwnerUserId, deal.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        deal.Update(command.Name, command.AccountId, owner.Value, command.ContactId, command.Amount, command.Currency, command.ClosingDate, command.LostReason);
        return Result.Success();
    }
}

[RequiresPermission(SalesPermissions.DealsWrite)]
public sealed record DeleteDealCommand(Guid Id) : ICommand;

public sealed class DeleteDealHandler(IDealRepository deals) : ICommandHandler<DeleteDealCommand>
{
    public async Task<Result> Handle(DeleteDealCommand command, CancellationToken cancellationToken)
    {
        var deal = await deals.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (deal is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        deals.Remove(deal);
        return Result.Success();
    }
}

/// <summary>
/// Aşama değiştirir. Aşama fırsatın hunisinde olmalı (<c>pipeline.stage_not_found</c>); kaybedilen aşamada neden zorunlu
/// (<c>deal.lost_reason_required</c>); kazanma/kaybetmede <c>closedAt</c> yazılır; <c>DealStageChanged</c> domain event'i yükselir.
/// </summary>
[RequiresPermission(SalesPermissions.DealsWrite)]
public sealed record MoveDealStageCommand(Guid Id, Guid StageId, string? LostReason) : ICommand;

public sealed class MoveDealStageValidator : AbstractValidator<MoveDealStageCommand>
{
    public MoveDealStageValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.StageId).NotEmpty();
        RuleFor(x => x.LostReason).Optional(SalesLimits.LostReasonMaxLength);
    }
}

public sealed class MoveDealStageHandler(IDealRepository deals, IPipelineRepository pipelines, TimeProvider clock) : ICommandHandler<MoveDealStageCommand>
{
    public async Task<Result> Handle(MoveDealStageCommand command, CancellationToken cancellationToken)
    {
        var deal = await deals.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (deal is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var stage = await pipelines.GetStageAsync(command.StageId, cancellationToken).ConfigureAwait(false);
        if (stage is null)
        {
            return Error.NotFound(SalesErrors.StageNotFound);
        }

        return deal.MoveToStage(stage, command.LostReason, clock.GetUtcNow().UtcDateTime);
    }
}
