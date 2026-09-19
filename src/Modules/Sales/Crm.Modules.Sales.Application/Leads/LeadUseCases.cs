using Crm.Modules.Sales.Contracts;
using Crm.Modules.Sales.Domain;
using Crm.Modules.Sales.Domain.Accounts;
using Crm.Modules.Sales.Domain.Contacts;
using Crm.Modules.Sales.Domain.Deals;
using Crm.Modules.Sales.Domain.Leads;
using Crm.Modules.Sales.Domain.Pipelines;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Events;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Sales.Application.Leads;

[RequiresPermission(SalesPermissions.LeadsRead)]
public sealed record ListLeadsQuery(PagedQuery Paging, LeadStatus? Status, LeadSource? Source, Guid? OwnerUserId) : IQuery<PagedResult<LeadDto>>;

public sealed class ListLeadsHandler(ISalesReadStore store) : IQueryHandler<ListLeadsQuery, PagedResult<LeadDto>>
{
    public async Task<Result<PagedResult<LeadDto>>> Handle(ListLeadsQuery query, CancellationToken cancellationToken) =>
        await store.ListLeadsAsync(query.Paging, query.Status, query.Source, query.OwnerUserId, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(SalesPermissions.LeadsRead)]
public sealed record GetLeadQuery(Guid Id) : IQuery<LeadDto>;

public sealed class GetLeadHandler(ISalesReadStore store) : IQueryHandler<GetLeadQuery, LeadDto>
{
    public async Task<Result<LeadDto>> Handle(GetLeadQuery query, CancellationToken cancellationToken) =>
        await store.GetLeadAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } lead
            ? lead
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Lead alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface ILeadFields
{
    string? FirstName { get; }

    string LastName { get; }

    string Company { get; }

    string? Email { get; }

    string? Phone { get; }
}

public abstract class LeadFieldsValidator<T> : AbstractValidator<T>
    where T : ILeadFields
{
    protected LeadFieldsValidator()
    {
        RuleFor(x => x.FirstName).Optional(SalesLimits.PersonNameMaxLength);
        RuleFor(x => x.LastName).Required(SalesLimits.PersonNameMaxLength);
        RuleFor(x => x.Company).Required(SalesLimits.NameMaxLength);
        RuleFor(x => x.Email).OptionalEmail();
        RuleFor(x => x.Phone).Optional(SalesLimits.PhoneMaxLength);
    }
}

/// <summary>Oluşturma her zaman <c>new</c> durumuyla başlar; kaynak verilmezse <c>other</c>.</summary>
[RequiresPermission(SalesPermissions.LeadsWrite)]
public sealed record CreateLeadCommand(
    string? FirstName,
    string LastName,
    string Company,
    string? Email,
    string? Phone,
    LeadSource? Source,
    LeadRating? Rating,
    Guid? OwnerUserId) : ICommand<Guid>, ILeadFields;

public sealed class CreateLeadValidator : LeadFieldsValidator<CreateLeadCommand>;

public sealed class CreateLeadHandler(ILeadRepository leads, OwnerResolver owners, ITenantContext tenant, IIntegrationEventOutbox outbox, ICurrentUser user)
    : ICommandHandler<CreateLeadCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateLeadCommand command, CancellationToken cancellationToken)
    {
        var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        var lead = Lead.Create(
            tenant.TenantId,
            command.FirstName,
            command.LastName,
            command.Company,
            owner.Value,
            command.Source ?? LeadSource.Other,
            command.Rating,
            command.Email,
            command.Phone);
        leads.Add(lead);

        // M4: workflow tetikleyicisi (aynı transaction'da outbox'a yazılır; komut başarısız olursa yayınlanmaz).
        outbox.Enqueue(new LeadCreated(
            tenant.TenantId,
            lead.Id,
            lead.FullName,
            lead.Company,
            LeadSourceNames.ToWire(lead.Source),
            lead.OwnerUserId,
            user.UserId));
        return lead.Id;
    }
}

/// <summary>Dönüşmüş lead güncellenemez: <c>lead.already_converted</c> (409). Durum <c>converted</c> yalnız dönüştürmeyle olur.</summary>
[RequiresPermission(SalesPermissions.LeadsWrite)]
public sealed record UpdateLeadCommand(
    Guid Id,
    string? FirstName,
    string LastName,
    string Company,
    string? Email,
    string? Phone,
    LeadSource? Source,
    LeadStatus? Status,
    LeadRating? Rating,
    Guid? OwnerUserId) : ICommand, ILeadFields;

public sealed class UpdateLeadValidator : LeadFieldsValidator<UpdateLeadCommand>
{
    public UpdateLeadValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Status).NotEqual(LeadStatus.Converted).WithMessage(SalesErrors.LeadStatusNotSettable);
    }
}

