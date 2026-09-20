using FluentValidation;
using Sense.Crm.Modules.Activities.Contracts;
using Sense.Crm.Modules.Activities.Domain;
using Sense.Crm.Modules.Activities.Domain.Activities;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Activities.Application.Activities;

/// <summary>
/// Liste: filtreler <c>assignedUserId, type, status, relatedType, relatedId, dueFrom, dueTo, overdue</c> + <c>q</c> (konu/açıklama).
/// <c>dueFrom</c>/<c>dueTo</c> anları (UTC, uçlar dahil) belirtir.
/// </summary>
[RequiresPermission(ActivitiesPermissions.Read)]
public sealed record ListActivitiesQuery(PagedQuery Paging, ActivityFilter Filter) : IQuery<PagedResult<ActivityDto>>;

public sealed class ListActivitiesHandler(IActivityReadStore store, TimeProvider clock) : IQueryHandler<ListActivitiesQuery, PagedResult<ActivityDto>>
{
    public async Task<Result<PagedResult<ActivityDto>>> Handle(ListActivitiesQuery query, CancellationToken cancellationToken)
    {
        var filter = query.Filter with { DueFrom = Activity.ToUtc(query.Filter.DueFrom), DueTo = Activity.ToUtc(query.Filter.DueTo) };
        return await store.ListAsync(query.Paging, filter, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission(ActivitiesPermissions.Read)]
public sealed record GetActivityQuery(Guid Id) : IQuery<ActivityDto>;

public sealed class GetActivityHandler(IActivityReadStore store, TimeProvider clock) : IQueryHandler<GetActivityQuery, ActivityDto>
{
    public async Task<Result<ActivityDto>> Handle(GetActivityQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false) is { } activity
            ? activity
            : Error.NotFound(ErrorCodes.NotFound);
}

/// <summary>Aktivite alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface IActivityFields
{
    ActivityType? Type { get; }

    string Subject { get; }

    string? Description { get; }

    ActivityRelatedType? RelatedType { get; }

    Guid? RelatedId { get; }

    Guid? AssignedUserId { get; }
}

public abstract class ActivityFieldsValidator<T> : AbstractValidator<T>
    where T : IActivityFields
{
    protected ActivityFieldsValidator()
    {
        RuleFor(x => x.Type).NotNull();
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(ActivityLimits.SubjectMaxLength);
        RuleFor(x => x.Description).MaximumLength(ActivityLimits.DescriptionMaxLength);
        RuleFor(x => x.RelatedType).NotNull().When(x => x.RelatedId is not null).WithMessage(ActivitiesErrors.RelatedIncomplete);
        RuleFor(x => x.RelatedId).NotNull().When(x => x.RelatedType is not null).WithMessage(ActivitiesErrors.RelatedIncomplete);
        RuleFor(x => x.RelatedId).NotEqual(Guid.Empty).When(x => x.RelatedId is not null);
        RuleFor(x => x.AssignedUserId).NotEqual(Guid.Empty).When(x => x.AssignedUserId is not null);
    }
}

/// <summary>
/// Yeni aktivite. Durum verilmezse <c>open</c> (not: <c>completed</c>); atanan verilmezse çağıran; ilişkili kayıt aktif
/// organizasyonda bulunmalı (<c>activity.related_not_found</c>); <c>endAt &lt; startAt</c> → <c>activity.invalid_range</c>.
/// </summary>
[RequiresPermission(ActivitiesPermissions.Write)]
[ConsumesLimit(LimitKeys.Records)]
public sealed record CreateActivityCommand(
    ActivityType? Type,
    string Subject,
    string? Description,
    ActivityStatus? Status,
    ActivityPriority? Priority,
    DateTime? DueAt,
    DateTime? StartAt,
    DateTime? EndAt,
    ActivityRelatedType? RelatedType,
    Guid? RelatedId,
    Guid? AssignedUserId) : ICommand<Guid>, IActivityFields;

public sealed class CreateActivityValidator : ActivityFieldsValidator<CreateActivityCommand>;

public sealed class CreateActivityHandler(
    IActivityRepository activities,
    AssigneeResolver assignees,
    RelatedRecordVerifier related,
    ITenantContext tenant,
    TimeProvider clock) : ICommandHandler<CreateActivityCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateActivityCommand command, CancellationToken cancellationToken)
    {
        var assignee = await assignees.ResolveAsync(command.AssignedUserId, current: null, cancellationToken).ConfigureAwait(false);
        if (assignee.IsFailure)
        {
            return assignee.Error;
        }

        var relatedCheck = await related.VerifyAsync(command.RelatedType, command.RelatedId, cancellationToken).ConfigureAwait(false);
        if (relatedCheck.IsFailure)
        {
            return relatedCheck.Error;
        }

        var created = Activity.Create(
            tenant.TenantId,
            command.Type!.Value,
            command.Subject,
            command.Description,
            command.Status,
            command.Priority,
            command.DueAt,
            command.StartAt,
            command.EndAt,
            command.RelatedType,
            command.RelatedId,
            assignee.Value,
            clock.GetUtcNow().UtcDateTime);
        if (created.IsFailure)
        {
            return created.Error;
        }

        activities.Add(created.Value);
        return created.Value.Id;
    }
}

