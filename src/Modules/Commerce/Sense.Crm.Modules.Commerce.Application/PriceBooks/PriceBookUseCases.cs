using FluentValidation;
using FluentValidation.Results;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application.PriceBooks;

/// <summary>
/// Liste: filtreler <c>isActive, currency, effective</c> (türetilmiş etkinlik, kiracı "bugün"ü), <c>ownerUserId</c> + <c>q</c> (ad + açıklama).
/// Belge editörü ve arama penceresi (<c>effective=true&amp;currency=…</c>) bu ucu kullanır.
/// </summary>
[RequiresPermission(CommercePermissions.PriceBooksRead)]
public sealed record ListPriceBooksQuery(PagedQuery Paging, PriceBookFilter Filter) : IQuery<PagedResult<PriceBookDto>>;

public sealed class ListPriceBooksHandler(IPriceBookReadStore store, CommerceClock clock) : IQueryHandler<ListPriceBooksQuery, PagedResult<PriceBookDto>>
{
    public async Task<Result<PagedResult<PriceBookDto>>> Handle(ListPriceBooksQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, query.Filter, await clock.TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(CommercePermissions.PriceBooksRead)]
public sealed record GetPriceBookQuery(Guid Id) : IQuery<PriceBookDto>;

public sealed class GetPriceBookHandler(IPriceBookReadStore store, CommerceClock clock) : IQueryHandler<GetPriceBookQuery, PriceBookDto>
{
    public async Task<Result<PriceBookDto>> Handle(GetPriceBookQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, await clock.TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is { } book
            ? book
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Fiyat listesi alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IPriceBookFields
{
    string Name { get; }

    Guid? OwnerUserId { get; }

    PricingModel? PricingModel { get; }

    decimal? AdjustmentPercent { get; }

    string? Currency { get; }

    DateOnly? ValidFrom { get; }

    DateOnly? ValidTo { get; }

    string? Description { get; }
}

public abstract class PriceBookFieldsValidator<T> : AbstractValidator<T>
    where T : IPriceBookFields
{
    protected PriceBookFieldsValidator(bool modelRequired)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CommerceLimits.NameMaxLength);
        RuleFor(x => x.OwnerUserId).NotEqual(Guid.Empty).When(x => x.OwnerUserId is not null);
        RuleFor(x => x.Currency).OptionalCurrency();
        RuleFor(x => x.Description).MaximumLength(CommerceLimits.PriceBookDescriptionMaxLength);
        RuleFor(x => x.PricingModel).IsInEnum().When(x => x.PricingModel is not null);
        if (modelRequired)
        {
            RuleFor(x => x.PricingModel).NotNull();
        }

        // Yüzde yalnız "flat" modelde kullanılır ve o modelde zorunludur (perProduct'ta verilirse reddedilir).
        RuleFor(x => x.AdjustmentPercent)
            .NotNull().WithMessage(CommerceErrors.FlatPercentRequired)
            .When(x => x.PricingModel == Domain.PriceBooks.PricingModel.Flat);
        RuleFor(x => x.AdjustmentPercent)
            .Null().WithMessage(CommerceErrors.FlatPercentRequired)
            .When(x => x.PricingModel == Domain.PriceBooks.PricingModel.PerProduct);
        RuleFor(x => x.AdjustmentPercent)
            .InclusiveBetween(CommerceLimits.MinAdjustmentPercent, CommerceLimits.MaxAdjustmentPercent)
            .Must(value => value is null || decimal.Round(value.Value, CommerceLimits.PercentScale) == value.Value)
            .WithMessage(CommerceErrors.Decimals)
            .When(x => x.AdjustmentPercent is not null);
        RuleFor(x => x.ValidTo)
            .Must((fields, validTo) => validTo is null || fields.ValidFrom is null || validTo >= fields.ValidFrom)
            .WithMessage(CommerceErrors.ValidRange);
    }
}

/// <summary>
/// Yeni fiyat listesi: <c>name</c> kiracıda benzersiz (büyük/küçük harf duyarsız; <c>pricebook.name_taken</c> 409), <c>pricingModel</c> zorunlu
/// (<c>perProduct|flat</c>), <c>flat</c> için <c>adjustmentPercent</c> zorunlu; <c>currency</c> varsayılan <c>TRY</c>; <c>isActive</c> varsayılan true.
/// Model ve para birimi oluşturmadan sonra değişmez.
/// </summary>
[RequiresPermission(CommercePermissions.PriceBooksWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreatePriceBookCommand(
    string Name,
    Guid? OwnerUserId,
    bool? IsActive,
    PricingModel? PricingModel,
    decimal? AdjustmentPercent,
    string? Currency,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Description) : ICommand<PriceBookDto>, IPriceBookFields;

public sealed class CreatePriceBookValidator : PriceBookFieldsValidator<CreatePriceBookCommand>
{
    public CreatePriceBookValidator() : base(modelRequired: true)
    {
    }
}

public sealed class CreatePriceBookHandler(
    IPriceBookRepository books,
    IPriceBookReadStore store,
    OwnerResolver owners,
    ICommerceTransaction transaction,
    ITenantContext tenant,
    CommerceClock clock) : ICommandHandler<CreatePriceBookCommand, PriceBookDto>
{
    public async Task<Result<PriceBookDto>> Handle(CreatePriceBookCommand command, CancellationToken cancellationToken)
    {
        var created = await transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, ct).ConfigureAwait(false);
                if (owner.IsFailure)
                {
                    return owner.Error;
                }

                if (await books.NameExistsAsync(PriceBook.NormalizeName(command.Name), null, ct).ConfigureAwait(false))
                {
                    return Error.Conflict(CommerceErrors.PriceBookNameTaken);
                }

                var book = PriceBook.Create(
                    tenant.TenantId,
                    command.Name,
                    owner.Value,
                    command.IsActive ?? true,
                    command.PricingModel!.Value,
                    command.AdjustmentPercent,
                    command.Currency,
                    command.ValidFrom,
                    command.ValidTo,
                    command.Description);
                books.Add(book);
                return book.Id;
            },
            cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var today = await clock.TodayAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetAsync(created.Value, today, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }
}

/// <summary>
/// Tam değiştirme (PUT): <c>pricingModel</c>/<c>currency</c> farklı değerle gelirse <c>pricebook.model_immutable</c> 409 (<c>args.property</c>), gönderilmezse
/// değişmez sayılır; <c>isActive</c> verilmezse mevcut korunur; <c>ownerUserId</c> verilmezse mevcut korunur.
/// </summary>
[RequiresPermission(CommercePermissions.PriceBooksWrite)]
public sealed record UpdatePriceBookCommand(
    Guid Id,
    string Name,
    Guid? OwnerUserId,
    bool? IsActive,
    PricingModel? PricingModel,
    decimal? AdjustmentPercent,
    string? Currency,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Description) : ICommand, IPriceBookFields;

public sealed class UpdatePriceBookValidator : PriceBookFieldsValidator<UpdatePriceBookCommand>
{
    public UpdatePriceBookValidator() : base(modelRequired: false) => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdatePriceBookHandler(IPriceBookRepository books, OwnerResolver owners, ICommerceTransaction transaction) : ICommandHandler<UpdatePriceBookCommand>
{
    public Task<Result> Handle(UpdatePriceBookCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var book = await books.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (book is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var owner = await owners.ResolveAsync(command.OwnerUserId, book.OwnerUserId, ct).ConfigureAwait(false);
                if (owner.IsFailure)
                {
                    return owner.Error;
                }

                // Model değişmezliği yüzde/tür denetiminden önce: farklı model → 409 (model bu noktada mevcut modele göre doğrulanır).
                if (command.PricingModel is { } model && model != book.PricingModel)
                {
                    return Error.Conflict(CommerceErrors.PriceBookModelImmutable, ("property", "pricingModel"));
                }

                if (book.PricingModel == PricingModel.Flat && command.AdjustmentPercent is null)
                {
                    throw new ValidationException([new ValidationFailure("AdjustmentPercent", CommerceErrors.FlatPercentRequired)]);
                }

                if (book.PricingModel == PricingModel.PerProduct && command.AdjustmentPercent is not null)
                {
                    throw new ValidationException([new ValidationFailure("AdjustmentPercent", CommerceErrors.FlatPercentRequired)]);
                }

                var normalized = PriceBook.NormalizeName(command.Name);
                if (await books.NameExistsAsync(normalized, book.Id, ct).ConfigureAwait(false))
                {
                    return Error.Conflict(CommerceErrors.PriceBookNameTaken);
                }

                return book.Update(
                    command.Name,
                    owner.Value,
                    command.IsActive ?? book.IsActive,
                    command.PricingModel,
                    command.AdjustmentPercent,
                    command.Currency,
                    command.ValidFrom,
                    command.ValidTo,
                    command.Description);
            },
            cancellationToken);
}

/// <summary>Yumuşak silme: girdiler ve firma varsayılanları aynı transaction'da silinir; belgeler <c>priceBookId</c>'yi korur (ad boş döner).</summary>
[RequiresPermission(CommercePermissions.PriceBooksWrite)]
public sealed record DeletePriceBookCommand(Guid Id) : ICommand;

public sealed class DeletePriceBookHandler(IPriceBookRepository books, ICommerceTransaction transaction) : ICommandHandler<DeletePriceBookCommand>
{
    public Task<Result> Handle(DeletePriceBookCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var book = await books.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (book is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                await books.RemoveAsync(book, ct).ConfigureAwait(false);
                return Result.Success();
            },
            cancellationToken);
}

// ---- Girdiler (yalnız perProduct) ----------------------------------------------------------------------------------------

/// <summary><c>GET /pricebooks/{id}/entries</c>: <c>flat</c> listede <c>pricebook.model_mismatch</c> 409. <c>q</c>: ürün adı/kodu.</summary>
[RequiresPermission(CommercePermissions.PriceBooksRead)]
public sealed record ListPriceBookEntriesQuery(Guid PriceBookId, PagedQuery Paging) : IQuery<PagedResult<PriceBookEntryDto>>;

public sealed class ListPriceBookEntriesHandler(IPriceBookRepository books, IPriceBookReadStore store) : IQueryHandler<ListPriceBookEntriesQuery, PagedResult<PriceBookEntryDto>>
{
    public async Task<Result<PagedResult<PriceBookEntryDto>>> Handle(ListPriceBookEntriesQuery query, CancellationToken cancellationToken)
    {
        var book = await books.GetByIdAsync(query.PriceBookId, cancellationToken).ConfigureAwait(false);
        if (book is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (book.PricingModel != PricingModel.PerProduct)
        {
            return Error.Conflict(CommerceErrors.PriceBookModelMismatch);
        }

        return await store.ListEntriesAsync(book.Id, query.Paging, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Girdi ekle/güncelle (idempotent; <c>PUT /pricebooks/{id}/entries/{productId}</c>): ürün kiracıda var olmalı (<c>commerce.related_not_found</c>), para birimi liste
/// para birimiyle aynı olmalı (<c>errors.productId</c> = <c>validation.entry_currency_mismatch</c>; pasif ürün için girdi yazılabilir); liste başına en çok 2.000
/// girdi (<c>pricebook.entry_limit</c> 422); <c>flat</c> listede <c>pricebook.model_mismatch</c> 409. Fiyat değişimi denetlenir; başlığın <c>updatedAt</c>'i ilerler.
/// </summary>
[RequiresPermission(CommercePermissions.PriceBooksWrite)]
public sealed record UpsertPriceBookEntryCommand(Guid PriceBookId, Guid ProductId, decimal UnitPrice) : ICommand;

public sealed class UpsertPriceBookEntryValidator : AbstractValidator<UpsertPriceBookEntryCommand>
{
    public UpsertPriceBookEntryValidator()
    {
        RuleFor(x => x.PriceBookId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.UnitPrice).Amount(CommerceLimits.MaxUnitPrice, CommerceLimits.UnitPriceScale);
    }
}

public sealed class UpsertPriceBookEntryHandler(IPriceBookRepository books, IProductLookup products, ICommerceTransaction transaction, ITenantContext tenant)
    : ICommandHandler<UpsertPriceBookEntryCommand>
{
    public Task<Result> Handle(UpsertPriceBookEntryCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var book = await books.GetByIdAsync(command.PriceBookId, ct).ConfigureAwait(false);
                if (book is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                if (book.PricingModel != PricingModel.PerProduct)
                {
                    return Error.Conflict(CommerceErrors.PriceBookModelMismatch);
                }

                var found = await products.GetAsync([command.ProductId], ct).ConfigureAwait(false);
                if (!found.TryGetValue(command.ProductId, out var product))
                {
                    return Error.NotFound(CommerceErrors.RelatedNotFound);
                }

                if (!string.Equals(product.Currency, book.Currency, StringComparison.Ordinal))
                {
                    throw new ValidationException([new ValidationFailure("ProductId", CommerceErrors.EntryCurrencyMismatch)]);
                }

                var entry = await books.GetEntryAsync(book.Id, command.ProductId, ct).ConfigureAwait(false);
                if (entry is null)
                {
                    if (await books.CountEntriesAsync(book.Id, ct).ConfigureAwait(false) >= CommerceLimits.MaxPriceBookEntries)
                    {
                        return Error.Rule(CommerceErrors.PriceBookEntryLimit, ("max", CommerceLimits.MaxPriceBookEntries));
                    }

                    books.AddEntry(PriceBookEntry.Create(tenant.TenantId, book.Id, command.ProductId, command.UnitPrice));
                }
                else
                {
                    entry.SetPrice(command.UnitPrice);
                }

                books.Touch(book);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Girdiyi siler (idempotent: yoksa 204). <c>flat</c> listede <c>pricebook.model_mismatch</c> 409.</summary>
[RequiresPermission(CommercePermissions.PriceBooksWrite)]
public sealed record DeletePriceBookEntryCommand(Guid PriceBookId, Guid ProductId) : ICommand;

public sealed class DeletePriceBookEntryHandler(IPriceBookRepository books, ICommerceTransaction transaction) : ICommandHandler<DeletePriceBookEntryCommand>
{
    public Task<Result> Handle(DeletePriceBookEntryCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var book = await books.GetByIdAsync(command.PriceBookId, ct).ConfigureAwait(false);
                if (book is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                if (book.PricingModel != PricingModel.PerProduct)
                {
                    return Error.Conflict(CommerceErrors.PriceBookModelMismatch);
                }

                if (await books.GetEntryAsync(book.Id, command.ProductId, ct).ConfigureAwait(false) is { } entry)
                {
                    books.RemoveEntry(entry);
                    books.Touch(book);
                }

                return Result.Success();
            },
            cancellationToken);
}

// ---- Fiyat çözümü ------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>POST /pricebooks/{id}/resolve</c> (<c>crm.pricebooks.read</c>): 1–100 tekil ürün; yanıt istek sırasında, bulunamayan/silinmiş/başka kiracı ürün listede yok.
/// Liste etkin değilse <c>pricebook.not_effective</c> 409. Belge yazma yoluyla aynı <see cref="IPriceResolver"/>.
/// </summary>
[RequiresPermission(CommercePermissions.PriceBooksRead)]
public sealed record ResolvePricesQuery(Guid PriceBookId, IReadOnlyList<Guid>? ProductIds) : IQuery<ResolvePricesResponse>;

public sealed class ResolvePricesValidator : AbstractValidator<ResolvePricesQuery>
{
    public ResolvePricesValidator()
    {
        RuleFor(x => x.PriceBookId).NotEmpty();
        RuleFor(x => x.ProductIds).NotNull().Must(ids => ids is { Count: >= 1 and <= CommerceLimits.MaxResolveProducts }).WithMessage("validation.product_ids");
        RuleFor(x => x.ProductIds).Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("validation.product_ids");
        RuleForEach(x => x.ProductIds).NotEqual(Guid.Empty);
    }
}

public sealed class ResolvePricesHandler(IPriceBookRepository books, IProductLookup products, IPriceResolver resolver, CommerceClock clock) : IQueryHandler<ResolvePricesQuery, ResolvePricesResponse>
{
    public async Task<Result<ResolvePricesResponse>> Handle(ResolvePricesQuery query, CancellationToken cancellationToken)
    {
        var book = await books.GetByIdAsync(query.PriceBookId, cancellationToken).ConfigureAwait(false);
        if (book is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var today = await clock.TodayAsync(cancellationToken).ConfigureAwait(false);
        if (!book.IsEffective(today))
        {
            return Error.Conflict(CommerceErrors.PriceBookNotEffective);
        }

        var ids = query.ProductIds ?? [];
        var found = await products.GetAsync(ids, cancellationToken).ConfigureAwait(false);
        var resolved = await resolver.ResolveAsync(book, found, cancellationToken).ConfigureAwait(false);
        var items = ids.Where(resolved.ContainsKey).Select(id => new ResolvedPriceDto(id, resolved[id].UnitPrice, resolved[id].Source)).ToList();
        return new ResolvePricesResponse(items);
    }
}

// ---- Firma varsayılan listesi ---------------------------------------------------------------------------------------------

/// <summary>Firma varsayılanı okuma sonucu (<c>Default</c> yoksa uç 204 döner).</summary>
public sealed record AccountDefaultResult(AccountDefaultPriceBookDto? Default);

[RequiresPermission(CommercePermissions.PriceBooksRead)]
public sealed record GetAccountDefaultPriceBookQuery(Guid AccountId) : IQuery<AccountDefaultResult>;

public sealed class GetAccountDefaultPriceBookHandler(IRecordLookup records, IPriceBookReadStore store, CommerceClock clock) : IQueryHandler<GetAccountDefaultPriceBookQuery, AccountDefaultResult>
{
    public async Task<Result<AccountDefaultResult>> Handle(GetAccountDefaultPriceBookQuery query, CancellationToken cancellationToken)
    {
        if (!await records.ExistsAsync(RecordType.Account, query.AccountId, cancellationToken).ConfigureAwait(false))
        {
            return Error.NotFound(CommerceErrors.RelatedNotFound);
        }

        var today = await clock.TodayAsync(cancellationToken).ConfigureAwait(false);
        return new AccountDefaultResult(await store.GetAccountDefaultAsync(query.AccountId, today, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Firma varsayılan listesini ayarlar (<c>PUT /pricebooks/accounts/{accountId}/default</c>): firma yok/silinmiş/başka kiracı → <c>commerce.related_not_found</c> 404; liste yok → 404
/// <c>not_found</c>. Sunucu belgeye otomatik uygulamaz (yalnız web öneri olarak kullanır).
/// </summary>
[RequiresPermission(CommercePermissions.PriceBooksWrite)]
public sealed record SetAccountDefaultPriceBookCommand(Guid AccountId, Guid PriceBookId) : ICommand;

public sealed class SetAccountDefaultPriceBookValidator : AbstractValidator<SetAccountDefaultPriceBookCommand>
{
    public SetAccountDefaultPriceBookValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.PriceBookId).NotEmpty();
    }
}

public sealed class SetAccountDefaultPriceBookHandler(IRecordLookup records, IPriceBookRepository books, ICommerceTransaction transaction, ITenantContext tenant)
    : ICommandHandler<SetAccountDefaultPriceBookCommand>
{
    public Task<Result> Handle(SetAccountDefaultPriceBookCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                if (!await records.ExistsAsync(RecordType.Account, command.AccountId, ct).ConfigureAwait(false))
                {
                    return Error.NotFound(CommerceErrors.RelatedNotFound);
                }

                if (await books.GetByIdAsync(command.PriceBookId, ct).ConfigureAwait(false) is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                if (await books.GetAccountDefaultAsync(command.AccountId, ct).ConfigureAwait(false) is { } existing)
                {
                    existing.SetPriceBook(command.PriceBookId);
                }
                else
                {
                    books.AddAccountDefault(AccountPriceBook.Create(tenant.TenantId, command.AccountId, command.PriceBookId));
                }

                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Firma varsayılanını kaldırır (idempotent: yoksa 204).</summary>
[RequiresPermission(CommercePermissions.PriceBooksWrite)]
public sealed record ClearAccountDefaultPriceBookCommand(Guid AccountId) : ICommand;

public sealed class ClearAccountDefaultPriceBookHandler(IRecordLookup records, IPriceBookRepository books, ICommerceTransaction transaction) : ICommandHandler<ClearAccountDefaultPriceBookCommand>
{
    public Task<Result> Handle(ClearAccountDefaultPriceBookCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                if (!await records.ExistsAsync(RecordType.Account, command.AccountId, ct).ConfigureAwait(false))
                {
                    return Error.NotFound(CommerceErrors.RelatedNotFound);
                }

                if (await books.GetAccountDefaultAsync(command.AccountId, ct).ConfigureAwait(false) is { } existing)
                {
                    books.RemoveAccountDefault(existing);
                }

                return Result.Success();
            },
            cancellationToken);
}
