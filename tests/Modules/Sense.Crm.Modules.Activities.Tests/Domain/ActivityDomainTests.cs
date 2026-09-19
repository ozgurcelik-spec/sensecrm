using Sense.Crm.Modules.Activities.Application;
using Sense.Crm.Modules.Activities.Domain;
using Sense.Crm.Modules.Activities.Domain.Activities;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Kernel.Time;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Activities.Tests.Domain;

internal static class Fixtures
{
    public static readonly Guid Tenant = Guid.NewGuid();
    public static readonly Guid User = Guid.NewGuid();
    public static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public static Activity NewActivity(
        ActivityType type = ActivityType.Task,
        ActivityStatus? status = null,
        DateTime? dueAt = null,
        DateTime? startAt = null,
        DateTime? endAt = null,
        ActivityRelatedType? relatedType = null,
        Guid? relatedId = null) =>
        Activity.Create(Tenant, type, "  Müşteriyi ara  ", "  detay  ", status, priority: null, dueAt, startAt, endAt, relatedType, relatedId, User, Now).Value;
}

public sealed class ActivityCreationTests
{
    [Fact]
    public void NewTask_IsOpen_WithNormalPriority_AndCleanedText()
    {
        var activity = Fixtures.NewActivity();

        activity.Type.ShouldBe(ActivityType.Task);
        activity.Status.ShouldBe(ActivityStatus.Open);
        activity.Priority.ShouldBe(ActivityPriority.Normal);
        activity.Subject.ShouldBe("Müşteriyi ara");
        activity.Description.ShouldBe("detay");
        activity.CompletedAt.ShouldBeNull();
        activity.AssignedUserId.ShouldBe(Fixtures.User);
        activity.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Note_IsAlwaysCompleted_OnCreation()
    {
        var note = Fixtures.NewActivity(ActivityType.Note);

        note.Status.ShouldBe(ActivityStatus.Completed);
        note.CompletedAt.ShouldBe(Fixtures.Now);
        note.IsNote.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ActivityStatus.Open)]
    [InlineData(ActivityStatus.Cancelled)]
    public void Note_WithAnotherStatus_IsRejected(ActivityStatus status)
    {
        var result = Activity.Create(Fixtures.Tenant, ActivityType.Note, "Not", null, status, null, null, null, null, null, null, Fixtures.User, Fixtures.Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ActivitiesErrors.NoteStatusFixed);
        result.Error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public void Task_CanBeCreatedAsCompleted_WithCompletedAt()
    {
        var done = Activity.Create(Fixtures.Tenant, ActivityType.Call, "Aradım", null, ActivityStatus.Completed, ActivityPriority.High, null, null, null, null, null, Fixtures.User, Fixtures.Now).Value;

        done.Status.ShouldBe(ActivityStatus.Completed);
        done.CompletedAt.ShouldBe(Fixtures.Now);
        done.Priority.ShouldBe(ActivityPriority.High);
    }

    [Fact]
    public void EndBeforeStart_IsRejected_ButEqualOrSingleBoundIsFine()
    {
        var start = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

        var bad = Activity.Create(Fixtures.Tenant, ActivityType.Meeting, "Toplantı", null, null, null, null, start, start.AddMinutes(-1), null, null, Fixtures.User, Fixtures.Now);
        bad.IsFailure.ShouldBeTrue();
        bad.Error.Code.ShouldBe(ActivitiesErrors.InvalidRange);
        bad.Error.Type.ShouldBe(ErrorType.Validation);

        Fixtures.NewActivity(ActivityType.Meeting, startAt: start, endAt: start).Status.ShouldBe(ActivityStatus.Open);
        Fixtures.NewActivity(ActivityType.Meeting, startAt: start).EndAt.ShouldBeNull();
        Fixtures.NewActivity(ActivityType.Meeting, endAt: start).StartAt.ShouldBeNull();
    }

    [Fact]
    public void RelatedType_AndId_MustComeTogether()
    {
        Should.Throw<ArgumentException>(() => Fixtures.NewActivity(relatedType: ActivityRelatedType.Deal));
        Should.Throw<ArgumentException>(() => Fixtures.NewActivity(relatedId: Guid.NewGuid()));

        var id = Guid.NewGuid();
        var linked = Fixtures.NewActivity(relatedType: ActivityRelatedType.Account, relatedId: id);
        (linked.RelatedType, linked.RelatedId).ShouldBe((ActivityRelatedType.Account, id));
    }

    [Fact]
    public void BlankSubject_IsAProgrammingError()
    {
        Should.Throw<ArgumentException>(() => Activity.Create(Fixtures.Tenant, ActivityType.Task, "  ", null, null, null, null, null, null, null, null, Fixtures.User, Fixtures.Now));
    }

    [Fact]
    public void Times_AreNormalizedToUtc_UnspecifiedIsTreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc).ToLocalTime();

