using Crm.Modules.Identity.Contracts;
using Crm.Modules.Service.Contracts;
using Crm.Modules.Service.Domain;
using Crm.Modules.Service.Domain.Cases;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Events;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;
using FluentValidation;

namespace Crm.Modules.Service.Application.Cases;

/// <summary>Talep alanları (oluşturma ve güncelleme ortak doğrulaması).</summary>
public interface ICaseFields
{
    string Subject { get; }

    string? Description { get; }

    Guid? AccountId { get; }

    Guid? ContactId { get; }
}

public abstract class CaseFieldsValidator<T> : AbstractValidator<T>
    where T : ICaseFields
{
    protected CaseFieldsValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(ServiceLimits.SubjectMaxLength);
        RuleFor(x => x.Description).MaximumLength(ServiceLimits.DescriptionMaxLength);
        RuleFor(x => x.AccountId).NotEqual(Guid.Empty).When(x => x.AccountId is not null);
        RuleFor(x => x.ContactId).NotEqual(Guid.Empty).When(x => x.ContactId is not null);
    }
}

/// <summary>
/// Yeni talep (durum her zaman <c>new</c>). Sıra: doğrula → firma/kişi/üye kontrolleri → SLA politikası (yoksa tembel tohumla) → numara
/// sayacı (en son adım; aynı transaction) → INSERT + <c>created</c> olayı. <c>assignedUserId</c> verilmezse atanmamış.
/// </summary>
[RequiresPermission(ServicePermissions.CasesWrite)]
public sealed record CreateCaseCommand(
    string Subject,
    string? Description,
    Guid? AccountId,
    Guid? ContactId,
    CasePriority? Priority,
    CaseChannel? Channel,
    Guid? AssignedUserId) : ICommand<Guid>, ICaseFields;

public sealed class CreateCaseValidator : CaseFieldsValidator<CreateCaseCommand>
{
    public CreateCaseValidator() =>
        RuleFor(x => x.AssignedUserId).NotEqual(Guid.Empty).When(x => x.AssignedUserId is not null);
}

public sealed class CreateCaseHandler(
    ICaseRepository cases,
    CaseLinkResolver links,
    CaseAssigneeVerifier assignees,
    SlaPolicyProvider sla,
    ICaseNumberGenerator numbers,
    TenantCalendarService calendars,
    ITenantContext tenant,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<CreateCaseCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateCaseCommand command, CancellationToken cancellationToken)
    {
        var resolved = await links.ResolveAsync(command.AccountId, command.ContactId, currentAccountId: null, currentContactId: null, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var assignee = await assignees.VerifyAsync(command.AssignedUserId, current: null, cancellationToken).ConfigureAwait(false);
        if (assignee.IsFailure)
        {
            return assignee.Error;
        }

        var priority = command.Priority ?? CasePriority.Normal;
        var minutes = await sla.GetAsync(priority, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow().UtcDateTime;
        var calendar = await calendars.GetCalendarAsync(cancellationToken).ConfigureAwait(false);

        // Sayaç en son adım: yukarıdaki doğrulamalar başarısızsa numara hiç tüketilmez; sonrası patlarsa transaction geri alınır.
        var number = await numbers.NextAsync(calendar.LocalDate(now).Year, cancellationToken).ConfigureAwait(false);

        var created = Case.Create(
            tenant.TenantId,
            number,
            command.Subject,
            command.Description,
            resolved.Value.AccountId,
            resolved.Value.ContactId,
            priority,
            command.Channel ?? CaseChannel.Other,
            command.AssignedUserId,
            minutes,
            user.UserId,
            now);

        cases.Add(created.Case);
        cases.AddEvent(created.Event);
        return created.Case.Id;
    }
}

/// <summary>
/// Tam değiştirme (PUT): konu, açıklama, firma/kişi (gönderilmeyen isteğe bağlı alan temizlenir), <c>channel</c> verilmezse korunur.
/// Durum/öncelik/atanan/SLA'yı değiştirmez. Firma/kişi kuralları oluşturmayla aynı (yalnız değiştiyse yeniden doğrulanır; türetme yalnız
/// <c>accountId</c> yoksa ve <c>contactId</c> değiştiyse). Aktif değilse <c>case.not_active</c>.
/// </summary>
[RequiresPermission(ServicePermissions.CasesWrite)]
public sealed record UpdateCaseCommand(
    Guid Id,
    string Subject,
    string? Description,
    Guid? AccountId,
    Guid? ContactId,
    CaseChannel? Channel) : ICommand, ICaseFields;

public sealed class UpdateCaseValidator : CaseFieldsValidator<UpdateCaseCommand>
{
    public UpdateCaseValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class UpdateCaseHandler(ICaseRepository cases, CaseLinkResolver links) : ICommandHandler<UpdateCaseCommand>
{
    public async Task<Result> Handle(UpdateCaseCommand command, CancellationToken cancellationToken)
    {
        var entity = await cases.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!entity.IsActive)
        {
            return Error.Conflict(ServiceErrors.NotActive);
        }

        var resolved = await links.ResolveAsync(command.AccountId, command.ContactId, entity.AccountId, entity.ContactId, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        return entity.Update(command.Subject, command.Description, resolved.Value.AccountId, resolved.Value.ContactId, command.Channel);
    }
}

/// <summary>Yumuşak silme (her durumda); yorumlar/olaylar kalır ama okunamaz.</summary>
[RequiresPermission(ServicePermissions.CasesWrite)]
public sealed record DeleteCaseCommand(Guid Id) : ICommand;

public sealed class DeleteCaseHandler(ICaseRepository cases) : ICommandHandler<DeleteCaseCommand>
{
    public async Task<Result> Handle(DeleteCaseCommand command, CancellationToken cancellationToken)
    {
        var entity = await cases.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        cases.Remove(entity);
        return Result.Success();
    }
}

/// <summary>
/// Durum geçişi (§3.2–3.3): tablo dışı → <c>case.invalid_transition</c> (409); aynı duruma geçiş idempotent 204; çözme / çözülmeden
/// kapatmada not zorunlu; kapalıyı yeniden açma süre sınırlı. <c>resolvedAt</c> yazan geçiş <c>CaseResolved</c>'ı aynı transaction'da
/// outbox'a yazar.
/// </summary>
[RequiresPermission(ServicePermissions.CasesWrite)]
public sealed record ChangeCaseStatusCommand(Guid Id, CaseStatus? Status, string? ResolutionNote) : ICommand;

public sealed class ChangeCaseStatusValidator : AbstractValidator<ChangeCaseStatusCommand>
{
    public ChangeCaseStatusValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Status).NotNull();
        RuleFor(x => x.ResolutionNote).MaximumLength(ServiceLimits.ResolutionNoteMaxLength);
    }
}

