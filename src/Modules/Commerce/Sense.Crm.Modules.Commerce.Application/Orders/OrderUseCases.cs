using FluentValidation;
using Sense.Crm.Modules.Commerce.Application.Invoices;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application.Orders;

/// <summary>
/// Liste: filtreler <c>status, accountId, contactId, dealId, quoteId, ownerUserId, orderFrom, orderTo</c> (<c>orderDate</c>, uçlar dahil)
/// + <c>q</c> (numara + konu).
/// </summary>
[RequiresPermission(CommercePermissions.OrdersRead)]
public sealed record ListOrdersQuery(PagedQuery Paging, OrderFilter Filter) : IQuery<PagedResult<OrderSummaryDto>>;

public sealed class ListOrdersHandler(IOrderReadStore store) : IQueryHandler<ListOrdersQuery, PagedResult<OrderSummaryDto>>
{
    public async Task<Result<PagedResult<OrderSummaryDto>>> Handle(ListOrdersQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, query.Filter, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(CommercePermissions.OrdersRead)]
public sealed record GetOrderQuery(Guid Id) : IQuery<OrderDto>;

public sealed class GetOrderHandler(IOrderReadStore store) : IQueryHandler<GetOrderQuery, OrderDto>
{
    public async Task<Result<OrderDto>> Handle(GetOrderQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } order
            ? order
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>
/// Doğrudan sipariş (<c>draft</c>; numara burada atanır; <c>orderDate</c> verilmezse bugün — kiracı saati). Kurallar teklifle aynıdır
/// (<c>validUntil</c> yok). <c>SalesOrderCreated</c> (<c>direct</c>) olayı aynı transaction'da outbox'a yazılır.
/// </summary>
[RequiresPermission(CommercePermissions.OrdersWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateOrderCommand(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? OrderDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    DateOnly? DueDate,
    string? CustomerPoNumber,
    decimal? ExciseTax,
    decimal? SalesCommission,
    string? Pending,
    IReadOnlyList<LineRequest>? Lines) : ICommand<OrderDto>, IDocumentFields, IOrderExtraFields;

/// <summary>Siparişe özgü M9C alanları: müşteri satın alma emri no, gider vergisi, satış komisyonu (bilgi amaçlı), bekliyor, son tarih.</summary>
public interface IOrderExtraFields
{
    DateOnly? DueDate { get; }

    string? CustomerPoNumber { get; }

    decimal? ExciseTax { get; }

    decimal? SalesCommission { get; }

    string? Pending { get; }
}

public static class OrderExtraRules
{
    public static void AddTo<T>(AbstractValidator<T> validator)
        where T : IOrderExtraFields
    {
        validator.RuleFor(x => x.CustomerPoNumber).MaximumLength(CommerceLimits.CustomerPoNumberMaxLength);
        validator.RuleFor(x => x.ExciseTax).OptionalAmount(CommerceLimits.MaxAdjustment, CommerceLimits.AmountScale);
        validator.RuleFor(x => x.SalesCommission).OptionalAmount(CommerceLimits.MaxAdjustment, CommerceLimits.AmountScale);
        validator.RuleFor(x => x.Pending).MaximumLength(CommerceLimits.PendingMaxLength);
    }

    public static OrderExtras ToExtras(this IOrderExtraFields f) => new(f.CustomerPoNumber, f.DueDate, f.ExciseTax, f.SalesCommission, f.Pending);
}

public sealed class CreateOrderValidator : DocumentFieldsValidator<CreateOrderCommand>
{
    public CreateOrderValidator() => OrderExtraRules.AddTo(this);
}

/// <summary>Yanıt sipariş detayıdır (yazma izni yeterli; <c>crm.orders.read</c> gerekmez).</summary>
public sealed class CreateOrderHandler(
    ISalesOrderRepository orders,
    IOrderReadStore store,
    SalesDocumentPreparer preparer,
    DocumentNumbers numbers,
    ICommerceTransaction transaction,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user,
    CommerceClock clock) : ICommandHandler<CreateOrderCommand, OrderDto>
{
    public async Task<Result<OrderDto>> Handle(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var created = await CreateAsync(command, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        return await store.GetAsync(created.Value, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }

    private Task<Result<Guid>> CreateAsync(CreateOrderCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                var prepared = await preparer.PrepareAsync(command, existing: null, new HashSet<Guid>(), ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return prepared.Error;
                }

                var orderDate = command.OrderDate ?? await clock.TodayAsync(ct).ConfigureAwait(false);
                if (command.DueDate is { } due && due < orderDate)
                {
                    // Numara ayrılmadan önce (domain aynı kuralı yeniden uygular).
                    throw new ValidationException([new FluentValidation.Results.ValidationFailure("DueDate", CommerceErrors.DueBeforeOrder)]);
                }

                var number = await numbers.NextAsync(DocumentKinds.Order, ct).ConfigureAwait(false);
                var header = SalesDocumentPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var created = SalesOrder.Create(tenant.TenantId, number, header, orderDate, quoteId: null, prepared.Value.Lines, command.ToExtras()).ThrowIfFieldError();
                if (created.IsFailure)
                {
                    return created.Error;
                }

                var order = created.Value;
                orders.Add(order);
                outbox.Enqueue(new SalesOrderCreated(
                    tenant.TenantId, order.Id, order.Number, order.AccountId, order.DealId, null, order.GrandTotal, order.Currency, SalesOrderSources.Direct, user.UserId));
                return order.Id;
            },
            cancellationToken);
}

/// <summary>
/// Tam değiştirme (PUT, yalnız <c>draft</c>): <c>quoteId</c> değişmez; <c>orderDate</c> verilmezse mevcut korunur;
/// <c>ownerUserId</c> verilmezse mevcut korunur.
/// </summary>
[RequiresPermission(CommercePermissions.OrdersWrite)]
public sealed record UpdateOrderCommand(
    Guid Id,
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? OrderDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    DateOnly? DueDate,
    string? CustomerPoNumber,
    decimal? ExciseTax,
    decimal? SalesCommission,
    string? Pending,
    IReadOnlyList<LineRequest>? Lines) : ICommand, IDocumentFields, IOrderExtraFields;

public sealed class UpdateOrderValidator : DocumentFieldsValidator<UpdateOrderCommand>
{
    public UpdateOrderValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        OrderExtraRules.AddTo(this);
    }
}

public sealed class UpdateOrderHandler(
    ISalesOrderRepository orders,
    SalesDocumentPreparer preparer,
    ICommerceTransaction transaction) : ICommandHandler<UpdateOrderCommand>
{
    public Task<Result> Handle(UpdateOrderCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var order = await orders.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
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

                var header = SalesDocumentPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var updated = order.Update(header, command.OrderDate ?? order.OrderDate, prepared.Value.Lines, command.ToExtras()).ThrowIfFieldError();
                if (updated.IsFailure)
                {
                    return updated;
                }

                orders.Touch(order);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Yumuşak silme (yalnız <c>draft</c>; tekliften gelen taslak silinirse teklif yeniden dönüştürülebilir).</summary>
[RequiresPermission(CommercePermissions.OrdersWrite)]
public sealed record DeleteOrderCommand(Guid Id) : ICommand;

public sealed class DeleteOrderHandler(ISalesOrderRepository orders, ICommerceTransaction transaction) : ICommandHandler<DeleteOrderCommand>
{
    public Task<Result> Handle(DeleteOrderCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var order = await orders.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var editable = order.EnsureEditable();
                if (editable.IsFailure)
                {
                    return editable;
                }

                orders.Remove(order);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Durum geçişi yürütücüsü (bkz. <c>QuoteTransitions</c>): xmin çakışması → <c>commerce.concurrent_update</c> 409.</summary>
public sealed class OrderTransitions(ISalesOrderRepository orders, ICommerceTransaction transaction, CommerceClock clock)
{
    public Task<Result> RunAsync(Guid id, Func<SalesOrder, DateTime, Result> transition, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var order = await orders.GetByIdAsync(id, ct).ConfigureAwait(false);
                return order is null ? Error.NotFound(ErrorCodes.NotFound) : transition(order, clock.NowUtc);
            },
            cancellationToken);
}

/// <summary><c>draft → confirmed</c>: ≥1 kalem (<c>order.no_lines</c> 422).</summary>
[RequiresPermission(CommercePermissions.OrdersWrite)]
public sealed record ConfirmOrderCommand(Guid Id) : ICommand;

public sealed class ConfirmOrderHandler(OrderTransitions transitions) : ICommandHandler<ConfirmOrderCommand>
{
    public Task<Result> Handle(ConfirmOrderCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (order, _) => order.Confirm(), cancellationToken);
}

/// <summary><c>confirmed → fulfilled</c> (stok/sevkiyat yok).</summary>
[RequiresPermission(CommercePermissions.OrdersWrite)]
public sealed record FulfillOrderCommand(Guid Id) : ICommand;

public sealed class FulfillOrderHandler(OrderTransitions transitions) : ICommandHandler<FulfillOrderCommand>
{
    public Task<Result> Handle(FulfillOrderCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (order, now) => order.Fulfill(now), cancellationToken);
}

/// <summary><c>draft | confirmed → cancelled</c>; <c>reason</c> ≤ 1000.</summary>
[RequiresPermission(CommercePermissions.OrdersWrite)]
public sealed record CancelOrderCommand(Guid Id, string? Reason) : ICommand;

public sealed class CancelOrderValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderValidator() => RuleFor(x => x.Reason).MaximumLength(CommerceLimits.ReasonMaxLength);
}

/// <summary>Aktif (iptal edilmemiş, silinmemiş) faturası olan sipariş iptal edilemez: <c>order.has_active_invoice</c> 409 (önce fatura iptal edilir).</summary>
public sealed class CancelOrderHandler(ISalesOrderRepository orders, IInvoiceRepository invoices, ICommerceTransaction transaction, CommerceClock clock) : ICommandHandler<CancelOrderCommand>
{
    public Task<Result> Handle(CancelOrderCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                // Sipariş → fatura dönüşümüyle aynı kilit: iptal ile eşzamanlı dönüşüm birbirini görür (fatura yalnız iptal edilmemiş sipariş için).
                await transaction.LockAsync(OrderInvoiceLock.KeyFor(command.Id), ct).ConfigureAwait(false);
                var order = await orders.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var hasActiveInvoice = await invoices.ExistsActiveForOrderAsync(order.Id, ct).ConfigureAwait(false);
                return order.Cancel(command.Reason, clock.NowUtc, hasActiveInvoice);
            },
            cancellationToken);
}
