using FluentValidation;
using FluentValidation.Results;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application.Invoices;

/// <summary>Sipariş → fatura dönüşümü ile sipariş iptalinin paylaştığı danışma kilidi anahtarı (bkz. <see cref="ICommerceTransaction.LockAsync"/>).</summary>
public static class OrderInvoiceLock
{
    public static string KeyFor(Guid orderId) => $"order-invoice:{orderId:D}";
}

/// <summary>
/// Liste: filtreler <c>status</c> (<b>etkin</b> durum: <c>draft|sent|partiallyPaid|paid|overdue|cancelled</c>), <c>accountId, contactId, dealId, orderId, ownerUserId,
/// invoiceFrom, invoiceTo, dueFrom, dueTo</c> (uçlar dahil) + <c>q</c> (numara + konu + <c>customerPoNumber</c>).
/// </summary>
[RequiresPermission(CommercePermissions.InvoicesRead)]
public sealed record ListInvoicesQuery(PagedQuery Paging, InvoiceFilter Filter) : IQuery<PagedResult<InvoiceSummaryDto>>;

public sealed class ListInvoicesHandler(IInvoiceReadStore store, CommerceClock clock) : IQueryHandler<ListInvoicesQuery, PagedResult<InvoiceSummaryDto>>
{
    public async Task<Result<PagedResult<InvoiceSummaryDto>>> Handle(ListInvoicesQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, query.Filter, await clock.TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(CommercePermissions.InvoicesRead)]
public sealed record GetInvoiceQuery(Guid Id) : IQuery<InvoiceDto>;

public sealed class GetInvoiceHandler(IInvoiceReadStore store, CommerceClock clock) : IQueryHandler<GetInvoiceQuery, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> Handle(GetInvoiceQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, await clock.TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is { } invoice
            ? invoice
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Faturaya özgü alanlar: müşteri satın alma emri no'su, gider vergisi, satış komisyonu (bilgi amaçlı).</summary>
public interface IInvoiceExtraFields
{
    string? CustomerPoNumber { get; }

    decimal? ExciseTax { get; }

    decimal? SalesCommission { get; }
}

public static class InvoiceExtraRules
{
    public static void AddTo<T>(AbstractValidator<T> validator)
        where T : IInvoiceExtraFields
    {
        validator.RuleFor(x => x.CustomerPoNumber).MaximumLength(CommerceLimits.CustomerPoNumberMaxLength);
        validator.RuleFor(x => x.ExciseTax).OptionalAmount(CommerceLimits.MaxAdjustment, CommerceLimits.AmountScale);
        validator.RuleFor(x => x.SalesCommission).OptionalAmount(CommerceLimits.MaxAdjustment, CommerceLimits.AmountScale);
    }

    public static InvoiceExtras ToExtras(this IInvoiceExtraFields f) => new(f.CustomerPoNumber, f.ExciseTax, f.SalesCommission);
}

/// <summary>
/// Doğrudan fatura (<c>draft</c>; numara <c>INV-{yıl}-{sıra}</c> burada atanır; <c>invoiceDate</c> verilmezse bugün — kiracı saati). Kurallar teklif/sipariş
/// ile aynıdır (sahip, bağlı kayıtlar, ürünler, fiyat çözümü, toplamlar sunucuda). <c>InvoiceCreated</c> (<c>direct</c>) olayı aynı transaction'da outbox'a yazılır.
/// <c>orderId</c> gövdede kabul edilmez (yalnız siparişten dönüşümle dolar).
/// </summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateInvoiceCommand(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    string? CustomerPoNumber,
    decimal? ExciseTax,
    decimal? SalesCommission,
    IReadOnlyList<LineRequest>? Lines) : ICommand<InvoiceDto>, IDocumentFields, IInvoiceExtraFields;

public sealed class CreateInvoiceValidator : DocumentFieldsValidator<CreateInvoiceCommand>
{
    public CreateInvoiceValidator() => InvoiceExtraRules.AddTo(this);
}

/// <summary>Yanıt fatura detayıdır (yazma izni yeterli; <c>crm.invoices.read</c> gerekmez).</summary>
public sealed class CreateInvoiceHandler(
    IInvoiceRepository invoices,
    IInvoiceReadStore store,
    SalesDocumentPreparer preparer,
    DocumentNumbers numbers,
    ICommerceTransaction transaction,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user,
    CommerceClock clock) : ICommandHandler<CreateInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> Handle(CreateInvoiceCommand command, CancellationToken cancellationToken)
    {
        var created = await CreateAsync(command, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var today = await clock.TodayAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetAsync(created.Value, today, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }

    private Task<Result<Guid>> CreateAsync(CreateInvoiceCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                var prepared = await preparer.PrepareAsync(command, existing: null, new HashSet<Guid>(), ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return prepared.Error;
                }

                var invoiceDate = command.InvoiceDate ?? await clock.TodayAsync(ct).ConfigureAwait(false);
                if (command.DueDate is { } due && due < invoiceDate)
                {
                    throw new ValidationException([new ValidationFailure("DueDate", CommerceErrors.DueBeforeInvoice)]);
                }

                var number = await numbers.NextAsync(DocumentKinds.Invoice, ct).ConfigureAwait(false);
                var header = SalesDocumentPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var created = Invoice.Create(tenant.TenantId, number, header, invoiceDate, command.DueDate, orderId: null, prepared.Value.Lines, command.ToExtras()).ThrowIfFieldError();
                if (created.IsFailure)
                {
                    return created.Error;
                }

                var invoice = created.Value;
                invoices.Add(invoice);
                outbox.Enqueue(new InvoiceCreated(
                    tenant.TenantId, invoice.Id, invoice.Number, invoice.AccountId, invoice.DealId, null, invoice.GrandTotal, invoice.Currency, InvoiceSources.Direct, user.UserId));
                return invoice.Id;
            },
            cancellationToken);
}

/// <summary>
/// Tam değiştirme (PUT, yalnız <c>draft</c>): numara/<c>orderId</c> değişmez; <c>invoiceDate</c> verilmezse mevcut korunur; <c>ownerUserId</c> verilmezse
/// mevcut korunur; gönderilmeyen isteğe bağlı alan temizlenir, <c>adjustment</c> gönderilmezse 0.
/// </summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
public sealed record UpdateInvoiceCommand(
    Guid Id,
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    string? CustomerPoNumber,
    decimal? ExciseTax,
    decimal? SalesCommission,
    IReadOnlyList<LineRequest>? Lines) : ICommand, IDocumentFields, IInvoiceExtraFields;

public sealed class UpdateInvoiceValidator : DocumentFieldsValidator<UpdateInvoiceCommand>
{
    public UpdateInvoiceValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        InvoiceExtraRules.AddTo(this);
    }
}

