using FluentValidation;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Sales.Domain;
using Sense.Crm.Modules.Sales.Domain.Contacts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Sales.Application.Contacts;

[RequiresPermission(SalesPermissions.ContactsRead)]
public sealed record ListContactsQuery(PagedQuery Paging, Guid? AccountId, Guid? OwnerUserId) : IQuery<PagedResult<ContactDto>>;

public sealed class ListContactsHandler(ISalesReadStore store) : IQueryHandler<ListContactsQuery, PagedResult<ContactDto>>
{
    public async Task<Result<PagedResult<ContactDto>>> Handle(ListContactsQuery query, CancellationToken cancellationToken) =>
        await store.ListContactsAsync(query.Paging, query.AccountId, query.OwnerUserId, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(SalesPermissions.ContactsRead)]
public sealed record GetContactQuery(Guid Id) : IQuery<ContactDto>;

public sealed class GetContactHandler(ISalesReadStore store) : IQueryHandler<GetContactQuery, ContactDto>
{
    public async Task<Result<ContactDto>> Handle(GetContactQuery query, CancellationToken cancellationToken) =>
        await store.GetContactAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } contact
            ? contact
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Kişi alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IContactFields
{
    string? FirstName { get; }

    string LastName { get; }

    string? Email { get; }

    string? Phone { get; }

    string? Mobile { get; }

    string? Title { get; }

    AddressDto? MailingAddress { get; }
}

public abstract class ContactFieldsValidator<T> : AbstractValidator<T>
    where T : IContactFields
{
    protected ContactFieldsValidator()
    {
        RuleFor(x => x.FirstName).Optional(SalesLimits.PersonNameMaxLength);
        RuleFor(x => x.LastName).Required(SalesLimits.PersonNameMaxLength);
        RuleFor(x => x.Email).OptionalEmail();
        RuleFor(x => x.Phone).Optional(SalesLimits.PhoneMaxLength);
        RuleFor(x => x.Mobile).Optional(SalesLimits.PhoneMaxLength);
        RuleFor(x => x.Title).Optional(SalesLimits.TitleMaxLength);
        RuleFor(x => x.MailingAddress).SetValidator(new AddressValidator()!);
    }
}

[RequiresPermission(SalesPermissions.ContactsWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateContactCommand(
    string? FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? Title,
    Guid? AccountId,
    AddressDto? MailingAddress,
    Guid? OwnerUserId) : ICommand<Guid>, IContactFields;

public sealed class CreateContactValidator : ContactFieldsValidator<CreateContactCommand>
{
    public CreateContactValidator() => RuleFor(x => x.AccountId).NotEqual(Guid.Empty).When(x => x.AccountId is not null);
}

public sealed class CreateContactHandler(IContactRepository contacts, IAccountRepository accounts, OwnerResolver owners, ITenantContext tenant)
    : ICommandHandler<CreateContactCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateContactCommand command, CancellationToken cancellationToken)
    {
        // Kiracı filtresi: başka organizasyonun firması burada bulunmaz → not_found.
        if (command.AccountId is { } accountId && await accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        var contact = Contact.Create(
            tenant.TenantId,
            command.FirstName,
            command.LastName,
            owner.Value,
            command.AccountId,
            command.Email,
            command.Phone,
            command.Mobile,
            command.Title,
            command.MailingAddress.ToDomain());
        contacts.Add(contact);
        return contact.Id;
    }
}

[RequiresPermission(SalesPermissions.ContactsWrite)]
public sealed record UpdateContactCommand(
    Guid Id,
    string? FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? Title,
    Guid? AccountId,
    AddressDto? MailingAddress,
    Guid? OwnerUserId) : ICommand, IContactFields;

public sealed class UpdateContactValidator : ContactFieldsValidator<UpdateContactCommand>
{
    public UpdateContactValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.AccountId).NotEqual(Guid.Empty).When(x => x.AccountId is not null);
    }
}

public sealed class UpdateContactHandler(IContactRepository contacts, IAccountRepository accounts, OwnerResolver owners) : ICommandHandler<UpdateContactCommand>
{
    public async Task<Result> Handle(UpdateContactCommand command, CancellationToken cancellationToken)
    {
        var contact = await contacts.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (contact is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (command.AccountId is { } accountId && await accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var owner = await owners.ResolveAsync(command.OwnerUserId, contact.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        contact.Update(
            command.FirstName,
            command.LastName,
            owner.Value,
            command.AccountId,
            command.Email,
            command.Phone,
            command.Mobile,
            command.Title,
            command.MailingAddress.ToDomain());
        return Result.Success();
    }
}

[RequiresPermission(SalesPermissions.ContactsWrite)]
public sealed record DeleteContactCommand(Guid Id) : ICommand;

public sealed class DeleteContactHandler(IContactRepository contacts) : ICommandHandler<DeleteContactCommand>
{
    public async Task<Result> Handle(DeleteContactCommand command, CancellationToken cancellationToken)
    {
        var contact = await contacts.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (contact is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        contacts.Remove(contact);
        return Result.Success();
    }
}
