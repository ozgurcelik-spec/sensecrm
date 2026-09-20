using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Contracts;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Domain.Deletion;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Kernel.Time;

namespace Sense.Crm.Modules.Platform.Application.Console;

// ---------------------------------------------------------------------------------------------------------------------
// Abonelik: plan / deneme / istisna (tam ve kalıcı değiştirme).
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>PUT /platform/organizations/{tenantId}/subscription</c>: <c>planCode*</c>, <c>trialEndsOn?</c> (yok/null = denemesiz), <c>overrides?</c> (yok/null = temiz;
/// <c>maxUsers</c> anahtarının varlığı esastır: <c>null</c> = sınırsız). Mevcut kullanım yeni limitin üstündeyse plan yine değişir (veri silinmez);
/// <c>overLimit</c> bunu bildirir. Değişiklik yoksa yazma yapılmaz.
/// </summary>
[PlatformAdminOnly]
public sealed record UpdateSubscriptionCommand(Guid TenantId, string? PlanCode, DateOnly? TrialEndsOn, JsonElement? Overrides) : ICommand<SubscriptionUpdateResultDto>;

public sealed class UpdateSubscriptionValidator : AbstractValidator<UpdateSubscriptionCommand>
{
    public UpdateSubscriptionValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.PlanCode).NotEmpty().MaximumLength(PlatformLimits.PlanCodeMaxLength);
        RuleFor(x => x.Overrides).Custom((element, context) =>
        {
            if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return;
            }

            if (TenantOverrides.FromElement(value) is null)
            {
                context.AddFailure("Overrides", PlatformErrors.InvalidOverrides);
                return;
            }

            foreach (var property in value.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "maxusers":
                        CheckNonNegativeInteger(context, "Overrides.MaxUsers", property.Value, allowNull: true);
                        break;
                    case "maxstoragemb":
                        CheckNonNegativeInteger(context, "Overrides.MaxStorageMb", property.Value, allowNull: true, upperBound: PlatformLimits.MaxStorageMbUpperBound);
                        break;
                    case "maxrecords" when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var record in property.Value.EnumerateObject())
                        {
                            if (!PlanCatalogValidator.KnownModules.Contains(record.Name, StringComparer.Ordinal))
                            {
                                context.AddFailure($"Overrides.MaxRecords.{record.Name}", PlatformErrors.InvalidOverrides);
                            }
                            else
                            {
                                CheckNonNegativeInteger(context, $"Overrides.MaxRecords.{record.Name}", record.Value, allowNull: true);
                            }
                        }

                        break;
                    case "modules" when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var module in property.Value.EnumerateObject())
                        {
                            if (!GatedModules.IsGated(module.Name) || module.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            {
                                context.AddFailure($"Overrides.Modules.{module.Name}", PlatformErrors.InvalidOverrides);
                            }
                        }

                        break;
                    case "maxrecords" or "modules":
                        context.AddFailure($"Overrides.{property.Name}", PlatformErrors.InvalidOverrides);
                        break;
                    default:
                        context.AddFailure($"Overrides.{property.Name}", PlatformErrors.InvalidOverrides);
                        break;
                }
            }
        });
    }

    private static void CheckNonNegativeInteger(ValidationContext<UpdateSubscriptionCommand> context, string property, JsonElement value, bool allowNull, int upperBound = int.MaxValue)
    {
        if (value.ValueKind == JsonValueKind.Null && allowNull)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0 || number > upperBound)
        {
            context.AddFailure(property, PlatformErrors.InvalidOverrides);
        }
    }
}

