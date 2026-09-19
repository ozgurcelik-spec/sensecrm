using Crm.Modules.Marketing.Contracts;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Domain.Campaigns;
using Crm.Modules.Marketing.Domain.Metrics;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Marketing.Application.Campaigns;

/// <summary>
/// Kampanya listesi: filtreler <c>q</c> (ad + açıklama), <c>type</c> ve <c>status</c> (virgülle ayrılmış çoklu), <c>ownerUserId</c>,
/// <c>startFrom</c>/<c>startTo</c> (<c>startDate</c> üzerinde, uçlar dahil; biri verilince <c>startDate</c>'i olmayanlar dışarıda kalır).
/// </summary>
[RequiresPermission(MarketingPermissions.Read)]
public sealed record ListCampaignsQuery(PagedQuery Paging, string? Type, string? Status, Guid? OwnerUserId, DateOnly? StartFrom, DateOnly? StartTo)
    : IQuery<PagedResult<CampaignDto>>;

public sealed class ListCampaignsValidator : AbstractValidator<ListCampaignsQuery>
{
    public ListCampaignsValidator()
    {
        RuleFor(x => x.Type).Must(v => EnumListParser.TryParse<CampaignType>(v, out _)).WithMessage(MarketingErrors.InvalidType);
        RuleFor(x => x.Status).Must(v => EnumListParser.TryParse<CampaignStatus>(v, out _)).WithMessage(MarketingErrors.InvalidStatus);
    }
}

public sealed class ListCampaignsHandler(IMarketingReadStore store) : IQueryHandler<ListCampaignsQuery, PagedResult<CampaignDto>>
{
    public async Task<Result<PagedResult<CampaignDto>>> Handle(ListCampaignsQuery query, CancellationToken cancellationToken)
    {
        var filter = new CampaignFilter(
            EnumListParser.ParseOrEmpty<CampaignType>(query.Type),
            EnumListParser.ParseOrEmpty<CampaignStatus>(query.Status),
            query.OwnerUserId,
            query.StartFrom,
            query.StartTo);
        return await store.ListCampaignsAsync(query.Paging, filter, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission(MarketingPermissions.Read)]
public sealed record GetCampaignQuery(Guid Id) : IQuery<CampaignDto>;

public sealed class GetCampaignHandler(IMarketingReadStore store) : IQueryHandler<GetCampaignQuery, CampaignDto>
{
    public async Task<Result<CampaignDto>> Handle(GetCampaignQuery query, CancellationToken cancellationToken) =>
        await store.GetCampaignAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } campaign
            ? campaign
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Kampanya alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface ICampaignFields
{
    string Name { get; }

    CampaignType? Type { get; }

    string? Currency { get; }

    decimal? Budget { get; }

    decimal? ExpectedRevenue { get; }

    decimal? ActualCost { get; }

    string? Description { get; }

    Guid? OwnerUserId { get; }
}

public abstract class CampaignFieldsValidator<T> : AbstractValidator<T>
    where T : ICampaignFields
{
    protected CampaignFieldsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MarketingLimits.NameMaxLength);
        RuleFor(x => x.Type).NotNull();
        RuleFor(x => x.Currency).ValidCurrency();
        RuleFor(x => x.Budget).ValidAmount();
        RuleFor(x => x.ExpectedRevenue).ValidAmount();
        RuleFor(x => x.ActualCost).ValidAmount();
        RuleFor(x => x.Description).MaximumLength(MarketingLimits.DescriptionMaxLength);
        RuleFor(x => x.OwnerUserId).NotEqual(Guid.Empty).When(x => x.OwnerUserId is not null);
    }
}

/// <summary>
/// Yeni kampanya. Durum verilmezse <c>planned</c> (verilirse yalnız <c>planned</c>/<c>active</c>); sahip verilmezse çağıran, verilen
/// aktif üye olmalı (<c>owner.not_member</c>); <c>endDate &lt; startDate</c> → <c>campaign.invalid_date_range</c>; para birimi varsayılan <c>TRY</c>.
/// </summary>
[RequiresPermission(MarketingPermissions.Write)]
public sealed record CreateCampaignCommand(
    string Name,
    CampaignType? Type,
    CampaignStatus? Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? Currency,
    decimal? Budget,
    decimal? ExpectedRevenue,
    decimal? ActualCost,
    string? Description,
    Guid? OwnerUserId) : ICommand<Guid>, ICampaignFields;

public sealed class CreateCampaignValidator : CampaignFieldsValidator<CreateCampaignCommand>
{
    public CreateCampaignValidator() =>
        RuleFor(x => x.Status)
            .Must(status => status is null or (CampaignStatus.Planned or CampaignStatus.Active))
            .WithMessage(MarketingErrors.InvalidCreateStatus);
}