public sealed class ChangeCaseStatusHandler(
    ICaseRepository cases,
    SlaPolicyProvider sla,
    IIntegrationEventOutbox outbox,
    ServiceSettings settings,
    ICurrentUser user,
    TimeProvider clock) : ICommandHandler<ChangeCaseStatusCommand>
{
    public async Task<Result> Handle(ChangeCaseStatusCommand command, CancellationToken cancellationToken)
    {
        var entity = await cases.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var target = command.Status!.Value;
        SlaMinutes? slaForReopen = null;
        if (target == CaseStatus.Open && entity.Status is CaseStatus.Resolved or CaseStatus.Closed)
        {
            slaForReopen = await sla.GetAsync(entity.Priority, cancellationToken).ConfigureAwait(false);
        }

        var changed = entity.ChangeStatus(target, command.ResolutionNote, user.UserId, clock.GetUtcNow().UtcDateTime, settings.ReopenWindowDays, slaForReopen);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        foreach (var caseEvent in changed.Value.Events)
        {
            cases.AddEvent(caseEvent);
        }

        if (changed.Value.ResolvedNow)
        {
            var resolvedAt = entity.ResolvedAt!.Value;
            outbox.Enqueue(new CaseResolved(
                entity.TenantId,
                entity.Id,
                entity.Number,
                entity.AccountId,
                entity.ContactId,
                EnumText.Camel(entity.Priority),
                entity.AssignedUserId,
                resolvedAt,
                entity.ResolutionMinutes!.Value,
                entity.EvaluateSla(resolvedAt).IsBreached,
                user.UserId));
        }

        return Result.Success();
    }
}

/// <summary>Öncelik değişimi: SLA hedefleri özgün başlangıçtan yeniden hesaplanır; aynı öncelik idempotent; aktif değilse <c>case.not_active</c>.</summary>
[RequiresPermission(ServicePermissions.CasesWrite)]
public sealed record ChangeCasePriorityCommand(Guid Id, CasePriority? Priority) : ICommand;

public sealed class ChangeCasePriorityValidator : AbstractValidator<ChangeCasePriorityCommand>
{
    public ChangeCasePriorityValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Priority).NotNull();
    }
}

