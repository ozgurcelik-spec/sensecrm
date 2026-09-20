using FluentValidation;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application.Products;

/// <summary>Liste: filtreler <c>isActive, currency, vendorId</c> + <c>q</c> (ad + kod + açıklama). Kalem seçici ve arama penceresi de bu ucu kullanır.</summary>
[RequiresPermission(CommercePermissions.ProductsRead)]
public sealed record ListProductsQuery(PagedQuery Paging, ProductFilter Filter) : IQuery<PagedResult<ProductDto>>;

public sealed class ListProductsHandler(IProductReadStore store) : IQueryHandler<ListProductsQuery, PagedResult<ProductDto>>
{
    public async Task<Result<PagedResult<ProductDto>>> Handle(ListProductsQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, query.Filter, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(CommercePermissions.ProductsRead)]
public sealed record GetProductQuery(Guid Id) : IQuery<ProductDto>;

public sealed class GetProductHandler(IProductReadStore store) : IQueryHandler<GetProductQuery, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(GetProductQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } product
            ? product
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Ürün alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IProductFields
{
    string Name { get; }

    string? Code { get; }

    string? Description { get; }

    decimal UnitPrice { get; }

    string? Currency { get; }

    decimal TaxRate { get; }

    string? Unit { get; }

    Guid? VendorId { get; }

    decimal? PurchasePrice { get; }
}

public abstract class ProductFieldsValidator<T> : AbstractValidator<T>
    where T : IProductFields
{
    protected ProductFieldsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CommerceLimits.NameMaxLength);
        RuleFor(x => x.Code).MaximumLength(CommerceLimits.CodeMaxLength);
        RuleFor(x => x.Description).MaximumLength(CommerceLimits.ProductDescriptionMaxLength);
        RuleFor(x => x.UnitPrice).Amount(CommerceLimits.MaxUnitPrice, CommerceLimits.UnitPriceScale);
        RuleFor(x => x.Currency).OptionalCurrency();
        RuleFor(x => x.TaxRate).Percent();
        RuleFor(x => x.Unit).MaximumLength(CommerceLimits.UnitMaxLength);
        RuleFor(x => x.VendorId).NotEqual(Guid.Empty).When(x => x.VendorId is not null);
        RuleFor(x => x.PurchasePrice).OptionalAmount(CommerceLimits.MaxUnitPrice, CommerceLimits.UnitPriceScale);
    }
}

/// <summary>
/// Yeni ürün. Para birimi verilmezse <c>TRY</c>, KDV varsayılanı 0 (web formu 20 önerir), <c>isActive</c> varsayılanı true.
/// <c>vendorId</c> (birincil tedarikçi; kiracıda var olmalı, aksi 404 <c>commerce.related_not_found</c>) ve <c>purchasePrice</c> (ürün para biriminde) isteğe bağlıdır.
/// </summary>
[RequiresPermission(CommercePermissions.ProductsWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateProductCommand(
    string Name,
    string? Code,
    string? Description,
    decimal UnitPrice,
    string? Currency,
    decimal TaxRate,
    string? Unit,
    bool? IsActive,
    Guid? VendorId = null,
    decimal? PurchasePrice = null) : ICommand<ProductDto>, IProductFields;

public sealed class CreateProductValidator : ProductFieldsValidator<CreateProductCommand>;

/// <summary>Yanıt ürün detayıdır (yazma izni yeterli; <c>crm.products.read</c> gerekmez).</summary>
public sealed class CreateProductHandler(IProductRepository products, IVendorRepository vendors, IProductReadStore store, ICommerceTransaction transaction, ITenantContext tenant)
    : ICommandHandler<CreateProductCommand, ProductDto>
{
    public async Task<Result<ProductDto>> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var created = await transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                if (Product.NormalizeCode(command.Code) is { } code && await products.CodeExistsAsync(code, null, ct).ConfigureAwait(false))
                {
                    return Error.Conflict(CommerceErrors.ProductCodeTaken);
                }

                if (command.VendorId is { } vendorId && await vendors.GetByIdAsync(vendorId, ct).ConfigureAwait(false) is null)
                {
                    return Error.NotFound(CommerceErrors.RelatedNotFound);
                }

                var product = Product.Create(
                    tenant.TenantId,
                    command.Name,
                    command.Code,
                    command.Description,
                    command.UnitPrice,
                    command.Currency,
                    command.TaxRate,
                    command.Unit,
                    command.IsActive ?? true,
                    command.VendorId,
                    command.PurchasePrice);
                products.Add(product);
                return product.Id;
            },
            cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        return await store.GetAsync(created.Value, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }
}

/// <summary>Tam değiştirme (PUT): gönderilmeyen isteğe bağlı alan (<c>vendorId</c>, <c>purchasePrice</c> dahil) temizlenir; <c>isActive</c> verilmezse mevcut korunur.</summary>
[RequiresPermission(CommercePermissions.ProductsWrite)]
public sealed record UpdateProductCommand(
    Guid Id,
    string Name,
    string? Code,
    string? Description,
    decimal UnitPrice,
    string? Currency,
    decimal TaxRate,
    string? Unit,
    bool? IsActive,
    Guid? VendorId = null,
    decimal? PurchasePrice = null) : ICommand, IProductFields;

public sealed class UpdateProductValidator : ProductFieldsValidator<UpdateProductCommand>
{
    public UpdateProductValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateProductHandler(IProductRepository products, IVendorRepository vendors, ICommerceTransaction transaction) : ICommandHandler<UpdateProductCommand>
{
    public Task<Result> Handle(UpdateProductCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var product = await products.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (product is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                if (Product.NormalizeCode(command.Code) is { } code && await products.CodeExistsAsync(code, product.Id, ct).ConfigureAwait(false))
                {
                    return Error.Conflict(CommerceErrors.ProductCodeTaken);
                }

                // Tedarikçi yalnız değiştiyse yeniden doğrulanır (silinmiş tedarikçiye bağlı eski ürün düzenlenebilir kalır).
                if (command.VendorId is { } vendorId && vendorId != product.VendorId && await vendors.GetByIdAsync(vendorId, ct).ConfigureAwait(false) is null)
                {
                    return Error.NotFound(CommerceErrors.RelatedNotFound);
                }

                product.Update(
                    command.Name,
                    command.Code,
                    command.Description,
                    command.UnitPrice,
                    command.Currency,
                    command.TaxRate,
                    command.Unit,
                    command.IsActive ?? product.IsActive,
                    command.VendorId,
                    command.PurchasePrice);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>
/// Yumuşak silme: belgelerdeki kalemler anlık görüntü olduğundan silme her zaman serbesttir; kod yeniden kullanılabilir.
/// Ürünün fiyat listesi girdileri aynı transaction'da silinir.
/// </summary>
[RequiresPermission(CommercePermissions.ProductsWrite)]
public sealed record DeleteProductCommand(Guid Id) : ICommand;

public sealed class DeleteProductHandler(IProductRepository products, IPriceBookRepository priceBooks) : ICommandHandler<DeleteProductCommand>
{
    public async Task<Result> Handle(DeleteProductCommand command, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (product is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        products.Remove(product);
        await priceBooks.RemoveEntriesForProductAsync(product.Id, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