public sealed class UpdateLeadHandler(ILeadRepository leads, OwnerResolver owners) : ICommandHandler<UpdateLeadCommand>
{
    public async Task<Result> Handle(UpdateLeadCommand command, CancellationToken cancellationToken)
    {
        var lead = await leads.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (lead is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (lead.IsConverted)
        {
            return Error.Conflict(SalesErrors.LeadAlreadyConverted);
        }

        var owner = await owners.ResolveAsync(command.OwnerUserId, lead.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        return lead.Update(
            command.FirstName,
            command.LastName,
            command.Company,
            owner.Value,
            command.Source ?? lead.Source,
            command.Status ?? lead.Status,
            command.Rating,
            command.Email,
            command.Phone);
    }
}

[RequiresPermission(SalesPermissions.LeadsWrite)]
public sealed record DeleteLeadCommand(Guid Id) : ICommand;

public sealed class DeleteLeadHandler(ILeadRepository leads) : ICommandHandler<DeleteLeadCommand>
{
    public async Task<Result> Handle(DeleteLeadCommand command, CancellationToken cancellationToken)
    {
        var lead = await leads.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (lead is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        leads.Remove(lead);
        return Result.Success();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Dönüştürme: lead → firma (+ kişi + isteğe bağlı fırsat), tek transaction, LeadConverted integration event.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Lead'i dönüştürür. <c>AccountId</c> verilirse mevcut firma kullanılır, yoksa lead'in şirket adıyla yeni firma açılır; kişi
/// lead adından oluşur; <c>CreateDeal</c> ise fırsat pipeline'ın (verilmezse varsayılanın) ilk açık aşamasında başlar. Firma,
/// kişi, fırsat, lead durumu ve outbox kaydı UnitOfWorkBehaviour'ın tek SaveChanges'inde yazılır: hata → hiçbiri kalıcı olmaz.
/// Fırsat açılıyorsa ayrıca <c>crm.deals.write</c> gerekir (handler'da denetlenir).
/// </summary>
[RequiresPermission(SalesPermissions.LeadsWrite)]
[RequiresPermission(SalesPermissions.AccountsWrite)]
[RequiresPermission(SalesPermissions.ContactsWrite)]
public sealed record ConvertLeadCommand(
    Guid LeadId,
    Guid? AccountId,
    bool CreateDeal,
    string? DealName,
    decimal? Amount,
    DateOnly? ClosingDate,
    Guid? PipelineId) : ICommand<ConvertLeadResult>;

public sealed class ConvertLeadValidator : AbstractValidator<ConvertLeadCommand>
{
    public ConvertLeadValidator()
    {
        RuleFor(x => x.LeadId).NotEmpty();
        RuleFor(x => x.AccountId).NotEqual(Guid.Empty).When(x => x.AccountId is not null);
        RuleFor(x => x.DealName).NotEmpty().When(x => x.CreateDeal);
        RuleFor(x => x.DealName).Optional(SalesLimits.NameMaxLength);
        RuleFor(x => x.Amount).OptionalAmount();
    }
}

public sealed class ConvertLeadHandler(
    ILeadRepository leads,
    IAccountRepository accounts,
    IContactRepository contacts,
    IDealRepository deals,
    IPipelineRepository pipelines,
    DefaultPipelineResolver defaultPipeline,
    IPermissionService permissions,
    ICurrentUser user,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    TimeProvider clock) : ICommandHandler<ConvertLeadCommand, ConvertLeadResult>
{
    public async Task<Result<ConvertLeadResult>> Handle(ConvertLeadCommand command, CancellationToken cancellationToken)
    {
        var lead = await leads.GetByIdAsync(command.LeadId, cancellationToken).ConfigureAwait(false);
        if (lead is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (lead.IsConverted)
        {
            return Error.Conflict(SalesErrors.LeadAlreadyConverted);
        }

        Pipeline? pipeline = null;
        if (command.CreateDeal)
        {
            if (user.UserId is { } userId && !await permissions.HasAsync(userId, SalesPermissions.DealsWrite, cancellationToken).ConfigureAwait(false))
            {
                return Error.Forbidden(ErrorCodes.Forbidden, ("permission", SalesPermissions.DealsWrite));
            }

            pipeline = command.PipelineId is { } pipelineId
                ? await pipelines.GetByIdAsync(pipelineId, cancellationToken).ConfigureAwait(false)
                : await defaultPipeline.GetDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (pipeline is null)
            {
                return Error.NotFound(ErrorCodes.NotFound);
            }
        }

        // Tüm nesneler önce bellekte kurulur; yalnız hepsi geçerliyse depolara eklenir (başarısızlıkta hiçbir şey izlenmez).
        var now = clock.GetUtcNow().UtcDateTime;
        Account? newAccount = null;
        var account = command.AccountId is { } existingId
            ? await accounts.GetByIdAsync(existingId, cancellationToken).ConfigureAwait(false)
            : newAccount = Account.Create(tenant.TenantId, lead.Company, lead.OwnerUserId);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var contact = Contact.Create(tenant.TenantId, lead.FirstName, lead.LastName, lead.OwnerUserId, account.Id, lead.Email, lead.Phone);

        Deal? deal = null;
        if (pipeline is not null)
        {
            var created = Deal.Create(
                tenant.TenantId,
                command.DealName!,
                account.Id,
                lead.OwnerUserId,
                pipeline,
                stage: null,
                contact.Id,
                command.Amount,
                currency: null,
                command.ClosingDate,
                lostReason: null,
                now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            deal = created.Value;
        }

        var converted = lead.Convert(account.Id, contact.Id, deal?.Id, now);
        if (converted.IsFailure)
        {
            return converted.Error;
        }

        if (newAccount is not null)
        {
            accounts.Add(newAccount);
        }

        contacts.Add(contact);
        if (deal is not null)
        {
            deals.Add(deal);
        }

        outbox.Enqueue(new LeadConverted(tenant.TenantId, lead.Id, account.Id, contact.Id, deal?.Id, user.UserId));
        return new ConvertLeadResult(account.Id, contact.Id, deal?.Id);
    }
}

/// <summary>Lead kaynağının tel adı (camelCase; API/olay sözleşmesi).</summary>
public static class LeadSourceNames
{
    public static string ToWire(LeadSource source) => source switch
    {
        LeadSource.Web => "web",
        LeadSource.Referral => "referral",
        LeadSource.Campaign => "campaign",
        LeadSource.ColdCall => "coldCall",
        _ => "other",
    };
}
