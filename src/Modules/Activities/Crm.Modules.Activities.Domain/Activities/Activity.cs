using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Activities.Domain.Activities;

public enum ActivityType
{
    Task,
    Call,
    Meeting,
    Note,
}

public enum ActivityStatus
{
    Open,
    Completed,
    Cancelled,
}

public enum ActivityPriority
{
    Low,
    Normal,
    High,
}

/// <summary>Aktivitenin bağlanabildiği Sales kayıt türü (Sales.Contracts <c>RecordType</c>'ın domain karşılığı).</summary>
public enum ActivityRelatedType
{
    Account,
    Contact,
    Lead,
    Deal,
}

/// <summary>
/// Aktivite (Zoho "Activity"): görev, arama, toplantı veya not. İsteğe bağlı olarak bir firmaya/kişiye/potansiyele/fırsata
/// bağlanır (ilişki yumuşaktır: bağlı kayıt silinse de aktivite kalır) ve bir kullanıcıya atanır.
/// Kurallar: <c>note</c> her zaman <c>completed</c>'dır; <c>endAt &gt;= startAt</c>; <c>completedAt</c> yalnız tamamlanmış durumda doludur.
/// Zamanlar UTC'dir. Kişisel veri alanı yoktur (denetim kaydında maskelenecek alan yok).
/// </summary>
public sealed class Activity : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private Activity()
    {
    }

    private Activity(Guid id, Guid tenantId, ActivityType type, string subject, Guid assignedUserId) : base(id, tenantId)
    {
        Type = type;
        Subject = subject;
        AssignedUserId = assignedUserId;
    }

    public ActivityType Type { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public ActivityStatus Status { get; private set; }

    public ActivityPriority Priority { get; private set; } = ActivityPriority.Normal;

    /// <summary>Son tarih (görev için); UTC.</summary>
    public DateTime? DueAt { get; private set; }

    /// <summary>Başlangıç (arama/toplantı); UTC.</summary>
    public DateTime? StartAt { get; private set; }

    /// <summary>Bitiş (arama/toplantı); UTC, <see cref="StartAt"/>'ten önce olamaz.</summary>
    public DateTime? EndAt { get; private set; }

    public ActivityRelatedType? RelatedType { get; private set; }

    public Guid? RelatedId { get; private set; }

    public Guid AssignedUserId { get; private set; }

    /// <summary>Tamamlanma anı (UTC); yalnız <see cref="ActivityStatus.Completed"/> iken dolu.</summary>
    public DateTime? CompletedAt { get; private set; }

    public bool IsNote => Type == ActivityType.Note;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    /// <summary>
    /// Yeni aktivite. <paramref name="status"/> verilmezse <c>open</c> (not için <c>completed</c>); not için başka bir durum
    /// <c>activity.note_status_fixed</c>. <c>endAt &lt; startAt</c> → <c>activity.invalid_range</c>. Zamanlar UTC'ye normalleştirilir
    /// (<c>Kind</c> belirtilmemişse UTC kabul edilir).
    /// </summary>
    public static Result<Activity> Create(
        Guid tenantId,
        ActivityType type,
        string subject,
        string? description,
        ActivityStatus? status,
        ActivityPriority? priority,
        DateTime? dueAt,
        DateTime? startAt,
        DateTime? endAt,
        ActivityRelatedType? relatedType,
        Guid? relatedId,
        Guid assignedUserId,
        DateTime nowUtc)
    {
        var activity = new Activity(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            type,
            Guard.MaxLength(Guard.NotEmpty(subject), ActivityLimits.SubjectMaxLength),
            Guard.NotDefault(assignedUserId));

        var applied = activity.Apply(description, priority ?? ActivityPriority.Normal, dueAt, startAt, endAt, relatedType, relatedId);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var placed = activity.SetStatus(status ?? (type == ActivityType.Note ? ActivityStatus.Completed : ActivityStatus.Open), nowUtc);
        return placed.IsFailure ? placed.Error : activity;
    }

    /// <summary>
    /// Tam değiştirme (PUT): tür dahil tüm alanlar. <paramref name="status"/> verilmezse mevcut durum korunur (türü nota
    /// çevirmek durumu <c>completed</c> yapar); verilirse geçiş uygulanır (not için <c>completed</c> dışı reddedilir).
    /// <paramref name="priority"/> verilmezse <c>normal</c>.
    /// </summary>
    public Result Update(
        ActivityType type,
        string subject,
        string? description,
        ActivityStatus? status,
        ActivityPriority? priority,
        DateTime? dueAt,
        DateTime? startAt,
        DateTime? endAt,
        ActivityRelatedType? relatedType,
        Guid? relatedId,
        Guid assignedUserId,
        DateTime nowUtc)
    {
        var cleanSubject = Guard.MaxLength(Guard.NotEmpty(subject), ActivityLimits.SubjectMaxLength);
        var assignee = Guard.NotDefault(assignedUserId);

        var target = status ?? (type == ActivityType.Note ? ActivityStatus.Completed : Status);
        if (type == ActivityType.Note && target != ActivityStatus.Completed)
        {
            return Error.Conflict(ActivitiesErrors.NoteStatusFixed);
        }

        var applied = Apply(description, priority ?? ActivityPriority.Normal, dueAt, startAt, endAt, relatedType, relatedId);
        if (applied.IsFailure)
        {
            return applied;
        }

        Type = type;
        Subject = cleanSubject;
        AssignedUserId = assignee;
        return SetStatus(target, nowUtc);
    }

    /// <summary>Tamamlar (idempotent: zaten tamamlanmışsa <see cref="CompletedAt"/> korunur). Not için <c>activity.note_status_fixed</c>.</summary>
    public Result Complete(DateTime nowUtc) => IsNote
        ? Error.Conflict(ActivitiesErrors.NoteStatusFixed)
        : SetStatus(ActivityStatus.Completed, nowUtc);

    /// <summary>Yeniden açar (tamamlanmış veya iptal edilmişi <c>open</c> yapar, <see cref="CompletedAt"/> temizlenir); not için <c>note_status_fixed</c>.</summary>
    public Result Reopen() => IsNote
        ? Error.Conflict(ActivitiesErrors.NoteStatusFixed)
        : SetStatus(ActivityStatus.Open, default);

    /// <summary>Açık ve son tarihi geçmiş mi (<c>isOverdue</c>).</summary>
    public bool IsOverdue(DateTime nowUtc) => Status == ActivityStatus.Open && DueAt is { } due && due < nowUtc;

    /// <summary>Kullanıcı/istemci girdisi kaynaklı zamanı UTC'ye çevirir: <c>Local</c> → UTC, <c>Unspecified</c> → UTC kabul.</summary>
    public static DateTime? ToUtc(DateTime? value) => value is null
        ? null
        : value.Value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        };

    private Result Apply(
        string? description,
        ActivityPriority priority,
        DateTime? dueAt,
        DateTime? startAt,
        DateTime? endAt,
        ActivityRelatedType? relatedType,
        Guid? relatedId)
    {
        var start = ToUtc(startAt);
        var end = ToUtc(endAt);
        if (start is { } s && end is { } e && e < s)
        {
            return Error.Validation(ActivitiesErrors.InvalidRange);
        }

        Guard.Against(relatedType is null != relatedId is null, "relatedType and relatedId must be provided together.");

        Description = Clean(description, ActivityLimits.DescriptionMaxLength);
        Priority = priority;
        DueAt = ToUtc(dueAt);
        StartAt = start;
        EndAt = end;
        RelatedType = relatedType;
        RelatedId = relatedId is { } id ? Guard.NotDefault(id) : null;
        return Result.Success();
    }

    private Result SetStatus(ActivityStatus target, DateTime nowUtc)
    {
        if (IsNote && target != ActivityStatus.Completed)
        {
            return Error.Conflict(ActivitiesErrors.NoteStatusFixed);
        }

        if (target == ActivityStatus.Completed)
        {
            CompletedAt = Status == ActivityStatus.Completed && CompletedAt is not null ? CompletedAt : nowUtc;
        }
        else
        {
            CompletedAt = null;
        }

        Status = target;
        return Result.Success();
    }

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
    }
}
