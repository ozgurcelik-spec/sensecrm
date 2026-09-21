using FluentValidation;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Application.Quotes;

/// <summary>
/// Liste: filtreler <c>status</c> (etkin durum: <c>sent</c> süresi dolmamışları döner, <c>expired</c> süresi dolmuşları),
/// <c>accountId, contactId, dealId, ownerUserId, validFrom, validTo, converted</c> + <c>q</c> (numara + konu).
/// </summary>
[RequiresPermission(CommercePermissions.QuotesRead)]
public sealed record ListQuotesQuery(PagedQuery Paging, QuoteFilter Filter) : IQuery<PagedResult<QuoteSummaryDto>>;

public sealed class ListQuotesHandler(IQuoteReadStore store, CommerceClock clock) : IQueryHandler<ListQuotesQuery, PagedResult<QuoteSummaryDto>>
{
    public async Task<Result<PagedResult<QuoteSummaryDto>>> Handle(ListQuotesQuery query, CancellationToken cancellationToken) =>
        await store.ListAsync(query.Paging, query.Filter, await clock.TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(CommercePermissions.QuotesRead)]
public sealed record GetQuoteQuery(Guid Id) : IQuery<QuoteDto>;

public sealed class GetQuoteHandler(IQuoteReadStore store, CommerceClock clock) : IQueryHandler<GetQuoteQuery, QuoteDto>
{
    public async Task<Result<QuoteDto>> Handle(GetQuoteQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, await clock.TodayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is { } quote
            ? quote
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>
/// Yeni teklif (her zaman <c>draft</c>; numara burada atanır). Sahip verilmezse çağıran; bağlı kayıtlar ve kalem ürünleri doğrulanır;
/// toplamlar sunucuda hesaplanır (gövdedeki hesaplanan alanlar yok sayılır).
/// </summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateQuoteCommand(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? ValidUntil,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    IReadOnlyList<LineRequest>? Lines) : ICommand<QuoteDto>, IDocumentFields;

public sealed class CreateQuoteValidator : DocumentFieldsValidator<CreateQuoteCommand>;

/// <summary>Yanıt teklif detayıdır (yazma izni yeterli; <c>crm.quotes.read</c> gerekmez).</summary>
public sealed class CreateQuoteHandler(
    IQuoteRepository quotes,
    IQuoteReadStore store,
    SalesDocumentPreparer preparer,
    DocumentNumbers numbers,
    ICommerceTransaction transaction,
    ITenantContext tenant,
    CommerceClock clock) : ICommandHandler<CreateQuoteCommand, QuoteDto>
{
    public async Task<Result<QuoteDto>> Handle(CreateQuoteCommand command, CancellationToken cancellationToken)
    {
        var created = await CreateAsync(command, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var today = await clock.TodayAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetAsync(created.Value, today, cancellationToken).ConfigureAwait(false) is { } dto ? dto : Error.NotFound(ErrorCodes.NotFound);
    }

    private Task<Result<Guid>> CreateAsync(CreateQuoteCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                var prepared = await preparer.PrepareAsync(command, existing: null, new HashSet<Guid>(), ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return prepared.Error;
                }

                // Numara en son ayrılır: sonrasında yalnız kayıt kalır (sayaç satır kilidi transaction sonuna dek tutulur).
                var number = await numbers.NextAsync(DocumentKinds.Quote, ct).ConfigureAwait(false);
                var header = SalesDocumentPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var created = Quote.Create(tenant.TenantId, number, header, command.ValidUntil, prepared.Value.Lines).ThrowIfFieldError();
                if (created.IsFailure)
                {
                    return created.Error;
                }

                quotes.Add(created.Value);
                return created.Value.Id;
            },
            cancellationToken);
}

/// <summary>
/// Tam değiştirme (PUT, yalnız <c>draft</c>): kalem kümesi tümden değişir, gönderilmeyen isteğe bağlı alan temizlenir,
/// <c>ownerUserId</c> verilmezse mevcut korunur; numara/durum değişmez. Bağlı kayıt ve sahip yalnız değiştiyse yeniden doğrulanır.
/// </summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record UpdateQuoteCommand(
    Guid Id,
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? ValidUntil,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier,
    decimal Adjustment,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    IReadOnlyList<LineRequest>? Lines) : ICommand, IDocumentFields;

public sealed class UpdateQuoteValidator : DocumentFieldsValidator<UpdateQuoteCommand>
{
    public UpdateQuoteValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateQuoteHandler(
    IQuoteRepository quotes,
    SalesDocumentPreparer preparer,
    ICommerceTransaction transaction) : ICommandHandler<UpdateQuoteCommand>
{
    public Task<Result> Handle(UpdateQuoteCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var quote = await quotes.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (quote is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var editable = quote.EnsureEditable();
                if (editable.IsFailure)
                {
                    return editable;
                }

                var existing = quote.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).ToHashSet();
                var prepared = await preparer.PrepareAsync(command, quote, existing, ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return prepared.Error;
                }

                var header = SalesDocumentPreparer.HeaderFor(command, prepared.Value.OwnerUserId);
                var updated = quote.Update(header, command.ValidUntil, prepared.Value.Lines).ThrowIfFieldError();
                if (updated.IsFailure)
                {
                    return updated;
                }

                quotes.Touch(quote);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>Yumuşak silme (yalnız <c>draft</c>; aksi <c>quote.not_editable</c> 409).</summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record DeleteQuoteCommand(Guid Id) : ICommand;

public sealed class DeleteQuoteHandler(IQuoteRepository quotes, ICommerceTransaction transaction) : ICommandHandler<DeleteQuoteCommand>
{
    public Task<Result> Handle(DeleteQuoteCommand command, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var quote = await quotes.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (quote is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var editable = quote.EnsureEditable();
                if (editable.IsFailure)
                {
                    return editable;
                }

                quotes.Remove(quote);
                return Result.Success();
            },
            cancellationToken);
}

/// <summary>
/// Durum geçişi yürütücüsü: teklifi yükler (yok → 404), kiracı "bugün"ü ve şimdiyi verip geçişi uygular, isteğe bağlı olay yazar
/// ve kaydeder. Eşzamanlı çakışma (xmin) → <c>commerce.concurrent_update</c> 409 (aynı teklifi iki kez kabul edip olayı çiftlemek engellenir).
/// </summary>
public sealed class QuoteTransitions(IQuoteRepository quotes, ICommerceTransaction transaction, CommerceClock clock)
{
    public Task<Result> RunAsync(Guid id, Func<Quote, DateOnly, DateTime, Result> transition, CancellationToken cancellationToken, Action<Quote>? onSuccess = null) =>
        transaction.ExecuteAsync(
            async ct =>
            {
                var quote = await quotes.GetByIdAsync(id, ct).ConfigureAwait(false);
                if (quote is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                var today = await clock.TodayAsync(ct).ConfigureAwait(false);
                var result = transition(quote, today, clock.NowUtc);
                if (result.IsSuccess)
                {
                    onSuccess?.Invoke(quote);
                }

                return result;
            },
            cancellationToken);
}

/// <summary><c>draft → sent</c>: ≥1 kalem (<c>quote.no_lines</c> 422); <c>validUntil</c> bugünden önce olamaz (<c>errors.validUntil</c>).</summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record SendQuoteCommand(Guid Id) : ICommand;

public sealed class SendQuoteHandler(QuoteTransitions transitions) : ICommandHandler<SendQuoteCommand>
{
    public Task<Result> Handle(SendQuoteCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (quote, today, now) => quote.Send(today, now).ThrowIfValidUntilPast(), cancellationToken);
}

/// <summary><c>sent → negotiation</c> (yalnız süresi dolmamış gönderilmiş teklif; aksi <c>quote.invalid_transition</c> 409). Zoho "Müzakere".</summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record NegotiateQuoteCommand(Guid Id) : ICommand;

public sealed class NegotiateQuoteHandler(QuoteTransitions transitions) : ICommandHandler<NegotiateQuoteCommand>
{
    public Task<Result> Handle(NegotiateQuoteCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (quote, today, _) => quote.Negotiate(today), cancellationToken);
}

/// <summary><c>sent | negotiation → accepted</c> (süresi dolmamış; dolmuşsa <c>quote.expired</c> 409). <c>QuoteAccepted</c> olayı aynı transaction'da outbox'a yazılır.</summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record AcceptQuoteCommand(Guid Id) : ICommand;

public sealed class AcceptQuoteHandler(QuoteTransitions transitions, IIntegrationEventOutbox outbox, ITenantContext tenant, ICurrentUser user)
    : ICommandHandler<AcceptQuoteCommand>
{
    public Task<Result> Handle(AcceptQuoteCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(
            command.Id,
            (quote, today, now) => quote.Accept(today, now),
            cancellationToken,
            quote => outbox.Enqueue(new QuoteAccepted(tenant.TenantId, quote.Id, quote.Number, quote.AccountId, quote.DealId, quote.GrandTotal, quote.Currency, user.UserId)));
}

/// <summary><c>sent → rejected</c> (süresi dolmuş olsa da); <c>reason</c> ≤ 1000.</summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record RejectQuoteCommand(Guid Id, string? Reason) : ICommand;

public sealed class RejectQuoteValidator : AbstractValidator<RejectQuoteCommand>
{
    public RejectQuoteValidator() => RuleFor(x => x.Reason).MaximumLength(CommerceLimits.ReasonMaxLength);
}

public sealed class RejectQuoteHandler(QuoteTransitions transitions) : ICommandHandler<RejectQuoteCommand>
{
    public Task<Result> Handle(RejectQuoteCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (quote, today, now) => quote.Reject(command.Reason, today, now), cancellationToken);
}

/// <summary><c>sent | rejected → draft</c> (etkin <c>expired</c> dahil): yeniden düzenlemek için.</summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record RevertQuoteCommand(Guid Id) : ICommand;

public sealed class RevertQuoteHandler(QuoteTransitions transitions) : ICommandHandler<RevertQuoteCommand>
{
    public Task<Result> Handle(RevertQuoteCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (quote, today, _) => quote.Revert(today), cancellationToken);
}

/// <summary><c>sent → sent</c> (etkin <c>expired</c> dahil): yeni <c>validUntil</c> bugünden önce olamaz (<c>errors.validUntil</c>).</summary>
[RequiresPermission(CommercePermissions.QuotesWrite)]
public sealed record ExtendQuoteCommand(Guid Id, DateOnly? ValidUntil) : ICommand;

public sealed class ExtendQuoteValidator : AbstractValidator<ExtendQuoteCommand>
{
    public ExtendQuoteValidator() => RuleFor(x => x.ValidUntil).NotNull();
}

public sealed class ExtendQuoteHandler(QuoteTransitions transitions) : ICommandHandler<ExtendQuoteCommand>
{
    public Task<Result> Handle(ExtendQuoteCommand command, CancellationToken cancellationToken) =>
        transitions.RunAsync(command.Id, (quote, today, _) => quote.Extend(command.ValidUntil!.Value, today).ThrowIfValidUntilPast(), cancellationToken);
}

/// <summary>
/// Teklif → sipariş dönüşümü (tek transaction; izin <c>crm.quotes.read</c> + <c>crm.orders.write</c>). Teklif <c>accepted</c> olmalı
/// (<c>quote.not_accepted</c> 409), bağlı silinmemiş sipariş olmamalı (<c>quote.already_converted</c> 409); firma/kişi/fırsat yeniden doğrulanır.
/// Numara sayacı, sipariş, kalemler (yeni kimlikler, tutarlar yeniden hesaplanır) ve <c>SalesOrderCreated</c> (<c>quote</c>) olayı aynı transaction'dadır;
/// iki eşzamanlı dönüşümde ikincisi <c>(tenant_id, quote_id)</c> benzersiz indeksiyle düşer → <c>quote.already_converted</c> (sayaç geri döner).
/// Yanıt sipariş detayıdır (<c>crm.orders.read</c> gerekmez).
/// </summary>
[RequiresPermission(CommercePermissions.QuotesRead)]
[RequiresPermission(CommercePermissions.OrdersWrite)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record ConvertQuoteCommand(Guid Id) : ICommand<OrderDto>;

public sealed class ConvertQuoteHandler(
    IQuoteRepository quotes,
    ISalesOrderRepository orders,
    RelatedRecordVerifier related,
    DocumentNumbers numbers,
    IOrderReadStore orderStore,
    ICommerceTransaction transaction,
    IIntegrationEventOutbox outbox,
    ITenantContext tenant,
    ICurrentUser user,
    CommerceClock clock) : ICommandHandler<ConvertQuoteCommand, OrderDto>
{
    public async Task<Result<OrderDto>> Handle(ConvertQuoteCommand command, CancellationToken cancellationToken)
    {
        var converted = await transaction.ExecuteAsync<Guid>(
            async ct =>
            {
                var quote = await quotes.GetByIdAsync(command.Id, ct).ConfigureAwait(false);
                if (quote is null)
                {
                    return Error.NotFound(ErrorCodes.NotFound);
                }

                if (quote.Status != QuoteStatus.Accepted)
                {
                    return Error.Conflict(CommerceErrors.QuoteNotAccepted);
                }

                if (await orders.ExistsForQuoteAsync(quote.Id, ct).ConfigureAwait(false))
                {
                    return Error.Conflict(CommerceErrors.QuoteAlreadyConverted);
                }

                var relatedCheck = await related.VerifyAsync(quote.AccountId, quote.ContactId, quote.DealId, ct).ConfigureAwait(false);
                if (relatedCheck.IsFailure)
                {
                    return relatedCheck.Error;
                }

                var today = await clock.TodayAsync(ct).ConfigureAwait(false);
                var number = await numbers.NextAsync(DocumentKinds.Order, ct).ConfigureAwait(false);

                // Kopyalanır: nakliye, iki adres bloğu, yuvarlama, fiyat listesi (+ M6A alanları ve kalemler). Siparişe özgü alanlar
                // (müşteri satın alma emri no, son tarih, gider vergisi, satış komisyonu, bekliyor) kopyalanmaz → null. Toplamlar aynı
                // DocumentTotals ile (yuvarlama dahil) yeniden hesaplanır → grandTotal tekliften birebir.
                var header = new DocumentHeader(
                    quote.Subject,
                    quote.AccountId,
                    quote.ContactId,
                    quote.DealId,
                    quote.OwnerUserId,
                    quote.Currency,
                    quote.Terms,
                    quote.Notes,
                    quote.Carrier,
                    quote.Adjustment,
                    quote.BillingAddress,
                    quote.ShippingAddress,
                    quote.PriceBookId);
                var created = SalesOrder.Create(tenant.TenantId, number, header, today, quote.Id, quote.Lines.OrderBy(l => l.Position).Select(l => l.ToInput()).ToList());
                if (created.IsFailure)
                {
                    return created.Error;
                }

                var order = created.Value;
                orders.Add(order);
                outbox.Enqueue(new SalesOrderCreated(
                    tenant.TenantId, order.Id, order.Number, order.AccountId, order.DealId, quote.Id, order.GrandTotal, order.Currency, SalesOrderSources.Quote, user.UserId));
                return order.Id;
            },
            cancellationToken).ConfigureAwait(false);
        if (converted.IsFailure)
        {
            return converted.Error;
        }

        return await orderStore.GetAsync(converted.Value, cancellationToken).ConfigureAwait(false) is { } dto
            ? dto
            : Error.NotFound(ErrorCodes.NotFound);
    }
}
