using Crm.Modules.Activities.Contracts;
using Crm.Modules.Activities.Domain;
using Crm.Modules.Activities.Domain.Activities;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Contracts;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Activities.Infrastructure.Persistence;

/// <summary>
/// <see cref="IActivityCreator"/> uygulaması: workflow'un (Worker'da, sistem bağlamında) görev/not açmasını sağlar. HTTP
/// komutlarıyla aynı kurallar geçerlidir: atanan aktif üye (<c>owner.not_member</c>), ilişkili kayıt aktif kiracıda var
/// (<c>activity.related_not_found</c>); denetim kaydı interceptor'dan gelir. Doğrudan kaydeder (komut pipeline'ı yoktur).
/// </summary>
public sealed class ActivityCreator(
    ActivitiesDbContext db,
    ITenantContext tenant,
    IMemberLookup members,
    IRecordLookup records,
    TimeProvider clock) : IActivityCreator
{
    public Task<Result<Guid>> CreateTaskAsync(Guid assigneeUserId, string subject, string? description, DateTime? dueAt, RecordRef related, CancellationToken cancellationToken = default) =>
        CreateAsync(ActivityType.Task, assigneeUserId, subject, description, dueAt, related, cancellationToken);

    public Task<Result<Guid>> CreateNoteAsync(Guid assigneeUserId, string subject, string? description, RecordRef related, CancellationToken cancellationToken = default) =>
        CreateAsync(ActivityType.Note, assigneeUserId, subject, description, dueAt: null, related, cancellationToken);

    private async Task<Result<Guid>> CreateAsync(ActivityType type, Guid assigneeUserId, string subject, string? description, DateTime? dueAt, RecordRef related, CancellationToken ct)
    {
        if (!await members.IsActiveMemberAsync(assigneeUserId, ct).ConfigureAwait(false))
        {
            return Error.Validation(ActivitiesErrors.OwnerNotMember);
        }

        if (!await records.ExistsAsync(related.Type, related.Id, ct).ConfigureAwait(false))
        {
            return Error.NotFound(ActivitiesErrors.RelatedNotFound);
        }

        var trimmedSubject = subject.Trim();
        if (trimmedSubject.Length > ActivityLimits.SubjectMaxLength)
        {
            trimmedSubject = trimmedSubject[..ActivityLimits.SubjectMaxLength];
        }

        var created = Activity.Create(
            tenant.TenantId,
            type,
            trimmedSubject,
            description is { Length: > ActivityLimits.DescriptionMaxLength } ? description[..ActivityLimits.DescriptionMaxLength] : description,
            status: null,
            priority: null,
            dueAt,
            startAt: null,
            endAt: null,
            ToRelatedType(related.Type),
            related.Id,
            assigneeUserId,
            clock.GetUtcNow().UtcDateTime);
        if (created.IsFailure)
        {
            return created.Error;
        }

        db.Activities.Add(created.Value);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return created.Value.Id;
    }

    private static ActivityRelatedType ToRelatedType(RecordType type) => type switch
    {
        RecordType.Account => ActivityRelatedType.Account,
        RecordType.Contact => ActivityRelatedType.Contact,
        RecordType.Lead => ActivityRelatedType.Lead,
        RecordType.Deal => ActivityRelatedType.Deal,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