/// <summary>
/// Tam değiştirme (PUT). Durum verilmezse korunur; atanan verilmezse mevcut korunur; ilişkili kayıt ve atanan yalnız
/// değiştiyse yeniden doğrulanır (bağlı kaydı silinmiş aktivite başka alanlar için düzenlenebilir kalır).
/// </summary>
[RequiresPermission(ActivitiesPermissions.Write)]
public sealed record UpdateActivityCommand(
    Guid Id,
    ActivityType? Type,
    string Subject,
    string? Description,
    ActivityStatus? Status,
    ActivityPriority? Priority,
    DateTime? DueAt,
    DateTime? StartAt,
    DateTime? EndAt,
    ActivityRelatedType? RelatedType,
    Guid? RelatedId,
    Guid? AssignedUserId) : ICommand, IActivityFields;

public sealed class UpdateActivityValidator : ActivityFieldsValidator<UpdateActivityCommand>
{
    public UpdateActivityValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateActivityHandler(IActivityRepository activities, AssigneeResolver assignees, RelatedRecordVerifier related, TimeProvider clock)
    : ICommandHandler<UpdateActivityCommand>
{
    public async Task<Result> Handle(UpdateActivityCommand command, CancellationToken cancellationToken)
    {
        var activity = await activities.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (activity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var assignee = await assignees.ResolveAsync(command.AssignedUserId, activity.AssignedUserId, cancellationToken).ConfigureAwait(false);
        if (assignee.IsFailure)
        {
            return assignee.Error;
        }

        var relatedChanged = command.RelatedType != activity.RelatedType || command.RelatedId != activity.RelatedId;
        if (relatedChanged)
        {
            var relatedCheck = await related.VerifyAsync(command.RelatedType, command.RelatedId, cancellationToken).ConfigureAwait(false);
            if (relatedCheck.IsFailure)
            {
                return relatedCheck;
            }
        }

        return activity.Update(
            command.Type!.Value,
            command.Subject,
            command.Description,
            command.Status,
            command.Priority,
            command.DueAt,
            command.StartAt,
            command.EndAt,
            command.RelatedType,
            command.RelatedId,
            assignee.Value,
            clock.GetUtcNow().UtcDateTime);
    }
}

[RequiresPermission(ActivitiesPermissions.Write)]
public sealed record DeleteActivityCommand(Guid Id) : ICommand;

public sealed class DeleteActivityHandler(IActivityRepository activities) : ICommandHandler<DeleteActivityCommand>
{
    public async Task<Result> Handle(DeleteActivityCommand command, CancellationToken cancellationToken)
    {
        var activity = await activities.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (activity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        activities.Remove(activity);
        return Result.Success();
    }
}

/// <summary>Tamamlar (idempotent). Not için <c>activity.note_status_fixed</c> (409).</summary>
[RequiresPermission(ActivitiesPermissions.Write)]
public sealed record CompleteActivityCommand(Guid Id) : ICommand;

public sealed class CompleteActivityHandler(IActivityRepository activities, TimeProvider clock) : ICommandHandler<CompleteActivityCommand>
{
    public async Task<Result> Handle(CompleteActivityCommand command, CancellationToken cancellationToken)
    {
        var activity = await activities.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return activity is null ? Error.NotFound(ErrorCodes.NotFound) : activity.Complete(clock.GetUtcNow().UtcDateTime);
    }
}

/// <summary>Yeniden açar (idempotent). Not için <c>activity.note_status_fixed</c> (409).</summary>
[RequiresPermission(ActivitiesPermissions.Write)]
public sealed record ReopenActivityCommand(Guid Id) : ICommand;

public sealed class ReopenActivityHandler(IActivityRepository activities) : ICommandHandler<ReopenActivityCommand>
{
    public async Task<Result> Handle(ReopenActivityCommand command, CancellationToken cancellationToken)
    {
        var activity = await activities.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return activity is null ? Error.NotFound(ErrorCodes.NotFound) : activity.Reopen();
    }
}

/// <summary>
/// Kişisel iş özeti (<c>assignedUserId</c> yoksa çağıran): açık, geciken, bugün (kiracı saat dilimi) vadesi gelen ve bu hafta
/// (pazartesi başlangıçlı) tamamlanan iş sayısı. Notlar sayılmaz.
/// </summary>
[RequiresPermission(ActivitiesPermissions.Read)]
public sealed record GetActivitySummaryQuery(Guid? AssignedUserId) : IQuery<ActivitySummaryDto>;

public sealed class GetActivitySummaryHandler(IActivityReadStore store, TenantCalendarService calendars, ICurrentUser user)
    : IQueryHandler<GetActivitySummaryQuery, ActivitySummaryDto>
{
    public async Task<Result<ActivitySummaryDto>> Handle(GetActivitySummaryQuery query, CancellationToken cancellationToken)
    {
        var target = query.AssignedUserId ?? user.UserId;
        if (target is not { } userId)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        var calendar = await calendars.GetCalendarAsync(cancellationToken).ConfigureAwait(false);
        var window = ActivitySummaryWindows.For(calendar, calendars.UtcNow);
        return await store.GetSummaryAsync(userId, window, cancellationToken).ConfigureAwait(false);
    }
}
