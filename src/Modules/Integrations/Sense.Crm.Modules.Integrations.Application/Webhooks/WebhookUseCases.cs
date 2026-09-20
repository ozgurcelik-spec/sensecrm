using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application.Security;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Integrations.Application.Webhooks;

/// <summary>Abonelik → DTO eşlemesi (ham sır asla; yalnız <c>secretHint</c>).</summary>
public static class WebhookMapper
{
    public static WebhookSubscriptionDto ToDto(WebhookSubscription s, DateTime now, string? createdByName, string? secret = null, Guid? createdByUserId = null) =>
        new(
            s.Id,
            s.Name,
            s.Url,
            s.Host,
            s.EventTypes,
            s.Enabled,
            s.DisabledReason,
            s.Description,
            "…" + s.SecretLast4,
            s.SecretVersion,
            s.HasActivePreviousSecret(now) ? s.PreviousSecretExpiresAt : null,
            s.ConsecutiveFailures,
            WebhookSubscription.HealthOf(s.Enabled, s.ConsecutiveFailures, s.LastFailureAt, s.LastSuccessAt, now),
            s.LastSuccessAt,
            s.LastFailureAt,
            s.CreatedAt == default ? now : s.CreatedAt,
            s.ModifiedDate,
            createdByUserId ?? s.CreatedUserId ?? Guid.Empty,
            createdByName,
            secret);
}

/// <summary>Abonelik alanları (oluşturma ve tam değiştirme ortak doğrulaması). URL kuralları (<c>webhook.url_invalid</c>) yetki denetiminden sonra, handler'da uygulanır.</summary>
public interface IWebhookFields
{
    string Name { get; }

    string Url { get; }

    IReadOnlyList<string> EventTypes { get; }

    string? Description { get; }
}

public abstract class WebhookFieldsValidator<T> : AbstractValidator<T>
    where T : IWebhookFields
{
    protected WebhookFieldsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(IntegrationsLimits.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(IntegrationsLimits.DescriptionMaxLength);
        RuleFor(x => x.Url).NotEmpty();
        RuleFor(x => x.EventTypes)
            .NotNull()
            .Must(t => t is { Count: >= 1 and <= IntegrationsLimits.EventTypesMax })
            .WithMessage("{PropertyName} must contain 1-20 items.")
            .Must(t => t.All(type => WebhookEventTypes.All.Contains(type, StringComparer.Ordinal)))
            .WithMessage("{PropertyName} contains an unknown event type.");
    }
}

