using FluentValidation;
using FluentValidation.Results;
using Sense.Crm.Modules.Commerce.Application.Vendors;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application.PurchaseOrders;

/// <summary>
/// Liste: filtreler <c>status, vendorId, contactId, ownerUserId, poFrom, poTo</c> (<c>poDate</c>, uçlar dahil) + <c>q</c> (numara + konu).
/// </summary>
[RequiresPermission(CommercePermissions.PurchaseOrdersRead)]
public sealed record ListPurchaseOrdersQuery(PagedQuery Paging, PurchaseOrderFilter Filter) : IQuery<PagedResult<PurchaseOrderSummaryDto>>;

public sealed class ListPurchaseOrdersHandler(IPurchaseOrderReadStore store) : IQueryHandler<ListPurchaseOrdersQuery, PagedResult<PurchaseOrderSummaryDto>>
{
    public async Task<Result<PagedResult<PurchaseOrderSummaryDto>>> Handle(ListPurchaseOrdersQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, query.Filter, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(CommercePermissions.PurchaseOrdersRead)]
public sealed record GetPurchaseOrderQuery(Guid Id) : IQuery<PurchaseOrderDto>;

public sealed class GetPurchaseOrderHandler(IPurchaseOrderReadStore store) : IQueryHandler<GetPurchaseOrderQuery, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(GetPurchaseOrderQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } order ? order : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Satın alma emri alanları (oluşturma ve güncelleme ortak doğrulaması): ortak belge alanları + tedarikçi, son tarih, gider vergisi, satış komisyonu.</summary>
public interface IPurchaseOrderFields : ICommonDocumentFields
{
    Guid VendorId { get; }

    DateOnly? DueDate { get; }

    decimal? ExciseTax { get; }

    decimal? SalesCommission { get; }
}

public abstract class PurchaseOrderFieldsValidator<T> : CommonDocumentFieldsValidator<T>
    where T : IPurchaseOrderFields
{
    protected PurchaseOrderFieldsValidator()
    {
        RuleFor(x => x.VendorId).NotEmpty();
        RuleFor(x => x.ExciseTax).OptionalAmount(CommerceLimits.MaxAdjustment, CommerceLimits.AmountScale);
        RuleFor(x => x.SalesCommission).OptionalAmount(CommerceLimits.MaxAdjustment, CommerceLimits.AmountScale);
    }
}

/// <summary>Satın alma emri yazımının ortak ön hazırlığı: sahip, tedarikçi (kiracıda var olmalı), kişi, kalem fiyat çözümü (tedarikçi tarafı fiyat), toplam sınırı.</summary>
public sealed class PurchaseOrderPreparer(OwnerResolver owners, IVendorRepository vendors, RelatedRecordVerifier related, DocumentLinePricer pricer, ICommerceTransaction transaction)
{
    public async Task<Result<PreparedPurchaseOrder>> PrepareAsync(IPurchaseOrderFields fields, PurchaseOrder? existing, IReadOnlySet<Guid> existingProductIds, CancellationToken ct)
    {
        var owner = await owners.ResolveAsync(fields.OwnerUserId, existing?.OwnerUserId, ct).ConfigureAwait(false);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        if (existing is null || fields.VendorId != existing.VendorId)
        {
            // Tedarikçi silme ile aynı kilit: silinmekte olan tedarikçiye yeni PO bağlanamaz.
            await transaction.LockAsync(VendorLock.KeyFor(fields.VendorId), ct).ConfigureAwait(false);
            if (await vendors.GetByIdAsync(fields.VendorId, ct).ConfigureAwait(false) is null)
            {
                return Error.NotFound(CommerceErrors.RelatedNotFound);
            }
        }

        if (existing is null || fields.ContactId != existing.ContactId)
        {
            var contact = await related.VerifyContactAsync(fields.ContactId, ct).ConfigureAwait(false);
            if (contact.IsFailure)
            {
                return contact.Error;
            }
        }

        var lines = await pricer.PricePurchaseAsync(fields.Currency, fields.Lines, existingProductIds, ct).ConfigureAwait(false);
        var fits = CommerceDocument.EnsureTotalsFit(lines, fields.Adjustment).ThrowIfFieldError();
        if (fits.IsFailure)
        {
            return fits.Error;
        }

        return new PreparedPurchaseOrder(owner.Value, lines);
    }

