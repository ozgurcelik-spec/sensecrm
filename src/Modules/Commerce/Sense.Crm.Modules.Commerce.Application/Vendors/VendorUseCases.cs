using FluentValidation;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Vendors;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application.Vendors;

/// <summary>Liste: filtreler <c>category, ownerUserId, emailOptOut</c> + <c>q</c> (ad + e-posta + telefon + kategori). Arama penceresi de bu ucu kullanır.</summary>
[RequiresPermission(CommercePermissions.VendorsRead)]
public sealed record ListVendorsQuery(PagedQuery Paging, VendorFilter Filter) : IQuery<PagedResult<VendorDto>>;

public sealed class ListVendorsHandler(IVendorReadStore store) : IQueryHandler<ListVendorsQuery, PagedResult<VendorDto>>
{
    public async Task<Result<PagedResult<VendorDto>>> Handle(ListVendorsQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, query.Filter, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(CommercePermissions.VendorsRead)]
public sealed record GetVendorQuery(Guid Id) : IQuery<VendorDto>;

public sealed class GetVendorHandler(IVendorReadStore store) : IQueryHandler<GetVendorQuery, VendorDto>
{
    public async Task<Result<VendorDto>> Handle(GetVendorQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } vendor ? vendor : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Tedarikçi silme ile satın alma emri yazımının paylaştığı danışma kilidi anahtarı.</summary>
public static class VendorLock
{
    public static string KeyFor(Guid vendorId) => $"vendor:{vendorId:D}";
}

/// <summary>Tedarikçi alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IVendorFields
{
    string Name { get; }

    Guid? OwnerUserId { get; }

    string? Phone { get; }

    string? Email { get; }

    string? Website { get; }

    string? Category { get; }

    string? GlAccount { get; }

    DocumentAddressDto? Address { get; }

    string? Description { get; }
}

public abstract class VendorFieldsValidator<T> : AbstractValidator<T>
    where T : IVendorFields
{
    protected VendorFieldsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CommerceLimits.NameMaxLength);
        RuleFor(x => x.OwnerUserId).NotEqual(Guid.Empty).When(x => x.OwnerUserId is not null);
        RuleFor(x => x.Phone).MaximumLength(CommerceLimits.VendorPhoneMaxLength);
        RuleFor(x => x.Email).OptionalEmail(CommerceLimits.VendorEmailMaxLength);
        RuleFor(x => x.Website).OptionalHttpUrl(CommerceLimits.VendorWebsiteMaxLength);
        RuleFor(x => x.Category).MaximumLength(CommerceLimits.VendorTextMaxLength);
        RuleFor(x => x.GlAccount).MaximumLength(CommerceLimits.VendorTextMaxLength);
        RuleFor(x => x.Address!).SetValidator(new DocumentAddressValidator()).When(x => x.Address is not null);
        RuleFor(x => x.Description).MaximumLength(CommerceLimits.VendorDescriptionMaxLength);
    }
}

/// <summary>Yeni tedarikçi. Sahip verilmezse çağıran; <c>emailOptOut</c> verilmezse false (v1'de yalnız saklanır). Yanıt tedarikçi detayıdır.</summary>
[RequiresPermission(CommercePermissions.VendorsWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateVendorCommand(
    string Name,
    Guid? OwnerUserId,
    string? Phone,
    string? Email,
    string? Website,
    string? Category,
    string? GlAccount,
    DocumentAddressDto? Address,
    string? Description,
    bool? EmailOptOut) : ICommand<VendorDto>, IVendorFields;

public sealed class CreateVendorValidator : VendorFieldsValidator<CreateVendorCommand>;

public sealed class CreateVendorHandler(IVendorRepository vendors, IVendorReadStore store, OwnerResolver owners, ICommerceTransaction transaction, ITenantContext tenant)
    : ICommandHandler<CreateVendorCommand, VendorDto>
{
    public async Task<Result<VendorDto>> Handle(CreateVendorCommand command, CancellationToken cancellationToken)
    {
        var created = await transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, ct).ConfigureAwait(false);
                if (owner.IsFailure)
                {
                    return owner.Error;
                }

                var vendor = Vendor.Create(
                    tenant.TenantId,
                    command.Name,
                    owner.Value,
                    command.Phone,
                    command.Email,
                    command.Website,
                    command.Category,
                    command.GlAccount,
                    command.Address.ToDomain(),
                    command.Description,
                    command.EmailOptOut ?? false);
                vendors.Add(vendor);
                return vendor.Id;
            },
            cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        return await store.GetAsync(created.Value, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }
}

/// <summary>Tam değiştirme (PUT): gönderilmeyen isteğe bağlı alan temizlenir; <c>emailOptOut</c> verilmezse mevcut korunur; <c>ownerUserId</c> verilmezse mevcut korunur.</summary>
[RequiresPermission(CommercePermissions.VendorsWrite)]
public sealed record UpdateVendorCommand(
    Guid Id,
    string Name,
    Guid? OwnerUserId,
    string? Phone,
    string? Email,
    string? Website,
    string? Category,
    string? GlAccount,
    DocumentAddressDto? Address,
    string? Description,
    bool? EmailOptOut) : ICommand, IVendorFields;

public sealed class UpdateVendorValidator : VendorFieldsValidator<UpdateVendorCommand>
{
    public UpdateVendorValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateVendorHandler(IVendorRepository vendors, OwnerResolver owners, ICommerceTransaction transaction) : ICommandHandler<UpdateVendorCommand>
{
    public Task<Result> Handle(UpdateVendorCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var vendor = await vendors.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (vendor is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var owner = await owners.ResolveAsync(command.OwnerUserId, vendor.OwnerUserId, ct).ConfigureAwait(false);
                if (owner.IsFailure)
                {
                    return owner.Error;
                }

                vendor.Update(
                    command.Name,
                    owner.Value,
                    command.Phone,
                    command.Email,
                    command.Website,
                    command.Category,
                    command.GlAccount,
                    command.Address.ToDomain(),
                    command.Description,
                    command.EmailOptOut);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>
/// Yumuşak silme. Silinmemiş bir satın alma emri tedarikçiye bağlıysa <c>vendor.in_use</c> 409; silinirse ürünlerin <c>vendorId</c>'si aynı transaction'da temizlenir.
/// </summary>
[RequiresPermission(CommercePermissions.VendorsWrite)]
public sealed record DeleteVendorCommand(Guid Id) : ICommand;

public sealed class DeleteVendorHandler(IVendorRepository vendors, IPurchaseOrderRepository purchaseOrders, IProductRepository products, ICommerceTransaction transaction)
    : ICommandHandler<DeleteVendorCommand>
{
    public Task<Result> Handle(DeleteVendorCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                // Satın alma emri yazımıyla aynı kilit: tedarikçi silinirken eşzamanlı yeni PO oluşturulamaz.
                await transaction.LockAsync(VendorLock.KeyFor(command.Id), ct).ConfigureAwait(false);
                var vendor = await vendors.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (vendor is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                if (await purchaseOrders.ExistsForVendorAsync(vendor.Id, ct).ConfigureAwait(false))
                {
                    return Error.Conflict(CommerceErrors.VendorInUse);
                }

                foreach (var product in await products.ListByVendorAsync(vendor.Id, ct).ConfigureAwait(false))
                {
                    product.ClearVendor();
                }

                vendors.Remove(vendor);
                return Result.Success();
            },
            cancellationToken);
}

