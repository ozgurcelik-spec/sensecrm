using FluentValidation;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Integrations.Application.Webhooks;

/// <summary>
/// <c>GET /integrations/deliveries</c>: <c>subscriptionId</c>, <c>status</c> (virgülle çoklu), <c>eventType</c>, <c>eventId</c> (tam Guid), <c>kind</c>, <c>from</c>/<c>to</c> (UTC günü, dahil, en çok 90 gün aralığı);
/// sıralama <c>createdAt</c> (varsayılan <c>-createdAt</c>), <c>completedAt</c>, <c>attempts</c>, <c>status</c>.
/// </summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record ListDeliveriesQuery(PagedQuery Paging, Guid? SubscriptionId, string? Status, string? EventType, Guid? EventId, string? Kind, DateOnly? From, DateOnly? To)
    : IQuery<PagedResult<DeliveryListItemDto>>;

public sealed class ListDeliveriesValidator : AbstractValidator<ListDeliveriesQuery>
{
    public const int MaxRangeDays = 90;

    private static readonly string[] Statuses = [DeliveryStatuses.Pending, DeliveryStatuses.Delivering, DeliveryStatuses.Succeeded, DeliveryStatuses.Failed];
    private static readonly string[] Kinds = [DeliveryKinds.Event, DeliveryKinds.Ping, DeliveryKinds.Redelivery];

    public ListDeliveriesValidator()
    {
        RuleFor(x => x.Status).Must(s => SplitStatuses(s).All(x => Statuses.Contains(x, StringComparer.Ordinal))).When(x => !string.IsNullOrWhiteSpace(x.Status));
        RuleFor(x => x.Kind).Must(k => Kinds.Contains(k!, StringComparer.Ordinal)).When(x => !string.IsNullOrWhiteSpace(x.Kind));
        RuleFor(x => x.To).Must((q, to) => to!.Value >= q.From!.Value).When(x => x.From is not null && x.To is not null);
        RuleFor(x => x.To).Must((q, to) => to!.Value.DayNumber - q.From!.Value.DayNumber <= MaxRangeDays).When(x => x.From is not null && x.To is not null);
    }