    public static PurchaseOrderHeader HeaderFor(IPurchaseOrderFields f, Guid ownerUserId) =>
        new(f.Subject, f.VendorId, f.ContactId, ownerUserId, f.Currency, f.Terms, f.Notes, f.Carrier, f.Adjustment, f.BillingAddress.ToDomain(), f.ShippingAddress.ToDomain());

    public static PurchaseOrderExtras ExtrasFor(IPurchaseOrderFields f) => new(f.DueDate, f.ExciseTax, f.SalesCommission);
}

public sealed record PreparedPurchaseOrder(Guid OwnerUserId, IReadOnlyList<LineInput> Lines);

/// <summary>
/// Yeni satın alma emri (<c>draft</c>; numara <c>PO-{yıl}-{sıra}</c> burada atanır; <c>poDate</c> verilmezse bugün). Kalem <c>unitPrice</c> verilmezse
/// <c>product.purchasePrice</c> (yoksa <c>errors["lines[i].unitPrice"]</c> = <c>validation.line_price_unresolvable</c>). <c>PurchaseOrderCreated</c> olayı aynı transaction'dadır.
/// </summary>
[RequiresPermission(CommercePermissions.PurchaseOrdersWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreatePurchaseOrderCommand(
    string Subject,
    Guid VendorId,
    Guid? ContactId,
    DateOnly? PoDate,
    DateOnly? DueDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    decimal? ExciseTax,
    decimal? SalesCommission,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    IReadOnlyList<LineRequest>? Lines) : ICommand<PurchaseOrderDto>, IPurchaseOrderFields;

public sealed class CreatePurchaseOrderValidator : PurchaseOrderFieldsValidator<CreatePurchaseOrderCommand>;

public sealed class CreatePurchaseOrderHandler(
    IPurchaseOrderRepository purchaseOrders,
    IPurchaseOrderReadStore store,
    PurchaseOrderPreparer preparer,
    DocumentNumbers numbers,
    ICommerceTransaction transaction,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user,
    CommerceClock clock) : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> Handle(CreatePurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        var created = await transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                var prepared = await preparer.PrepareAsync(command, existing: null, new HashSet<Guid>(), ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return prepared.Error;
                }

                var poDate = command.PoDate ?? await clock.TodayAsync(ct).ConfigureAwait(false);
                if (command.DueDate is { } due && due < poDate)
                {
                    throw new ValidationException([new ValidationFailure("DueDate", CommerceErrors.DueBeforePo)]);
                }

                var number = await numbers.NextAsync(DocumentKinds.PurchaseOrder, ct).ConfigureAwait(false);
                var header = PurchaseOrderPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var result = PurchaseOrder.Create(tenant.TenantId, number, header, poDate, prepared.Value.Lines, PurchaseOrderPreparer.ExtrasFor(command)).ThrowIfFieldError();
                if (result.IsFailure)
                {
                    return result.Error;
                }

                var order = result.Value;
                purchaseOrders.Add(order);
                outbox.Enqueue(new PurchaseOrderCreated(tenant.TenantId, order.Id, order.Number, order.VendorId, order.GrandTotal, order.Currency, user.UserId));
                return order.Id;
            },
            cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        return await store.GetAsync(created.Value, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }
}

/// <summary>Tam değiştirme (PUT, yalnız <c>draft</c>): numara değişmez; <c>poDate</c> verilmezse mevcut korunur; <c>ownerUserId</c> verilmezse mevcut korunur.</summary>
[RequiresPermission(CommercePermissions.PurchaseOrdersWrite)]
public sealed record UpdatePurchaseOrderCommand(
    Guid Id,
    string Subject,
    Guid VendorId,
    Guid? ContactId,
    DateOnly? PoDate,
    DateOnly? DueDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    decimal? ExciseTax,
    decimal? SalesCommission,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    IReadOnlyList<LineRequest>? Lines) : ICommand, IPurchaseOrderFields;

public sealed class UpdatePurchaseOrderValidator : PurchaseOrderFieldsValidator<UpdatePurchaseOrderCommand>
{
    public UpdatePurchaseOrderValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdatePurchaseOrderHandler(IPurchaseOrderRepository purchaseOrders, PurchaseOrderPreparer preparer, ICommerceTransaction transaction)
    : ICommandHandler<UpdatePurchaseOrderCommand>
{
    public Task<Result> Handle(UpdatePurchaseOrderCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var order = await purchaseOrders.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var editable = order.EnsureEditable();
                if (editable.IsFailure)
                {
                    return editable;
                }

                var existing = order.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToHashSet();
                var prepared = await preparer.PrepareAsync(command, order, existing, ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return prepared.Error;
                }

                var header = PurchaseOrderPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var updated = order.Update(header, command.PoDate ?? order.PoDate, prepared.Value.Lines, PurchaseOrderPreparer.ExtrasFor(command)).ThrowIfFieldError();
                if (updated.IsFailure)
                {
                    return updated;
                }

                purchaseOrders.Touch(order);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Yumuşak silme (yalnız <c>draft</c>; aksi <c>purchase_order.not_editable</c> 409).</summary>
[RequiresPermission(CommercePermissions.PurchaseOrdersWrite)]
public sealed record DeletePurchaseOrderCommand(Guid Id) : ICommand;

public sealed class DeletePurchaseOrderHandler(IPurchaseOrderRepository purchaseOrders, ICommerceTransaction transaction) : ICommandHandler<DeletePurchaseOrderCommand>
{
    public Task<Result> Handle(DeletePurchaseOrderCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var order = await purchaseOrders.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var editable = order.EnsureEditable();
                if (editable.IsFailure)
                {
                    return editable;
                }

                purchaseOrders.Remove(order);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Durum geçişi yürütücüsü (bkz. <c>OrderTransitions</c>): xmin çakışması → <c>commerce.concurrent_update</c> 409.</summary>
public sealed class PurchaseOrderTransitions(IPurchaseOrderRepository purchaseOrders, ICommerceTransaction transaction, CommerceClock clock)
{
    public Task<Result> RunAsync(Guid id, Func<PurchaseOrder, DateTime, Result> transition, CancellationToken cancellationToken, Action<PurchaseOrder>? onSuccess = null) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var order = await purchaseOrders.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var result = transition(order, clock.NowUtc);
                if (result.IsSuccess)
                {
                    onSuccess?.Invoke(order);
                }

                return result;
            },
            cancellationToken);
}

/// <summary><c>draft → confirmed</c>: ≥1 kalem (<c>purchase_order.no_lines</c> 422).</summary>
[RequiresPermission(CommercePermissions.PurchaseOrdersWrite)]
public sealed record ConfirmPurchaseOrderCommand(Guid Id) : ICommand;

public sealed class ConfirmPurchaseOrderHandler(PurchaseOrderTransitions transitions) : ICommandHandler<ConfirmPurchaseOrderCommand>
{
    public Task<Result> Handle(ConfirmPurchaseOrderCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (order, now) => order.Confirm(now), cancellationToken);
}

/// <summary><c>confirmed → received</c> (stok yok). <c>PurchaseOrderReceived</c> olayı aynı transaction'da yazılır.</summary>
[RequiresPermission(CommercePermissions.PurchaseOrdersWrite)]
public sealed record ReceivePurchaseOrderCommand(Guid Id) : ICommand;

public sealed class ReceivePurchaseOrderHandler(PurchaseOrderTransitions transitions, IIntegrationEventOutbox outbox, ITenantContext tenant, ICurrentUser user)
    : ICommandHandler<ReceivePurchaseOrderCommand>
{
    public Task<Result> Handle(ReceivePurchaseOrderCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(
            command.Id,
            (order, now) => order.Receive(now),
            cancellationToken,
            order => outbox.Enqueue(new PurchaseOrderReceived(tenant.TenantId, order.Id, order.Number, order.VendorId, user.UserId)));
}

/// <summary><c>draft | confirmed → cancelled</c>; <c>reason</c> ≤ 1000.</summary>
[RequiresPermission(CommercePermissions.PurchaseOrdersWrite)]
public sealed record CancelPurchaseOrderCommand(Guid Id, string? Reason) : ICommand;

public sealed class CancelPurchaseOrderValidator : AbstractValidator<CancelPurchaseOrderCommand>
{
    public CancelPurchaseOrderValidator() => RuleFor(x => x.Reason).MaximumLength(CommerceLimits.ReasonMaxLength);
}

public sealed class CancelPurchaseOrderHandler(PurchaseOrderTransitions transitions) : ICommandHandler<CancelPurchaseOrderCommand>
{
    public Task<Result> Handle(CancelPurchaseOrderCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (order, now) => order.Cancel(command.Reason, now), cancellationToken);
}