public sealed class UpdateSubscriptionHandler(
    ITenantAccountRepository accounts,
    IPlanRepository plans,
    ITenantDirectory directory,
    IUsageMeter meter,
    IPlatformAudit audit,
    IIntegrationEventOutbox outbox,
    IEntitlementCache cache,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<UpdateSubscriptionCommand, SubscriptionUpdateResultDto>
{
    public async Task<Result<SubscriptionUpdateResultDto>> Handle(UpdateSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var plan = await plans.GetAsync(command.PlanCode!.Trim(), cancellationToken).ConfigureAwait(false);
        if (plan is null || !plan.IsActive)
        {
            return Error.NotFound(PlatformErrors.PlanNotFound);
        }

        var now = clock.GetUtcNow();
        var info = await directory.FindAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        var timeZone = info?.TimeZone ?? TenantCalendar.UtcId;

        // Geçmiş deneme tarihi reddedilir (anında bitirmek için askıya alınır); değişmeyen tarih (ör. yalnız istisna düzenlemesi) serbesttir.
        if (command.TrialEndsOn is { } trialOn && trialOn != account.TrialEndsOn && trialOn < TenantCalendar.For(timeZone).Today(now))
        {
            throw new ValidationException([new ValidationFailure("TrialEndsOn", PlatformErrors.InvalidTrialDate)]);
        }

        var overrides = command.Overrides is { } element && element.ValueKind == JsonValueKind.Object
            ? TenantOverrides.FromElement(element) ?? TenantOverrides.None
            : TenantOverrides.None;
        DateTime? trialEndsAt = command.TrialEndsOn is { } on ? EntitlementMath.TrialEndsAtUtc(on, timeZone) : null;

        var applied = account.ChangeSubscription(plan.Code, command.TrialEndsOn, trialEndsAt, overrides, now.UtcDateTime);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var change = applied.Value;
        if (change.Changed)
        {
            audit.Record(
                PlatformAuditActions.SubscriptionChanged,
                account,
                null,
                new Dictionary<string, object?>
                {
                    ["plan"] = new { old = change.OldPlanCode, @new = plan.Code },
                    ["trialEndsOn"] = new { old = change.OldTrialEndsOn?.ToString("yyyy-MM-dd"), @new = command.TrialEndsOn?.ToString("yyyy-MM-dd") },
                    ["overrides"] = new { old = ParseJson(change.OldOverridesJson), @new = ParseJson(account.Overrides) },
                });
            outbox.Enqueue(new PlanChanged(account.TenantId, change.OldPlanCode, plan.Code, command.TrialEndsOn, change.OverridesChanged, user.UserId));
            await cache.InvalidateAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        }

        // Mevcut kullanım yeni limitin üstündeyse: plan yine değişir (veri silinmez); aşım bildirilir, yeni tüketim 402 alır.
        var effective = EntitlementMath.Effective(plan, account);
        var usage = await meter.CollectAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        return new SubscriptionUpdateResultDto(EntitlementMath.OverLimits(effective.MaxUsers, effective.MaxRecords, effective.Modules, usage, effective.MaxStorageMb));
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Askı ve yeniden açma.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>POST …/suspend</c>: <c>reason*</c> (≤500), <c>mode</c> = <c>readOnly</c> (varsayılan) | <c>blocked</c>. Yalnız <c>active</c> kiracı; sistem kiracısı 422.</summary>
[PlatformAdminOnly]
public sealed record SuspendOrganizationCommand(Guid TenantId, string? Reason, string? Mode) : ICommand;

public sealed class SuspendOrganizationValidator : AbstractValidator<SuspendOrganizationCommand>
{
    public SuspendOrganizationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(PlatformLimits.ReasonMaxLength);
        RuleFor(x => x.Mode).Must(m => string.IsNullOrEmpty(m) || SuspensionModes.IsValid(m)).WithMessage(PlatformErrors.InvalidMode);
    }
}

public sealed class SuspendOrganizationHandler(
    ITenantAccountRepository accounts,
    IPlatformAudit audit,
    IIntegrationEventOutbox outbox,
    IEntitlementCache cache,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<SuspendOrganizationCommand>
{
    public async Task<Result> Handle(SuspendOrganizationCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var mode = string.IsNullOrEmpty(command.Mode) ? SuspensionModes.ReadOnly : command.Mode;
        var reason = command.Reason!.Trim();
        var suspended = account.Suspend(reason, mode, clock.GetUtcNow().UtcDateTime);
        if (suspended.IsFailure)
        {
            return suspended;
        }

        audit.Record(
            PlatformAuditActions.OrganizationSuspended,
            account,
            null,
            new Dictionary<string, object?> { ["status"] = new { old = AccountStatuses.Active, @new = AccountStatuses.Suspended }, ["mode"] = mode, ["reason"] = reason });
        outbox.Enqueue(new TenantSuspended(account.TenantId, reason, mode, user.UserId));
        await cache.InvalidateAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

/// <summary><c>POST …/reactivate</c>: yalnız <c>suspended</c> (deneme bitmişse etkin durum <c>trial_expired</c> kalır).</summary>
[PlatformAdminOnly]
public sealed record ReactivateOrganizationCommand(Guid TenantId) : ICommand;

public sealed class ReactivateOrganizationHandler(
    ITenantAccountRepository accounts,
    IPlatformAudit audit,
    IIntegrationEventOutbox outbox,
    IEntitlementCache cache,
    ICurrentUser user) : ICommandHandler<ReactivateOrganizationCommand>
{
    public async Task<Result> Handle(ReactivateOrganizationCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var reactivated = account.Reactivate();
        if (reactivated.IsFailure)
        {
            return reactivated;
        }

        audit.Record(
            PlatformAuditActions.OrganizationReactivated,
            account,
            null,
            new Dictionary<string, object?> { ["status"] = new { old = AccountStatuses.Suspended, @new = AccountStatuses.Active } });
        outbox.Enqueue(new TenantReactivated(account.TenantId, user.UserId));
        await cache.InvalidateAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Silme talebi (KVKK): kiracı anında pending_deletion; bekleme süresi sonunda Worker kalıcı imha eder.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary><c>POST …/deletion-request</c>: <c>reason*</c>, <c>retentionDays?</c> (varsayılan <c>Platform:Deletion:RetentionDays</c>, 7–90). Yalnız <c>active|suspended</c>.</summary>
[PlatformAdminOnly]
public sealed record RequestDeletionCommand(Guid TenantId, string? Reason, int? RetentionDays) : ICommand<DeletionRequestResultDto>;

public sealed class RequestDeletionValidator : AbstractValidator<RequestDeletionCommand>
{
    public RequestDeletionValidator(IOptions<PlatformOptions> options)
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(PlatformLimits.ReasonMaxLength);
        RuleFor(x => x.RetentionDays)
            .InclusiveBetween(options.Value.Deletion.MinRetentionDays, options.Value.Deletion.MaxRetentionDays)
            .When(x => x.RetentionDays is not null)
            .WithMessage(PlatformErrors.InvalidRetention);
    }
}

public sealed class RequestDeletionHandler(
    ITenantAccountRepository accounts,
    IDeletionRequestRepository requests,
    IPlatformAudit audit,
    IIntegrationEventOutbox outbox,
    IEntitlementCache cache,
    ICurrentUser user,
    IOptions<PlatformOptions> options,
    TimeProvider clock) : ICommandHandler<RequestDeletionCommand, DeletionRequestResultDto>
{
    public async Task<Result<DeletionRequestResultDto>> Handle(RequestDeletionCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (await requests.GetActiveAsync(account.TenantId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", account.Status), ("to", AccountStatuses.PendingDeletion));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var previous = account.MarkPendingDeletion();
        if (previous.IsFailure)
        {
            return previous.Error;
        }

        var retention = command.RetentionDays ?? options.Value.Deletion.RetentionDays;
        var reason = command.Reason!.Trim();
        var request = DeletionRequest.Create(account.TenantId, user.UserId, reason, retention, previous.Value, now);
        requests.Add(request);

        var scheduled = new DateTimeOffset(DateTime.SpecifyKind(request.ScheduledFor, DateTimeKind.Utc));
        audit.Record(
            PlatformAuditActions.DeletionRequested,
            account,
            null,
            new Dictionary<string, object?>
            {
                ["status"] = new { old = previous.Value, @new = AccountStatuses.PendingDeletion },
                ["retentionDays"] = retention,
                ["scheduledFor"] = scheduled,
                ["reason"] = reason,
            });
        outbox.Enqueue(new TenantDeletionRequested(account.TenantId, scheduled, user.UserId));
        await cache.InvalidateAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        return new DeletionRequestResultDto(request.Id, scheduled);
    }
}

/// <summary><c>POST …/deletion-request/cancel</c>: yalnız <c>scheduled</c> talep ve <c>now &lt; scheduled_for</c>; kiracı önceki duruma döner.</summary>
[PlatformAdminOnly]
public sealed record CancelDeletionCommand(Guid TenantId) : ICommand;

public sealed class CancelDeletionHandler(
    ITenantAccountRepository accounts,
    IDeletionRequestRepository requests,
    IPlatformAudit audit,
    IIntegrationEventOutbox outbox,
    IEntitlementCache cache,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<CancelDeletionCommand>
{
    public async Task<Result> Handle(CancelDeletionCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var request = await requests.GetActiveAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Error.Conflict(PlatformErrors.DeletionNotCancellable);
        }

        var cancelled = request.Cancel(user.UserId, clock.GetUtcNow().UtcDateTime);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        var restored = account.RestoreAfterDeletionCancelled(request.PreviousStatus);
        if (restored.IsFailure)
        {
            return restored;
        }

        audit.Record(
            PlatformAuditActions.DeletionCancelled,
            account,
            null,
            new Dictionary<string, object?> { ["status"] = new { old = AccountStatuses.PendingDeletion, @new = request.PreviousStatus } });
        outbox.Enqueue(new TenantDeletionCancelled(account.TenantId, user.UserId));
        await cache.InvalidateAsync(account.TenantId, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