public sealed class ChangeCasePriorityHandler(ICaseRepository cases, SlaPolicyProvider sla, ICurrentUser user) : ICommandHandler<ChangeCasePriorityCommand>
{
    public async Task<Result> Handle(ChangeCasePriorityCommand command, CancellationToken cancellationToken)
    {
        var entity = await cases.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        var priority = command.Priority!.Value;
        var minutes = await sla.GetAsync(priority, cancellationToken).ConfigureAwait(false);
        var changed = entity.ChangePriority(priority, minutes, user.UserId);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        foreach (var caseEvent in changed.Value.Events)
        {
            cases.AddEvent(caseEvent);
        }

        return Result.Success();
    }
}

/// <summary>Atama (<c>assignedUserId</c> <c>null</c> = atamayı kaldır): aktif üye olmalı (<c>owner.not_member</c>); aynı atanan idempotent; aktif değilse <c>case.not_active</c>.</summary>
[RequiresPermission(ServicePermissions.CasesWrite)]
public sealed record AssignCaseCommand(Guid Id, Guid? AssignedUserId) : ICommand;

public sealed class AssignCaseValidator : AbstractValidator<AssignCaseCommand>
{
    public AssignCaseValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.AssignedUserId).NotEqual(Guid.Empty).When(x => x.AssignedUserId is not null);
    }
}

public sealed class AssignCaseHandler(ICaseRepository cases, CaseAssigneeVerifier assignees, ICurrentUser user) : ICommandHandler<AssignCaseCommand>
{
    public async Task<Result> Handle(AssignCaseCommand command, CancellationToken cancellationToken)
    {
        var entity = await cases.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (!entity.IsActive)
        {
            return Error.Conflict(ServiceErrors.NotActive);
        }

        var verified = await assignees.VerifyAsync(command.AssignedUserId, entity.AssignedUserId, cancellationToken).ConfigureAwait(false);
        if (verified.IsFailure)
        {
            return verified;
        }

        var changed = entity.Assign(command.AssignedUserId, user.UserId);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        foreach (var caseEvent in changed.Value.Events)
        {
            cases.AddEvent(caseEvent);
        }

        return Result.Success();
    }
}

/// <summary>
/// Yorum ekleme (§3.5). <c>visibility</c> açıkça verilir (varsayılan yok). Herkese açık ilk yorum <c>firstResponseAt</c>'i yazar ve
/// <c>new → open</c> yapar. Yorum ile talep güncellemesi eşzamanlı yarışırsa (xmin) işleyici tek sefer yeniden yükleyip dener; yine
/// çakışırsa 409. Kapalı talepte <c>case.closed</c>.
/// </summary>
[RequiresPermission(ServicePermissions.CasesWrite)]
public sealed record AddCaseCommentCommand(Guid Id, CommentVisibility? Visibility, string Body) : ICommand<CaseCommentDto>;

public sealed class AddCaseCommentValidator : AbstractValidator<AddCaseCommentCommand>
{
    public AddCaseCommentValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Visibility).NotNull();
        RuleFor(x => x.Body).NotEmpty().MaximumLength(ServiceLimits.CommentBodyMaxLength);
    }
}

public sealed class AddCaseCommentHandler(ICaseRepository cases, IMemberLookup members, ICurrentUser user, TimeProvider clock)
    : ICommandHandler<AddCaseCommentCommand, CaseCommentDto>
{
    private const int MaxAttempts = 2;

    public async Task<Result<CaseCommentDto>> Handle(AddCaseCommentCommand command, CancellationToken cancellationToken)
    {
        if (user.UserId is not { } author)
        {
            return Error.Unauthorized(ErrorCodes.Unauthenticated);
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var entity = await cases.GetByIdAsync(command.Id, cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                return Error.NotFound(ErrorCodes.NotFound);
            }

            var added = entity.AddComment(command.Visibility!.Value, command.Body, author, clock.GetUtcNow().UtcDateTime);
            if (added.IsFailure)
            {
                return added.Error;
            }

            var comment = added.Value.Comment;
            cases.AddComment(comment);
            foreach (var caseEvent in added.Value.Events)
            {
                cases.AddEvent(caseEvent);
            }

            if (!await cases.TrySaveAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var names = await members.GetDisplayNamesAsync([author], cancellationToken).ConfigureAwait(false);
            return new CaseCommentDto(comment.Id, entity.Id, comment.Visibility, comment.Body, author, names.GetValueOrDefault(author), comment.CreatedAt);
        }

        return Error.Conflict(ErrorCodes.ConcurrencyConflict);
    }
}
