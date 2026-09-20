using FluentValidation;
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
    IReadOnlyList<LineRequest>? Lines) : ICommand<OrderDto>, IDocumentFields;

public sealed class CreateOrderValidator : DocumentFieldsValidator<CreateOrderCommand>;

/// <summary>Yanıt sipariş detayıdır (yazma izni yeterli; <c>crm.orders.read</c> gerekmez).</summary>
public sealed class CreateOrderHandler(
    ISalesOrderRepository orders,
    IOrderReadStore store,
    OwnerResolver owners,
    RelatedRecordVerifier related,
    LineProductVerifier products,
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
                var owner = await owners.ResolveAsync(command.OwnerUserId, current: null, ct).ConfigureAwait(false);
                if (owner.IsFailure)
                {
                    return owner.Error;
                }

                var relatedCheck = await related.VerifyAsync(command.AccountId, command.ContactId, command.DealId, ct).ConfigureAwait(false);
                if (relatedCheck.IsFailure)
                {
                    return relatedCheck.Error;
                }

                var lines = LineMapping.ToInputs(command.Lines);
                await products.VerifyAsync(command.Currency, lines, new HashSet<Guid>(), ct).ConfigureAwait(false);
                var fits = SalesDocument.EnsureTotalsFit(lines);
                if (fits.IsFailure)
                {
                    return fits.Error;
                }

                var orderDate = command.OrderDate ?? await clock.TodayAsync(ct).ConfigureAwait(false);
                var number = await numbers.NextAsync(DocumentKinds.Order, ct).ConfigureAwait(false);
                var header = new DocumentHeader(command.Subject, command.AccountId, command.ContactId, command.DealId, owner.Value, command.Currency, command.Terms, command.Notes);
                var created = SalesOrder.Create(tenant.TenantId, number, header, orderDate, quoteId: null, lines);
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
    IReadOnlyList<LineRequest>? Lines) : ICommand, IDocumentFields;

public sealed class UpdateOrderValidator : DocumentFieldsValidator<UpdateOrderCommand>
{
    public UpdateOrderValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateOrderHandler(
    ISalesOrderRepository orders,
    OwnerResolver owners,
    RelatedRecordVerifier related,
    LineProductVerifier products,
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

                var owner = await owners.ResolveAsync(command.OwnerUserId, order.OwnerUserId, ct).ConfigureAwait(false);
                if (owner.IsFailure)
                {
                    return owner.Error;
                }

                if (command.AccountId != order.AccountId || command.ContactId != order.ContactId || command.DealId != order.DealId)
                {
                    var relatedCheck = await related.VerifyAsync(command.AccountId, command.ContactId, command.DealId, ct).ConfigureAwait(false);
                    if (relatedCheck.IsFailure)
                    {
                        return relatedCheck;
                    }
                }

                var lines = LineMapping.ToInputs(command.Lines);
                var existing = order.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToHashSet();
                await products.VerifyAsync(command.Currency, lines, existing, ct).ConfigureAwait(false);

                var header = new DocumentHeader(command.Subject, command.AccountId, command.ContactId, command.DealId, owner.Value, command.Currency, command.Terms, command.Notes);
                var updated = order.Update(header, command.OrderDate ?? order.OrderDate, lines);
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

public sealed class CancelOrderHandler(OrderTransitions transitions) : ICommandHandler<CancelOrderCommand>
{
    public Task<Result> Handle(CancelOrderCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (order, now) => order.Cancel(command.Reason, now), cancellationToken);
}