        var activity = Fixtures.NewActivity(dueAt: unspecified, startAt: local);

        activity.DueAt.ShouldBe(new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));
        activity.DueAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        activity.StartAt!.Value.ShouldBe(new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));
        activity.StartAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }
}

public sealed class ActivityStatusTransitionTests
{
    private static readonly DateTime Later = Fixtures.Now.AddHours(5);

    [Fact]
    public void Complete_SetsCompletedAt_AndIsIdempotent()
    {
        var task = Fixtures.NewActivity();

        task.Complete(Fixtures.Now).IsSuccess.ShouldBeTrue();
        (task.Status, task.CompletedAt).ShouldBe((ActivityStatus.Completed, Fixtures.Now));

        task.Complete(Later).IsSuccess.ShouldBeTrue();
        task.CompletedAt.ShouldBe(Fixtures.Now, "tekrar tamamlama ilk tamamlanma anını korur");
    }

    [Fact]
    public void Reopen_ClearsCompletedAt_AndWorksFromCompletedAndCancelled()
    {
        var task = Fixtures.NewActivity();
        task.Complete(Fixtures.Now);

        task.Reopen().IsSuccess.ShouldBeTrue();
        (task.Status, task.CompletedAt).ShouldBe((ActivityStatus.Open, (DateTime?)null));

        task.Update(ActivityType.Task, "x", null, ActivityStatus.Cancelled, null, null, null, null, null, null, Fixtures.User, Fixtures.Now).IsSuccess.ShouldBeTrue();
        task.Status.ShouldBe(ActivityStatus.Cancelled);
        task.Reopen().IsSuccess.ShouldBeTrue();
        task.Status.ShouldBe(ActivityStatus.Open);

        task.Reopen().IsSuccess.ShouldBeTrue("açık aktiviteyi yeniden açmak idempotent");
    }

    [Fact]
    public void Note_CannotBeCompletedOrReopened_ThroughTheTransitionEndpoints()
    {
        var note = Fixtures.NewActivity(ActivityType.Note);

        note.Complete(Later).Error.Code.ShouldBe(ActivitiesErrors.NoteStatusFixed);
        note.Reopen().Error.Code.ShouldBe(ActivitiesErrors.NoteStatusFixed);
        (note.Status, note.CompletedAt).ShouldBe((ActivityStatus.Completed, (DateTime?)Fixtures.Now));
    }

    [Fact]
    public void Update_KeepsStatus_WhenNoneGiven_AndAppliesItWhenGiven()
    {
        var task = Fixtures.NewActivity();
        task.Complete(Fixtures.Now);

        task.Update(ActivityType.Task, "Yeni konu", "açıklama", null, ActivityPriority.Low, null, null, null, null, null, Fixtures.User, Later).IsSuccess.ShouldBeTrue();
        (task.Status, task.CompletedAt, task.Subject, task.Priority).ShouldBe((ActivityStatus.Completed, (DateTime?)Fixtures.Now, "Yeni konu", ActivityPriority.Low));

        task.Update(ActivityType.Task, "Yeni konu", null, ActivityStatus.Open, null, null, null, null, null, null, Fixtures.User, Later).IsSuccess.ShouldBeTrue();
        (task.Status, task.CompletedAt, task.Priority).ShouldBe((ActivityStatus.Open, (DateTime?)null, ActivityPriority.Normal));
    }

