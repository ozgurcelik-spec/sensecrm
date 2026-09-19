using Crm.Modules.Sales.Contracts;
using Crm.Modules.Sales.Domain;
using Crm.Modules.Sales.Domain.Accounts;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Paging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Sales.Application.Accounts;

// ---------------------------------------------------------------------------------------------------------------------
// Sorgular
// ---------------------------------------------------------------------------------------------------------------------

[RequiresPermission(SalesPermissions.AccountsRead)]
public sealed record ListAccountsQuery(PagedQuery Paging, Guid? OwnerUserId, string? Industry) : IQuery<PagedResult<AccountDto>>;

public sealed class ListAccountsHandler(ISalesReadStore store) : IQueryHandler<ListAccountsQuery, PagedResult<AccountDto>>
{
    public async Task<Result<PagedResult<AccountDto>>> Handle(ListAccountsQuery query, CancellationToken cancellationToken) =>
        await store.ListAccountsAsync(query.Paging, query.OwnerUserId, query.Industry, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(SalesPermissions.AccountsRead)]
public sealed record GetAccountQuery(Guid Id) : IQuery<AccountDto>;

public sealed class GetAccountHandler(ISalesReadStore store) : IQueryHandler<GetAccountQuery, AccountDto>
{
    public async Task<Result<AccountDto>> Handle(GetAccountQuery query, CancellationToken cancellationToken) =>
        await store.GetAccountAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } account
            ? account
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Firmaya bağlı kişiler (sayfasız, en çok 200).</summary>
[RequiresPermission(SalesPermissions.AccountsRead)]
[RequiresPermission(SalesPermissions.ContactsRead)]
public sealed record ListAccountContactsQuery(Guid AccountId) : IQuery<IReadOnlyList<ContactDto>>;

public sealed class ListAccountContactsHandler(ISalesReadStore store) : IQueryHandler<ListAccountContactsQuery, IReadOnlyList<ContactDto>>
{
    public async Task<Result<IReadOnlyList<ContactDto>>> Handle(ListAccountContactsQuery query, CancellationToken cancellationToken)
    {
        if (await store.GetAccountAsync(query.AccountId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        return Result.Success(await store.ListAccountContactsAsync(query.AccountId, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Firmaya bağlı fırsatlar (sayfasız, en çok 200).</summary>
[RequiresPermission(SalesPermissions.AccountsRead)]
[RequiresPermission(SalesPermissions.DealsRead)]
public sealed record ListAccountDealsQuery(Guid AccountId) : IQuery<IReadOnlyList<DealDto>>;

public sealed class ListAccountDealsHandler(ISalesReadStore store) : IQueryHandler<ListAccountDealsQuery, IReadOnlyList<DealDto>>
{
    public async Task<Result<IReadOnlyList<DealDto>>> Handle(ListAccountDealsQuery query, CancellationToken cancellationToken)
    {
        if (await store.GetAccountAsync(query.AccountId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        return Result.Success(await store.ListAccountDealsAsync(query.AccountId, cancellationToken).ConfigureAwait(false));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Komutlar
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Firma alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IAccountFields
{
    string Name { get; }

    string? Industry { get; }

    string? Website { get; }

    string? Phone { get; }

    string? Email { get; }

    AddressDto? BillingAddress { get; }

    string? Description { get; }
}

public abstract class AccountFieldsValidator<T> : AbstractValidator<T>
    where T : IAccountFields
{
    protected AccountFieldsValidator()
    {
        RuleFor(x => x.Name).Required(SalesLimits.NameMaxLength);
        RuleFor(x => x.Industry).Optional(SalesLimits.IndustryMaxLength);
        RuleFor(x => x.Website).Optional(SalesLimits.WebsiteMaxLength);
        RuleFor(x => x.Phone).Optional(SalesLimits.PhoneMaxLength);
        RuleFor(x => x.Email).OptionalEmail();
        RuleFor(x => x.Description).Optional(SalesLimits.DescriptionMaxLength);
        RuleFor(x => x.BillingAddress).SetValidator(new AddressValidator()!);
    }
}

[RequiresPermission(SalesPermissions.AccountsWrite)]
public sealed record CreateAccountCommand(
    string Name,
    string? Industry,
    string? Website,
    string? Phone,
    string? Email,
    AddressDto? BillingAddress,
    string? Description,
    Guid? OwnerUserId) : ICommand<Guid>, IAccountFields;

public sealed class CreateAccountValidator : AccountFieldsValidator<CreateAccountCommand>;

public sealed class CreateAccountHandler(IAccountRepository accounts, OwnerResolver owners, ITenantContext tenant) : ICommandHandler<CreateAccountCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateAccountCommand command, CancellationToken cancellationToken)
    {
        var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        var account = Account.Create(
            tenant.TenantId,
            command.Name,
            owner.Value,
            command.Industry,
            command.Website,
            command.Phone,
            command.Email,
            command.BillingAddress.ToDomain(),
            command.Description);
        accounts.Add(account);
        return account.Id;
    }
}

[RequiresPermission(SalesPermissions.AccountsWrite)]
public sealed record UpdateAccountCommand(
    Guid Id,
    string Name,
    string? Industry,
    string? Website,
    string? Phone,
    string? Email,
    AddressDto? BillingAddress,
    string? Description,
    Guid? OwnerUserId) : ICommand, IAccountFields;

public sealed class UpdateAccountValidator : AccountFieldsValidator<UpdateAccountCommand>
{
    public UpdateAccountValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateAccountHandler(IAccountRepository accounts, OwnerResolver owners) : ICommandHandler<UpdateAccountCommand>
{
    public async Task<Result> Handle(UpdateAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var owner = await owners.ResolveAsync(command.OwnerUserId, account.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        account.Update(
            command.Name,
            owner.Value,
            command.Industry,
            command.Website,
            command.Phone,
            command.Email,
            command.BillingAddress.ToDomain(),
            command.Description);
        return Result.Success();
    }
}

/// <summary>Yumuşak silme. Bağlı kişi veya fırsat varsa <c>account.has_dependents</c> (409).</summary>
[RequiresPermission(SalesPermissions.AccountsWrite)]
public sealed record DeleteAccountCommand(Guid Id) : ICommand;

public sealed class DeleteAccountHandler(IAccountRepository accounts) : ICommandHandler<DeleteAccountCommand>
{
    public async Task<Result> Handle(DeleteAccountCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (await accounts.HasDependentsAsync(account.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(SalesErrors.AccountHasDependents);
        }

        accounts.Remove(account);
        return Result.Success();
    }
}