public sealed class UpdateInvoiceHandler(IInvoiceRepository invoices, SalesDocumentPreparer preparer, ICommerceTransaction transaction) : ICommandHandler<UpdateInvoiceCommand>
{
    public Task<Result> Handle(UpdateInvoiceCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var invoice = await invoices.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (invoice is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var editable = invoice.EnsureEditable();
                if (editable.IsFailure)
                {
                    return editable;
                }

                var existing = invoice.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToHashSet();
                var prepared = await preparer.PrepareAsync(command, invoice, existing, ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return prepared.Error;
                }

                var header = SalesDocumentPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var updated = invoice.Update(header, command.InvoiceDate ?? invoice.InvoiceDate, command.DueDate, prepared.Value.Lines, command.ToExtras()).ThrowIfFieldError();
                if (updated.IsFailure)
                {
                    return updated;
                }

                invoices.Touch(invoice);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Yumuşak silme (yalnız <c>draft</c>; aksi <c>invoice.not_editable</c> 409). Siparişten gelen taslak silinirse sipariş yeniden faturalanabilir.</summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
public sealed record DeleteInvoiceCommand(Guid Id) : ICommand;

public sealed class DeleteInvoiceHandler(IInvoiceRepository invoices, ICommerceTransaction transaction) : ICommandHandler<DeleteInvoiceCommand>
{
    public Task<Result> Handle(DeleteInvoiceCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var invoice = await invoices.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (invoice is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var editable = invoice.EnsureEditable();
                if (editable.IsFailure)
                {
                    return editable;
                }

                invoices.Remove(invoice);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>
/// Durum geçişi yürütücüsü (bkz. <c>QuoteTransitions</c>): faturayı yükler (yok → 404), kiracı "bugün"ü ve şimdiyi verip geçişi uygular, isteğe bağlı olay yazar.
/// Eşzamanlı çakışma (xmin) → <c>commerce.concurrent_update</c> 409.
/// </summary>
public sealed class InvoiceTransitions(IInvoiceRepository invoices, ICommerceTransaction transaction, CommerceClock clock)
{
    public Task<Result> RunAsync(Guid id, Func<Invoice, DateOnly, DateTime, Result> transition, CancellationToken cancellationToken, Action<Invoice>? onSuccess = null) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var invoice = await invoices.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (invoice is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var today = await clock.TodayAsync(ct).ConfigureAwait(false);
                var result = transition(invoice, today, clock.NowUtc).ThrowIfFieldError();
                if (result.IsSuccess)
                {
                    onSuccess?.Invoke(invoice);
                }

                return result;
            },
            cancellationToken);
}

/// <summary><c>draft → sent</c>: ≥1 kalem (<c>invoice.no_lines</c> 422). <c>InvoiceSent</c> olayı aynı transaction'da yazılır.</summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
public sealed record SendInvoiceCommand(Guid Id) : ICommand;

public sealed class SendInvoiceHandler(InvoiceTransitions transitions, IIntegrationEventOutbox outbox, ITenantContext tenant, ICurrentUser user) : ICommandHandler<SendInvoiceCommand>
{
    public Task<Result> Handle(SendInvoiceCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(
            command.Id,
            (invoice, _, now) => invoice.Send(now),
            cancellationToken,
            invoice => outbox.Enqueue(new InvoiceSent(tenant.TenantId, invoice.Id, invoice.Number, invoice.AccountId, invoice.GrandTotal, invoice.Currency, invoice.DueDate, user.UserId)));
}

/// <summary><c>sent → draft</c> (tahsilat yoksa; varsa <c>invoice.has_payments</c> 409).</summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
public sealed record RevertInvoiceCommand(Guid Id) : ICommand;

public sealed class RevertInvoiceHandler(InvoiceTransitions transitions) : ICommandHandler<RevertInvoiceCommand>
{
    public Task<Result> Handle(RevertInvoiceCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (invoice, _, _) => invoice.Revert(), cancellationToken);
}

/// <summary><c>draft | sent → cancelled</c> (tahsilat varsa <c>invoice.has_payments</c> 409); <c>reason</c> ≤ 1000. <c>InvoiceCancelled</c> olayı yazılır.</summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
public sealed record CancelInvoiceCommand(Guid Id, string? Reason) : ICommand;

public sealed class CancelInvoiceValidator : AbstractValidator<CancelInvoiceCommand>
{
    public CancelInvoiceValidator() => RuleFor(x => x.Reason).MaximumLength(CommerceLimits.ReasonMaxLength);
}

public sealed class CancelInvoiceHandler(InvoiceTransitions transitions, IIntegrationEventOutbox outbox, ITenantContext tenant, ICurrentUser user) : ICommandHandler<CancelInvoiceCommand>
{
    public Task<Result> Handle(CancelInvoiceCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(
            command.Id,
            (invoice, _, now) => invoice.Cancel(command.Reason, now),
            cancellationToken,
            invoice => outbox.Enqueue(new InvoiceCancelled(tenant.TenantId, invoice.Id, invoice.Number, invoice.OrderId, user.UserId)));
}

/// <summary>
/// Tahsilat kaydı (<c>crm.invoices.write</c>): yalnız etkin <c>sent | partiallyPaid | overdue</c> fatura; <c>amount</c> &gt; 0, ≤ 2 ondalık; <c>paidOn</c> verilmezse bugün,
/// ≤ bugün ve ≥ <c>invoiceDate</c>; <c>amount</c> &gt; bakiye → <c>invoice.payment_exceeds_balance</c> 422. Faturanın <c>paid_amount</c>'ı aynı transaction'da güncellenir;
/// bakiye 0'a inerse <c>InvoicePaid</c>. Eşzamanlı iki tahsilatta ikincisi <c>commerce.concurrent_update</c> 409 (aşırı ödeme asla oluşmaz).
/// </summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
public sealed record RecordInvoicePaymentCommand(
    Guid InvoiceId,
    decimal Amount,
    DateOnly? PaidOn,
    PaymentMethod? Method,
    string? Reference,
    string? Notes) : ICommand<InvoicePaymentDto>;

public sealed class RecordInvoicePaymentValidator : AbstractValidator<RecordInvoicePaymentCommand>
{
    public RecordInvoicePaymentValidator()
    {
        RuleFor(x => x.InvoiceId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0m).LessThanOrEqualTo(CommerceLimits.MaxDocumentTotal).MaxDecimals(CommerceLimits.AmountScale);
        RuleFor(x => x.Method).IsInEnum().When(x => x.Method is not null);
        RuleFor(x => x.Reference).MaximumLength(CommerceLimits.PaymentReferenceMaxLength);
        RuleFor(x => x.Notes).MaximumLength(CommerceLimits.PaymentNotesMaxLength);
    }
}

public sealed class RecordInvoicePaymentHandler(
    IInvoiceRepository invoices,
    ICommerceTransaction transaction,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user,
    CommerceClock clock) : ICommandHandler<RecordInvoicePaymentCommand, InvoicePaymentDto>
{
    public Task<Result<InvoicePaymentDto>> Handle(RecordInvoicePaymentCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync<InvoicePaymentDto>(
            async ct =>
            {
                if (user.UserId is not { } userId)
                {
                    return Error.Unauthorized(ErrorCodes.Unauthenticated);
                }

                var invoice = await invoices.GetByIdAsync(command.InvoiceId, ct).ConfigureAwait(false);
                if (invoice is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var today = await clock.TodayAsync(ct).ConfigureAwait(false);
                var recorded = invoice.RecordPayment(
                    command.Amount, command.PaidOn ?? today, command.Method, command.Reference, command.Notes, userId, today, clock.NowUtc).ThrowIfFieldError();
                if (recorded.IsFailure)
                {
                    return recorded.Error;
                }

                var payment = recorded.Value;
                if (invoice.BalanceAmount <= 0m)
                {
                    outbox.Enqueue(new InvoicePaid(tenant.TenantId, invoice.Id, invoice.Number, invoice.AccountId, invoice.GrandTotal, invoice.Currency, payment.PaidOn, userId));
                }

                return new InvoicePaymentDto(payment.Id, payment.Amount, payment.PaidOn, payment.Method, payment.Reference, payment.Notes, payment.RecordedByUserId, payment.RecordedAt);
            },
            cancellationToken);
}

/// <summary>Yanlış giriş düzeltmesi: tahsilatı siler (fatura iptal edilmemiş olmalı); <c>paid_amount</c> geri düşer ve etkin durum yeniden türetilir.</summary>
[RequiresPermission(CommercePermissions.InvoicesWrite)]
public sealed record DeleteInvoicePaymentCommand(Guid InvoiceId, Guid PaymentId) : ICommand;

public sealed class DeleteInvoicePaymentHandler(IInvoiceRepository invoices, ICommerceTransaction transaction) : ICommandHandler<DeleteInvoicePaymentCommand>
{
    public Task<Result> Handle(DeleteInvoicePaymentCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var invoice = await invoices.GetByIdAsync(command.InvoiceId, ct).ConfigureAwait(false);
                if (invoice is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var removed = invoice.RemovePayment(command.PaymentId, out var payment);
                if (removed.IsFailure)
                {
                    return removed;
                }

                return payment is null ? Error.NotFound(ErrorCodes.NotFound) : Result.Success();
            },
            cancellationToken);
}

/// <summary>
/// Sipariş → fatura dönüşümü (tek transaction; izin <c>crm.orders.read</c> + <c>crm.invoices.write</c>). Sipariş <c>confirmed</c> veya <c>fulfilled</c> olmalı
/// (<c>order.not_invoiceable</c> 409), iptal edilmemiş fatura olmamalı (<c>order.already_invoiced</c> 409); firma/kişi/fırsat yeniden doğrulanır.
/// Numara sayacı, fatura (<c>draft</c>; başlık alanları, adresler, yuvarlama, fiyat listesi, müşteri PO no, gider vergisi, komisyon kopyalanır — <c>pending</c> ve
/// sipariş <c>dueDate</c>'i kopyalanmaz), kalemler (yeni kimlikler, tutarlar yeniden hesaplanır → sipariş toplamı birebir) ve <c>InvoiceCreated</c> (<c>order</c>)
/// olayı aynı transaction'dadır. Çift dönüşüm engeli: danışma kilidi (deterministik <c>order.already_invoiced</c>) + <c>ux_invoices_tenant_order</c> kısmi benzersiz
/// indeksi (yedek). Fatura siparişin durumunu <b>değiştirmez</b>. Yanıt fatura detayıdır (<c>crm.invoices.read</c> gerekmez).
/// </summary>
[RequiresPermission(CommercePermissions.OrdersRead)]
[RequiresPermission(CommercePermissions.InvoicesWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record ConvertOrderToInvoiceCommand(Guid OrderId, DateOnly? InvoiceDate, DateOnly? DueDate) : ICommand<InvoiceDto>;

public sealed class ConvertOrderToInvoiceHandler(
    ISalesOrderRepository orders,
    IInvoiceRepository invoices,
    IInvoiceReadStore store,
    RelatedRecordVerifier related,
    DocumentNumbers numbers,
    ICommerceTransaction transaction,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user,
    CommerceClock clock) : ICommandHandler<ConvertOrderToInvoiceCommand, InvoiceDto>
{
    public async Task<Result<InvoiceDto>> Handle(ConvertOrderToInvoiceCommand command, CancellationToken cancellationToken)
    {
        var converted = await transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                await transaction.LockAsync(OrderInvoiceLock.KeyFor(command.OrderId), ct).ConfigureAwait(false);
                var order = await orders.GetByIdAsync(command.OrderId, ct).ConfigureAwait(false);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var invoiceable = order.EnsureInvoiceable();
                if (invoiceable.IsFailure)
                {
                    return invoiceable.Error;
                }

                if (await invoices.ExistsActiveForOrderAsync(order.Id, ct).ConfigureAwait(false))
                {
                    return Error.Conflict(CommerceErrors.OrderAlreadyInvoiced);
                }

                var relatedCheck = await related.VerifyAsync(order.AccountId, order.ContactId, order.DealId, ct).ConfigureAwait(false);
                if (relatedCheck.IsFailure)
                {
                    return relatedCheck.Error;
                }

                var invoiceDate = command.InvoiceDate ?? await clock.TodayAsync(ct).ConfigureAwait(false);
                if (command.DueDate is { } due && due < invoiceDate)
                {
                    throw new ValidationException([new ValidationFailure("DueDate", CommerceErrors.DueBeforeInvoice)]);
                }

                var number = await numbers.NextAsync(DocumentKinds.Invoice, ct).ConfigureAwait(false);
                var header = new DocumentHeader(
                    order.Subject,
                    order.AccountId,
                    order.ContactId,
                    order.DealId,
                    order.OwnerUserId,
                    order.Currency,
                    order.Terms,
                    order.Notes,
                    order.Carrier,
                    order.Adjustment,
                    order.BillingAddress,
                    order.ShippingAddress,
                    order.PriceBookId);
                var extras = new InvoiceExtras(order.CustomerPoNumber, order.ExciseTax, order.SalesCommission);
                var created = Invoice.Create(
                    tenant.TenantId,
                    number,
                    header,
                    invoiceDate,
                    command.DueDate,
                    order.Id,
                    order.Lines.OrderBy(l => l.Position).Select(l => l.ToInput()).ToList(),
                    extras).ThrowIfFieldError();
                if (created.IsFailure)
                {
                    return created.Error;
                }

                var invoice = created.Value;
                invoices.Add(invoice);
                outbox.Enqueue(new InvoiceCreated(
                    tenant.TenantId, invoice.Id, invoice.Number, invoice.AccountId, invoice.DealId, order.Id, invoice.GrandTotal, invoice.Currency, InvoiceSources.Order, user.UserId));
                return invoice.Id;
            },
            cancellationToken).ConfigureAwait(false);
        if (converted.IsFailure)
        {
            return converted.Error;
        }

        var today = await clock.TodayAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetAsync(converted.Value, today, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }
}