    [Fact]
    public void Update_ToNoteType_MakesItCompleted_AndNoteRejectsOtherStatuses()
    {
        var task = Fixtures.NewActivity();

        task.Update(ActivityType.Note, "Artık not", null, null, null, null, null, null, null, null, Fixtures.User, Later).IsSuccess.ShouldBeTrue();
        (task.Type, task.Status, task.CompletedAt).ShouldBe((ActivityType.Note, ActivityStatus.Completed, (DateTime?)Later));

        var rejected = task.Update(ActivityType.Note, "Artık not", null, ActivityStatus.Open, null, null, null, null, null, null, Fixtures.User, Later);
        rejected.Error.Code.ShouldBe(ActivitiesErrors.NoteStatusFixed);
        task.Status.ShouldBe(ActivityStatus.Completed);
    }

    [Fact]
    public void FailedUpdate_LeavesTheAggregateUnchanged()
    {
        var start = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        var meeting = Fixtures.NewActivity(ActivityType.Meeting, startAt: start, endAt: start.AddHours(1));

        var result = meeting.Update(ActivityType.Task, "Değişti", "d", null, ActivityPriority.High, null, start, start.AddHours(-1), null, null, Fixtures.User, Later);

        result.Error.Code.ShouldBe(ActivitiesErrors.InvalidRange);
        (meeting.Type, meeting.Subject.StartsWith("Müşteri", StringComparison.Ordinal), meeting.Priority, meeting.EndAt).ShouldBe((ActivityType.Meeting, true, ActivityPriority.Normal, (DateTime?)start.AddHours(1)));
    }

    [Fact]
    public void IsOverdue_OnlyForOpenActivities_WithAPastDueDate()
    {
        var due = Fixtures.Now.AddHours(-1);

        Fixtures.NewActivity(dueAt: due).IsOverdue(Fixtures.Now).ShouldBeTrue();
        Fixtures.NewActivity(dueAt: Fixtures.Now.AddHours(1)).IsOverdue(Fixtures.Now).ShouldBeFalse();
        Fixtures.NewActivity().IsOverdue(Fixtures.Now).ShouldBeFalse("son tarihi olmayan geciken sayılmaz");

        var done = Fixtures.NewActivity(dueAt: due);
        done.Complete(Fixtures.Now);
        done.IsOverdue(Fixtures.Now).ShouldBeFalse("tamamlanan geciken sayılmaz");
    }
}

public sealed class ActivitySummaryWindowsTests
{
    [Fact]
    public void Window_FollowsTheTenantTimeZone_AndMondayWeeks()
    {
        // Cumartesi 19 Eylül 2026 21:30 UTC = Pazar 20 Eylül 00:30 İstanbul → bugün 20 Eylül, hafta pazartesi 14 Eylül'de başlar.
        var now = new DateTimeOffset(2026, 9, 19, 21, 30, 0, TimeSpan.Zero);

        var window = ActivitySummaryWindows.For(TenantCalendar.For("Europe/Istanbul"), now);

        window.NowUtc.ShouldBe(now.UtcDateTime);
        window.TodayStartUtc.ShouldBe(new DateTime(2026, 9, 19, 21, 0, 0, DateTimeKind.Utc));
        window.TomorrowStartUtc.ShouldBe(new DateTime(2026, 9, 20, 21, 0, 0, DateTimeKind.Utc));
        window.WeekStartUtc.ShouldBe(new DateTime(2026, 9, 13, 21, 0, 0, DateTimeKind.Utc));
        window.NextWeekStartUtc.ShouldBe(new DateTime(2026, 9, 20, 21, 0, 0, DateTimeKind.Utc));

        var utc = ActivitySummaryWindows.For(TenantCalendar.Utc, now);
        utc.TodayStartUtc.ShouldBe(new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc));
        utc.WeekStartUtc.ShouldBe(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));
    }
}