    public static IReadOnlyList<string> SplitStatuses(string? status) =>
        string.IsNullOrWhiteSpace(status) ? [] : [.. status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

public sealed class ListDeliveriesHandler(IWebhookReadStore store, IOptions<IntegrationsOptions> options)
    : IQueryHandler<ListDeliveriesQuery, PagedResult<DeliveryListItemDto>>
{
    public async Task<Result<PagedResult<DeliveryListItemDto>>> Handle(ListDeliveriesQuery query, CancellationToken cancellationToken) =>
        await store.ListDeliveriesAsync(
            query.Paging,
            new DeliveryFilter(query.SubscriptionId, ListDeliveriesValidator.SplitStatuses(query.Status), query.EventType, query.EventId, query.Kind, query.From, query.To),
            options.Value.Webhooks.MaxAttempts,
            cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record GetDeliveryQuery(Guid Id) : IQuery<DeliveryDetailDto>;

public sealed class GetDeliveryHandler(IWebhookReadStore store, IOptions<IntegrationsOptions> options) : IQueryHandler<GetDeliveryQuery, DeliveryDetailDto>
{
    public async Task<Result<DeliveryDetailDto>> Handle(GetDeliveryQuery query, CancellationToken cancellationToken)
    {
        var dto = await store.GetDeliveryAsync(query.Id, options.Value.Webhooks.MaxAttempts, cancellationToken).ConfigureAwait(false);
        return dto is null ? Error.NotFound(ErrorCodes.NotFound) : dto;
    }
}

/// <summary>
/// <c>POST …/test</c> → 202 <c>{ deliveryId }</c>: <c>ping</c> zarfı kuyruğa yazılır (API asla hedefe bağlanmaz; Worker birkaç sn içinde teslim eder). Pasif abonelikte de çalışır. Gönderim kapalıysa
/// <c>409 webhook.delivery_unavailable</c>; abonelik başına dakikada <c>TestPingPerMinute</c> (5) aşımı <c>429</c>. Ping sağlık sayacını etkilemez.
/// </summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record SendWebhookTestCommand(Guid Id) : ICommand<DeliveryAcceptedDto>;

public sealed class SendWebhookTestHandler(
    IWebhookSubscriptionRepository subscriptions,
    IWebhookDeliveryRepository deliveries,
    IWebhookRuntime runtime,
    IActionThrottle throttle,
    IOptions<IntegrationsOptions> options,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<SendWebhookTestCommand, DeliveryAcceptedDto>
{
    public async Task<Result<DeliveryAcceptedDto>> Handle(SendWebhookTestCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!runtime.DeliveryAvailable)
        {
            return Error.Conflict(IntegrationsErrors.WebhookDeliveryUnavailable);
        }

        if (!throttle.TryAcquire("ping:" + subscription.Id.ToString("N"), options.Value.Webhooks.TestPingPerMinute, TimeSpan.FromMinutes(1)))
        {
            return new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var eventId = Guid.CreateVersion7();
        var payload = WebhookEnvelope.Serialize(eventId, WebhookEventTypes.Ping, WebhookEnvelope.CurrentVersion, now, tenant.TenantId, user.UserId, WebhookDataMappers.Ping(subscription.Id));
        var delivery = WebhookDelivery.Create(tenant.TenantId, subscription.Id, eventId, WebhookEventTypes.Ping, DeliveryKinds.Ping, redeliveryOf: null, payload, subscription.Host, now);
        deliveries.Add(delivery);
        deliveries.AddQueueItem(new DeliveryQueueItem { DeliveryId = delivery.Id, TenantId = tenant.TenantId, DueAt = now, Attempt = 0 });
        return new DeliveryAcceptedDto(delivery.Id);
    }
}

/// <summary>
/// <c>POST /integrations/deliveries/{id}/redeliver</c> → 202: yeni satır (<c>kind = redelivery</c>, aynı <c>event_id</c>/<c>payload</c>, <c>attempts = 0</c>); imza <b>güncel</b> sırla, yeni <c>t</c>. Yalnız
/// <c>succeeded|failed</c> (aksi <c>409 delivery.not_redeliverable</c>); abonelik pasif → <c>409 webhook.disabled</c>; gönderim kapalı → <c>409 webhook.delivery_unavailable</c>;
/// kiracı başına dakikada 20 (<c>429</c>).
/// </summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record RedeliverDeliveryCommand(Guid Id) : ICommand<DeliveryAcceptedDto>;

public sealed class RedeliverDeliveryHandler(
    IWebhookDeliveryRepository deliveries,
    IWebhookSubscriptionRepository subscriptions,
    IWebhookRuntime runtime,
    IActionThrottle throttle,
    IOptions<IntegrationsOptions> options,
    ITenantContext tenant,
    TimeProvider clock) : ICommandHandler<RedeliverDeliveryCommand, DeliveryAcceptedDto>
{
    public async Task<Result<DeliveryAcceptedDto>> Handle(RedeliverDeliveryCommand command, CancellationToken cancellationToken)
    {
        var original = await deliveries.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (original is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!original.IsRedeliverable)
        {
            return Error.Conflict(IntegrationsErrors.DeliveryNotRedeliverable);
        }

        var subscription = await subscriptions.GetByIdAsync(original.SubscriptionId, cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!subscription.Enabled)
        {
            return Error.Conflict(IntegrationsErrors.WebhookDisabled);
        }

        if (!runtime.DeliveryAvailable)
        {
            return Error.Conflict(IntegrationsErrors.WebhookDeliveryUnavailable);
        }

        if (!throttle.TryAcquire("redeliver:" + tenant.TenantId.ToString("N"), options.Value.Webhooks.RedeliverPerMinute, TimeSpan.FromMinutes(1)))
        {
            return new Error(ErrorCodes.RateLimitExceeded, ErrorType.TooManyRequests);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var delivery = WebhookDelivery.Create(tenant.TenantId, subscription.Id, original.EventId, original.EventType, DeliveryKinds.Redelivery, original.Id, original.Payload, subscription.Host, now);
        deliveries.Add(delivery);
        deliveries.AddQueueItem(new DeliveryQueueItem { DeliveryId = delivery.Id, TenantId = tenant.TenantId, DueAt = now, Attempt = 0 });
        return new DeliveryAcceptedDto(delivery.Id);
    }
}