public sealed class CreateCampaignHandler(ICampaignRepository campaigns, OwnerResolver owners, ITenantContext tenant) : ICommandHandler<CreateCampaignCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateCampaignCommand command, CancellationToken cancellationToken)
    {
        var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        var created = Campaign.Create(
            tenant.TenantId,
            command.Name,
            command.Type!.Value,
            command.Status,
            command.StartDate,
            command.EndDate,
            command.Currency,
            command.Budget,
            command.ExpectedRevenue,
            command.ActualCost,
            command.Description,
            owner.Value);
        if (created.IsFailure)
        {
            return created.Error;
        }

        campaigns.Add(created.Value);
        return created.Value.Id;
    }
}

/// <summary>
/// Tam değiştirme (PUT; durum hariç): gönderilmeyen isteğe bağlı alan temizlenir, <c>ownerUserId</c> verilmezse mevcut sahip korunur
/// (yalnız değiştiyse üyelik yeniden sorgulanır), <c>currency</c> verilmezse <c>TRY</c>. Her durumda çalışır.
/// </summary>
[RequiresPermission(MarketingPermissions.Write)]
public sealed record UpdateCampaignCommand(
    Guid Id,
    string Name,
    CampaignType? Type,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? Currency,
    decimal? Budget,
    decimal? ExpectedRevenue,
    decimal? ActualCost,
    string? Description,
    Guid? OwnerUserId) : ICommand, ICampaignFields;

public sealed class UpdateCampaignValidator : CampaignFieldsValidator<UpdateCampaignCommand>
{
    public UpdateCampaignValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateCampaignHandler(ICampaignRepository campaigns, OwnerResolver owners) : ICommandHandler<UpdateCampaignCommand>
{
    public async Task<Result> Handle(UpdateCampaignCommand command, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (campaign is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var owner = await owners.ResolveAsync(command.OwnerUserId, campaign.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        return campaign.Update(
            command.Name,
            command.Type!.Value,
            command.StartDate,
            command.EndDate,
            command.Currency,
            command.Budget,
            command.ExpectedRevenue,
            command.ActualCost,
            command.Description,
            owner.Value);
    }
}

/// <summary>Durum geçişi (<c>POST /campaigns/{id}/status</c>): geçiş tablosu <see cref="Campaign.CanTransition"/>; aynı durum no-op.</summary>
[RequiresPermission(MarketingPermissions.Write)]
public sealed record ChangeCampaignStatusCommand(Guid Id, CampaignStatus? Status) : ICommand;

public sealed class ChangeCampaignStatusValidator : AbstractValidator<ChangeCampaignStatusCommand>
{
    public ChangeCampaignStatusValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Status).NotNull();
    }
}

public sealed class ChangeCampaignStatusHandler(ICampaignRepository campaigns) : ICommandHandler<ChangeCampaignStatusCommand>
{
    public async Task<Result> Handle(ChangeCampaignStatusCommand command, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return campaign is null ? Error.NotFound(ErrorCodes.NotFound) : campaign.ChangeStatus(command.Status!.Value);
    }
}

/// <summary>Yumuşak silme: üyelikler kalır ama hiçbir uçtan görünmez; üyeliği olan kampanya da silinebilir.</summary>
[RequiresPermission(MarketingPermissions.Write)]
public sealed record DeleteCampaignCommand(Guid Id) : ICommand;

public sealed class DeleteCampaignHandler(ICampaignRepository campaigns) : ICommandHandler<DeleteCampaignCommand>
{
    public async Task<Result> Handle(DeleteCampaignCommand command, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (campaign is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        campaigns.Remove(campaign);
        return Result.Success();
    }
}

/// <summary>Kampanya metrikleri (hesaplanır, saklanmaz): tek gruplama sorgusu + <see cref="MarketingMetrics"/> formülleri.</summary>
[RequiresPermission(MarketingPermissions.Read)]
public sealed record GetCampaignMetricsQuery(Guid Id) : IQuery<CampaignMetricsDto>;

public sealed class GetCampaignMetricsHandler(IMarketingReadStore store) : IQueryHandler<GetCampaignMetricsQuery, CampaignMetricsDto>
{
    public async Task<Result<CampaignMetricsDto>> Handle(GetCampaignMetricsQuery query, CancellationToken cancellationToken)
    {
        if (await store.GetCampaignAsync(query.Id, cancellationToken).ConfigureAwait(false) is not { } campaign)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var counts = await store.GetMemberCountsAsync(query.Id, cancellationToken).ConfigureAwait(false);
        return new CampaignMetricsDto(
            campaign.Id,
            counts.MemberCount,
            counts.LeadCount,
            counts.ContactCount,
            new MemberStatusCounts(counts.Added, counts.Sent, counts.Responded, counts.Converted, counts.Unsubscribed),
            counts.ContactedCount,
            counts.ResponseCount,
            MarketingMetrics.ResponseRate(counts),
            counts.Converted,
            MarketingMetrics.ConversionRate(counts),
            campaign.Currency,
            MarketingMetrics.CostPerLead(campaign.ActualCost, counts.LeadCount));
    }
}