internal static class WebhookUrls
{
    public static Result<UrlCheck> Check(string url, IOptions<IntegrationsOptions> options)
    {
        var check = SsrfGuard.Validate(url, UrlPolicy.From(options.Value));
        return check.Ok
            ? check
            : Error.Validation(IntegrationsErrors.WebhookUrlInvalid, (IntegrationsErrors.ReasonArg, check.Reason));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Sorgular
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>GET /integrations/webhooks</c>: <c>q</c> (ad + host), <c>enabled</c>, <c>eventType</c>; sıralama <c>name</c>, <c>createdAt</c>, <c>lastFailureAt</c>.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record ListWebhooksQuery(PagedQuery Paging, bool? Enabled, string? EventType) : IQuery<PagedResult<WebhookSubscriptionDto>>;

public sealed class ListWebhooksHandler(IWebhookReadStore store, TimeProvider clock) : IQueryHandler<ListWebhooksQuery, PagedResult<WebhookSubscriptionDto>>
{
    public async Task<Result<PagedResult<WebhookSubscriptionDto>>> Handle(ListWebhooksQuery query, CancellationToken cancellationToken) =>
        await store.ListSubscriptionsAsync(query.Paging, query.EventType, query.Enabled, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record GetWebhookQuery(Guid Id) : IQuery<WebhookSubscriptionDto>;

public sealed class GetWebhookHandler(IWebhookReadStore store, TimeProvider clock) : IQueryHandler<GetWebhookQuery, WebhookSubscriptionDto>
{
    public async Task<Result<WebhookSubscriptionDto>> Handle(GetWebhookQuery query, CancellationToken cancellationToken)
    {
        var dto = await store.GetSubscriptionAsync(query.Id, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return dto is null ? Error.NotFound(ErrorCodes.NotFound) : dto;
    }
}

/// <summary><c>GET /integrations/webhook-events</c>: olay kataloğu (<c>ping</c> dahil değil); <c>available</c> = kaynak modül kiracı planında açık.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record ListWebhookEventsQuery : IQuery<IReadOnlyList<WebhookEventDto>>;

public sealed class ListWebhookEventsHandler(ITenantContext tenant, ITenantEntitlements entitlements)
    : IQueryHandler<ListWebhookEventsQuery, IReadOnlyList<WebhookEventDto>>
{
    public async Task<Result<IReadOnlyList<WebhookEventDto>>> Handle(ListWebhookEventsQuery query, CancellationToken cancellationToken)
    {
        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WebhookEventDto> result =
        [
            .. WebhookEventCatalog.All.Select(d => new WebhookEventDto(
                d.Type,
                d.Version,
                d.Group,
                d.Module,
                snapshot.IsModuleEnabled(d.Module),
                d.Deprecated,
                d.SunsetOn,
                d.Description,
                JsonDocument.Parse(WebhookEventCatalog.SampleEnvelope(d)).RootElement.Clone())),
        ];
        return Result.Success(result);
    }
}

/// <summary><c>GET /integrations/status</c>: dağıtım durumu ve plan limitleri/kullanımı (API yalnız yapılandırmayı okur; dış çağrı yapmaz).</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record GetIntegrationsStatusQuery : IQuery<IntegrationsStatusDto>;

public sealed class GetIntegrationsStatusHandler(
    IOptions<IntegrationsOptions> options,
    IWebhookRuntime runtime,
    ITenantContext tenant,
    ITenantEntitlements entitlements,
    IIntegrationsStats stats,
    IOptions<RateLimitingOptions> rateLimiting,
    TimeProvider clock) : IQueryHandler<GetIntegrationsStatusQuery, IntegrationsStatusDto>
{
    public async Task<Result<IntegrationsStatusDto>> Handle(GetIntegrationsStatusQuery query, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var (webhooks, apiKeys) = await stats.CountAsync(clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return new IntegrationsStatusDto(
            runtime.DeliveryAvailable,
            o.Webhooks.EffectiveAllowedHosts.Count > 0,
            o.Webhooks.MaxAttempts,
            o.Webhooks.TimeoutSeconds,
            WebhookSignature.DefaultToleranceSeconds,
            o.Webhooks.DeliveryRetentionDays,
            new IntegrationsStatusApiKeysDto(o.ApiKeys.MaxLifetimeDays, o.ApiKeys.DefaultLifetimeDays, rateLimiting.Value.ApiKey.PermitLimit),
            new IntegrationsStatusLimitsDto(snapshot.MaxWebhooks, snapshot.MaxApiKeys),
            new IntegrationsStatusUsageDto(webhooks, apiKeys));
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Komutlar
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>POST /integrations/webhooks</c> → 201 abonelik + <c>secret</c> (yalnız burada). <c>eventTypes</c> 1–20 katalog türü (<c>ping</c> hariç). URL: <c>400 webhook.url_invalid</c>.
/// Ad çakışması <c>409 webhook.name_taken</c>. Plan limiti (<c>402</c>): abonelikler <b>tümü</b> sayılır (pasifler dahil).
/// </summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
[ConsumesLimit(LimitKeys.Webhooks)]
public sealed record CreateWebhookSubscriptionCommand(string Name, string Url, IReadOnlyList<string> EventTypes, string? Description, bool Enabled = true)
    : ICommand<WebhookSubscriptionDto>, IWebhookFields;

public sealed class CreateWebhookSubscriptionValidator : WebhookFieldsValidator<CreateWebhookSubscriptionCommand>;

public sealed class CreateWebhookSubscriptionHandler(
    IWebhookSubscriptionRepository subscriptions,
    IWebhookSecretProtector protector,
    IOptions<IntegrationsOptions> options,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<CreateWebhookSubscriptionCommand, WebhookSubscriptionDto>
{
    public async Task<Result<WebhookSubscriptionDto>> Handle(CreateWebhookSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var url = WebhookUrls.Check(command.Url, options);
        if (url.IsFailure)
        {
            return url.Error;
        }

        if (await subscriptions.NameExistsAsync(command.Name.Trim(), excludeId: null, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IntegrationsErrors.WebhookNameTaken);
        }

        var id = Guid.CreateVersion7();
        var secret = protector.GenerateSecret();
        var sealedSecret = protector.Seal(secret, tenant.TenantId, id, version: 1);
        var subscription = WebhookSubscription.Create(
            id, tenant.TenantId, command.Name, url.Value.Uri!.AbsoluteUri, url.Value.Host!, command.EventTypes, command.Description, command.Enabled, sealedSecret);
        subscriptions.Add(subscription);
        return WebhookMapper.ToDto(subscription, clock.GetUtcNow().UtcDateTime, user.DisplayName, secret, user.UserId);
    }
}

/// <summary><c>PUT /integrations/webhooks/{id}</c> → 204 (tam değiştirme); <c>enabled</c> true'ya dönüşte <c>consecutiveFailures = 0</c>, <c>disabledReason = null</c>.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record UpdateWebhookSubscriptionCommand(Guid Id, string Name, string Url, IReadOnlyList<string> EventTypes, string? Description, bool Enabled)
    : ICommand, IWebhookFields;

public sealed class UpdateWebhookSubscriptionValidator : WebhookFieldsValidator<UpdateWebhookSubscriptionCommand>
{
    public UpdateWebhookSubscriptionValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateWebhookSubscriptionHandler(
    IWebhookSubscriptionRepository subscriptions,
    IOptions<IntegrationsOptions> options,
    TimeProvider clock) : ICommandHandler<UpdateWebhookSubscriptionCommand>
{
    public async Task<Result> Handle(UpdateWebhookSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var url = WebhookUrls.Check(command.Url, options);
        if (url.IsFailure)
        {
            return url.Error;
        }

        if (await subscriptions.NameExistsAsync(command.Name.Trim(), command.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(IntegrationsErrors.WebhookNameTaken);
        }

        subscription.Update(command.Name, url.Value.Uri!.AbsoluteUri, url.Value.Host!, command.EventTypes, command.Description, command.Enabled, clock.GetUtcNow().UtcDateTime);
        return Result.Success();
    }
}

/// <summary><c>POST …/enable</c> → 204 (idempotent; sayaç ve neden sıfırlanır).</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record EnableWebhookCommand(Guid Id) : ICommand;

public sealed class EnableWebhookHandler(IWebhookSubscriptionRepository subscriptions) : ICommandHandler<EnableWebhookCommand>
{
    public async Task<Result> Handle(EnableWebhookCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        subscription.Enable();
        return Result.Success();
    }
}

/// <summary><c>POST …/disable</c> → 204 (idempotent; <c>disabledReason = manual</c>). Bekleyen teslimatlar sürer; yalnız yeni fan-out durur.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record DisableWebhookCommand(Guid Id) : ICommand;

public sealed class DisableWebhookHandler(IWebhookSubscriptionRepository subscriptions, TimeProvider clock) : ICommandHandler<DisableWebhookCommand>
{
    public async Task<Result> Handle(DisableWebhookCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        subscription.Disable(clock.GetUtcNow().UtcDateTime);
        return Result.Success();
    }
}

/// <summary><c>DELETE /integrations/webhooks/{id}</c> → 204; teslimat/deneme/kuyruk satırları silinir.</summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record DeleteWebhookCommand(Guid Id) : ICommand;

public sealed class DeleteWebhookHandler(IWebhookSubscriptionRepository subscriptions) : ICommandHandler<DeleteWebhookCommand>
{
    public async Task<Result> Handle(DeleteWebhookCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        await subscriptions.RemoveAsync(subscription, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary>
/// <c>POST …/rotate-secret { graceHours? }</c> → 200 yeni ham sır (<c>no-store</c>). Yeni sır anında geçerli; eski sır <c>graceHours</c> (0–168, varsayılan 24) boyunca çift imzayla desteklenir
/// (0 → eski sır hemen silinir). Grace içinde tekrar döndürme <c>previous</c>'ı mevcut sırla değiştirir.
/// </summary>
[RequiresPermission(IntegrationsPermissions.Manage)]
public sealed record RotateWebhookSecretCommand(Guid Id, int? GraceHours) : ICommand<RotatedSecretDto>;

public sealed class RotateWebhookSecretValidator : AbstractValidator<RotateWebhookSecretCommand>
{
    public const int MaxGraceHours = 168;
    public const int DefaultGraceHours = 24;

    public RotateWebhookSecretValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.GraceHours).InclusiveBetween(0, MaxGraceHours).When(x => x.GraceHours is not null);
    }
}

public sealed class RotateWebhookSecretHandler(
    IWebhookSubscriptionRepository subscriptions,
    IWebhookSecretProtector protector,
    ITenantContext tenant,
    TimeProvider clock) : ICommandHandler<RotateWebhookSecretCommand, RotatedSecretDto>
{
    public async Task<Result<RotatedSecretDto>> Handle(RotateWebhookSecretCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (subscription is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var grace = command.GraceHours ?? RotateWebhookSecretValidator.DefaultGraceHours;
        var secret = protector.GenerateSecret();
        subscription.RotateSecret(protector.Seal(secret, tenant.TenantId, subscription.Id, subscription.NextSecretVersion), grace, now);
        return new RotatedSecretDto(secret, "…" + subscription.SecretLast4, subscription.SecretVersion, subscription.HasActivePreviousSecret(now) ? subscription.PreviousSecretExpiresAt : null);
    }
}
