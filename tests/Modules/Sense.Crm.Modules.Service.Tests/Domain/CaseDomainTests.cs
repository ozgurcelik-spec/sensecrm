using Sense.Crm.Modules.Service.Domain;
using Sense.Crm.Modules.Service.Domain.Cases;
using Sense.Crm.Modules.Service.Domain.Sla;
using Sense.Crm.Shared.Kernel.Results;
using Shouldly;
using Xunit;
using Case = Sense.Crm.Modules.Service.Domain.Cases.Case;

namespace Sense.Crm.Modules.Service.Tests.Domain;

internal static class CaseFixtures
{
    public static readonly Guid Tenant = Guid.NewGuid();
    public static readonly Guid Actor = Guid.NewGuid();
    public static readonly DateTime T0 = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Varsayılan <c>normal</c> SLA: ilk yanıt 480 dk, çözüm 4320 dk.</summary>
    public static readonly SlaMinutes Normal = SlaPolicyDefaults.For(CasePriority.Normal);

    /// <summary>Persist edilmiş gibi: interceptor'ın yazacağı <c>CreatedAt</c> ayarlanır.</summary>
    public static Case NewCase(CasePriority priority = CasePriority.Normal, Guid? assignee = null, DateTime? at = null)
    {
        var created = Case.Create(Tenant, "C-2026-0001", "  Fatura hatalı  ", "  detay  ", null, null, priority, CaseChannel.Email, assignee, SlaPolicyDefaults.For(priority), Actor, at ?? T0);
        created.Case.CreatedAt = at ?? T0;
        return created.Case;
    }

    public static Case InStatus(CaseStatus status, DateTime? closedAt = null)
    {
        var c = NewCase();
        switch (status)
        {
            case CaseStatus.New:
                break;
            case CaseStatus.Open:
                c.ChangeStatus(CaseStatus.Open, null, Actor, T0.AddMinutes(1), 14, null).IsSuccess.ShouldBeTrue();
                break;
            case CaseStatus.Pending:
                c.ChangeStatus(CaseStatus.Pending, null, Actor, T0.AddMinutes(1), 14, null).IsSuccess.ShouldBeTrue();
                break;
            case CaseStatus.Resolved:
                c.ChangeStatus(CaseStatus.Resolved, "Çözüldü", Actor, T0.AddMinutes(1), 14, null).IsSuccess.ShouldBeTrue();
                break;
            case CaseStatus.Closed:
                c.ChangeStatus(CaseStatus.Resolved, "Çözüldü", Actor, T0.AddMinutes(1), 14, null).IsSuccess.ShouldBeTrue();
                c.ChangeStatus(CaseStatus.Closed, null, Actor, closedAt ?? T0.AddMinutes(2), 14, null).IsSuccess.ShouldBeTrue();
                break;
        }

        return c;
    }
}

public sealed class CaseCreationTests
{
    [Fact]
    public void NewCase_IsNew_WithCleanedText_UnassignedByDefault_AndSlaTargetsFromTheCreationMoment()
    {
        var created = Case.Create(CaseFixtures.Tenant, "C-2026-0007", "  Fatura hatalı  ", "  detay  ", null, null, CasePriority.Normal, CaseChannel.Web, null, CaseFixtures.Normal, CaseFixtures.Actor, CaseFixtures.T0);
        var c = created.Case;

        c.Number.ShouldBe("C-2026-0007");
        c.Subject.ShouldBe("Fatura hatalı");
        c.Description.ShouldBe("detay");
        c.Status.ShouldBe(CaseStatus.New);
        c.Priority.ShouldBe(CasePriority.Normal);
        c.Channel.ShouldBe(CaseChannel.Web);
        c.AssignedUserId.ShouldBeNull();
        c.ReopenCount.ShouldBe(0);
        c.FirstResponseAt.ShouldBeNull();
        c.SlaAnchorAt.ShouldBe(CaseFixtures.T0);
        c.FirstResponseDueAt.ShouldBe(CaseFixtures.T0.AddMinutes(480));
        c.DueAt.ShouldBe(CaseFixtures.T0.AddMinutes(4320));
        c.FirstResponseWarnAt.ShouldBe(CaseFixtures.T0.AddMinutes(384), "toplam sürenin %80'i");
        c.ResolutionWarnAt.ShouldBe(CaseFixtures.T0.AddMinutes(3456));
        c.IsActive.ShouldBeTrue();

        created.Event.Type.ShouldBe(CaseEventType.Created);
        created.Event.CaseId.ShouldBe(c.Id);
        created.Event.ToValue.ShouldBe("new");
        created.Event.ActorUserId.ShouldBe(CaseFixtures.Actor);
    }

    [Theory]
    [InlineData(CasePriority.Urgent, 60, 240)]
    [InlineData(CasePriority.High, 240, 1440)]
    [InlineData(CasePriority.Normal, 480, 4320)]
    [InlineData(CasePriority.Low, 1440, 10080)]
    public void DefaultSlaPolicies_MatchTheContract_AndDriveTheTargets(CasePriority priority, int firstResponse, int resolution)
    {
        SlaPolicyDefaults.For(priority).ShouldBe(new SlaMinutes(firstResponse, resolution));

        var c = CaseFixtures.NewCase(priority);

        c.FirstResponseDueAt.ShouldBe(CaseFixtures.T0.AddMinutes(firstResponse));
        c.DueAt.ShouldBe(CaseFixtures.T0.AddMinutes(resolution));
    }

    [Fact]
    public void AssignedUser_IsKept_WhenGiven()
    {
        var user = Guid.NewGuid();
        CaseFixtures.NewCase(assignee: user).AssignedUserId.ShouldBe(user);
    }
}

public sealed class CaseTransitionTableTests
{
    private static readonly CaseStatus[] All = Enum.GetValues<CaseStatus>();

    /// <summary>Plan §3.2 tablosunun 5×5 hücresi: satır = mevcut, sütun = hedef.</summary>
    private static readonly Dictionary<CaseStatus, CaseStatus[]> Allowed = new()
    {
        [CaseStatus.New] = [CaseStatus.Open, CaseStatus.Pending, CaseStatus.Resolved, CaseStatus.Closed],
        [CaseStatus.Open] = [CaseStatus.Pending, CaseStatus.Resolved, CaseStatus.Closed],
        [CaseStatus.Pending] = [CaseStatus.Open, CaseStatus.Resolved, CaseStatus.Closed],
        [CaseStatus.Resolved] = [CaseStatus.Open, CaseStatus.Closed],
        [CaseStatus.Closed] = [CaseStatus.Open],
    };

    public static IEnumerable<object[]> Cells() =>
        All.SelectMany(from => All.Select(to => new object[] { from, to, from != to && Allowed[from].Contains(to) }));

    [Theory]
    [MemberData(nameof(Cells))]
    public void EveryCell_FollowsThePlanTable(CaseStatus from, CaseStatus to, bool allowed)
    {
        CaseRules.IsTransitionAllowed(from, to).ShouldBe(allowed);

        var c = CaseFixtures.InStatus(from);
        var result = c.ChangeStatus(to, "not", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(10), 14, CaseFixtures.Normal);

        if (from == to)
        {
            result.IsSuccess.ShouldBeTrue("aynı duruma geçiş idempotent");
            result.Value.Events.ShouldBeEmpty();
            result.Value.ResolvedNow.ShouldBeFalse();
            c.Status.ShouldBe(from);
        }
        else if (allowed)
        {
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : string.Empty);
            c.Status.ShouldBe(to);
            result.Value.Events.ShouldHaveSingleItem().Type.ShouldBe(CaseEventType.StatusChanged);
        }
        else
        {
            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe(ServiceErrors.InvalidTransition);
            result.Error.Type.ShouldBe(ErrorType.Conflict);
            result.Error.Args!["from"].ShouldBe(EnumText.Camel(from));
            result.Error.Args["to"].ShouldBe(EnumText.Camel(to));
            c.Status.ShouldBe(from, "başarısız geçiş durumu değiştirmez");
        }
    }

    [Fact]
    public void NeverBackToNew()
    {
        foreach (var from in All.Where(s => s != CaseStatus.New))
        {
            CaseRules.IsTransitionAllowed(from, CaseStatus.New).ShouldBeFalse();
        }
    }
}

public sealed class CaseResolutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolving_RequiresANote(string? note)
    {
        var c = CaseFixtures.InStatus(CaseStatus.Open);

        var result = c.ChangeStatus(CaseStatus.Resolved, note, CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(30), 14, null);

        result.Error.Code.ShouldBe(ServiceErrors.ResolutionRequired);
        result.Error.Type.ShouldBe(ErrorType.Validation);
        c.Status.ShouldBe(CaseStatus.Open);
        c.ResolvedAt.ShouldBeNull();
    }

    [Fact]
    public void ClosingWithoutResolving_RequiresANote_ButClosingAResolvedCaseDoesNot()
    {
        var open = CaseFixtures.InStatus(CaseStatus.Open);
        open.ChangeStatus(CaseStatus.Closed, " ", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(30), 14, null).Error.Code.ShouldBe(ServiceErrors.ResolutionRequired);

        var resolved = CaseFixtures.InStatus(CaseStatus.Resolved);
        resolved.ChangeStatus(CaseStatus.Closed, null, CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(30), 14, null).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Resolving_WritesResolvedAtAndNote_AndCountsAsTheFirstResponseWhenThereWasNone()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Open);
        var at = CaseFixtures.T0.AddMinutes(90);

        var result = c.ChangeStatus(CaseStatus.Resolved, "  Fatura düzeltildi.  ", CaseFixtures.Actor, at, 14, null).Value;

        c.ResolvedAt.ShouldBe(at);
        c.ResolutionNote.ShouldBe("Fatura düzeltildi.");
        c.FirstResponseAt.ShouldBe(at, "hiç yorumsuz çözülen talep ilk yanıt SLA'sında sonsuza dek bekliyor kalmaz");
        c.ClosedAt.ShouldBeNull();
        c.ResolutionMinutes.ShouldBe(90);
        result.ResolvedNow.ShouldBeTrue();
        var timeline = result.Events.ShouldHaveSingleItem();
        (timeline.FromValue, timeline.ToValue, timeline.Note).ShouldBe(("open", "resolved", "Fatura düzeltildi."));
    }

    [Fact]
    public void Resolving_KeepsAnExistingFirstResponse()
    {
        var c = CaseFixtures.NewCase();
        c.AddComment(CommentVisibility.Public, "Merhaba", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(5)).IsSuccess.ShouldBeTrue();

        c.ChangeStatus(CaseStatus.Resolved, "ok", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(60), 14, null).IsSuccess.ShouldBeTrue();

        c.FirstResponseAt.ShouldBe(CaseFixtures.T0.AddMinutes(5));
    }

    [Fact]
    public void ClosingAResolvedCase_KeepsResolvedAtAndNote_AndOnlyWritesClosedAt_WithoutAnotherResolvedSignal()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Resolved);
        var resolvedAt = c.ResolvedAt;

        var result = c.ChangeStatus(CaseStatus.Closed, "yok sayılır", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(50), 14, null).Value;

        c.ResolvedAt.ShouldBe(resolvedAt);
        c.ResolutionNote.ShouldBe("Çözüldü");
        c.ClosedAt.ShouldBe(CaseFixtures.T0.AddMinutes(50));
        result.ResolvedNow.ShouldBeFalse("resolvedAt yeniden yazılmadı → CaseResolved yok");
        result.Events.Single().Note.ShouldBeNull();
    }

    [Fact]
    public void ClosingWithoutResolving_WritesResolvedAtAndClosedAt_AndCountsAsResolved()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Pending);
        var at = CaseFixtures.T0.AddMinutes(45);

        var result = c.ChangeStatus(CaseStatus.Closed, "Müşteri vazgeçti", CaseFixtures.Actor, at, 14, null).Value;

        (c.ResolvedAt, c.ClosedAt, c.ResolutionNote).ShouldBe((at, at, "Müşteri vazgeçti"));
        result.ResolvedNow.ShouldBeTrue();
    }

    [Fact]
    public void NoteIsIgnored_OnOtherTransitions()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Open);

        c.ChangeStatus(CaseStatus.Pending, "yok sayılır", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(20), 14, null).Value.Events.Single().Note.ShouldBeNull();
        c.ResolutionNote.ShouldBeNull();
    }
}

public sealed class CaseReopenTests
{
    private static readonly DateTime ClosedAt = CaseFixtures.T0.AddHours(2);

    [Fact]
    public void ResolvedToOpen_HasNoTimeLimit_AndResetsTheResolutionFields()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Resolved);
        var farLater = CaseFixtures.T0.AddDays(400);

        var result = c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, farLater, 14, CaseFixtures.Normal).Value;

        c.Status.ShouldBe(CaseStatus.Open);
        c.ReopenCount.ShouldBe(1);
        (c.ResolvedAt, c.ClosedAt, c.ResolutionNote).ShouldBe((null, null, null));
        c.SlaAnchorAt.ShouldBe(farLater);
        c.DueAt.ShouldBe(farLater.AddMinutes(4320), "çözüm hedefi açılış anından yeniden hesaplanır");
        c.ResolutionWarnAt.ShouldBe(farLater.AddMinutes(3456));
        result.ResolvedNow.ShouldBeFalse();
        result.Events.Single().Note.ShouldBeNull("eski not zaman çizelgesindeki olayda kalır, yeniden açmada yazılmaz");
    }

    [Fact]
    public void Reopening_KeepsTheFirstResponseTargets()
    {
        var c = CaseFixtures.NewCase();
        var firstDue = c.FirstResponseDueAt;
        c.ChangeStatus(CaseStatus.Resolved, "ok", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(10), 14, null);
        var firstResponseAt = c.FirstResponseAt;

        c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, CaseFixtures.T0.AddDays(1), 14, CaseFixtures.Normal);

        c.FirstResponseAt.ShouldBe(firstResponseAt);
        c.FirstResponseDueAt.ShouldBe(firstDue);
    }

    [Fact]
    public void ReopeningTwice_CountsEachTime()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Resolved);
        c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, CaseFixtures.T0.AddDays(1), 14, CaseFixtures.Normal);
        c.ChangeStatus(CaseStatus.Resolved, "tekrar", CaseFixtures.Actor, CaseFixtures.T0.AddDays(2), 14, null);
        c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, CaseFixtures.T0.AddDays(3), 14, CaseFixtures.Normal);

        c.ReopenCount.ShouldBe(2);
    }

    [Fact]
    public void ClosedToOpen_IsAllowed_ThroughTheFourteenthDay_AndRejectedOneSecondLater()
    {
        var onDay14 = CaseFixtures.InStatus(CaseStatus.Closed, ClosedAt);
        onDay14.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, ClosedAt.AddDays(14), 14, CaseFixtures.Normal).IsSuccess.ShouldBeTrue("gün 14 geçer");
        onDay14.ReopenCount.ShouldBe(1);

        var late = CaseFixtures.InStatus(CaseStatus.Closed, ClosedAt);
        var result = late.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, ClosedAt.AddDays(14).AddSeconds(1), 14, CaseFixtures.Normal);
        result.Error.Code.ShouldBe(ServiceErrors.ReopenWindowExpired);
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        late.Status.ShouldBe(CaseStatus.Closed);
        late.ReopenCount.ShouldBe(0);
    }

    [Fact]
    public void ReopenWindow_IsConfigurable()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Closed, ClosedAt);

        c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, ClosedAt.AddDays(3).AddSeconds(1), reopenWindowDays: 3, CaseFixtures.Normal).Error.Code
            .ShouldBe(ServiceErrors.ReopenWindowExpired);
        c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, ClosedAt.AddDays(3), reopenWindowDays: 3, CaseFixtures.Normal).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ClosedWithoutResolving_ThenReopened_ResolvesAgainLater()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Pending);
        c.ChangeStatus(CaseStatus.Closed, "vazgeçti", CaseFixtures.Actor, ClosedAt, 14, null);
        c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, ClosedAt.AddDays(1), 14, CaseFixtures.Normal);

        var again = c.ChangeStatus(CaseStatus.Resolved, "çözüldü", CaseFixtures.Actor, ClosedAt.AddDays(2), 14, null).Value;

        again.ResolvedNow.ShouldBeTrue("yeniden açılıp tekrar çözülen talep yeni bir olay üretir");
        c.ResolvedAt.ShouldBe(ClosedAt.AddDays(2));
    }
}

public sealed class CaseActionRulesTests
{
    [Theory]
    [InlineData(CaseStatus.New, true)]
    [InlineData(CaseStatus.Open, true)]
    [InlineData(CaseStatus.Pending, true)]
    [InlineData(CaseStatus.Resolved, false)]
    [InlineData(CaseStatus.Closed, false)]
    public void UpdatePriorityAndAssign_OnlyWorkOnActiveCases(CaseStatus status, bool active)
    {
        var c = CaseFixtures.InStatus(status);

        c.IsActive.ShouldBe(active);
        var update = c.Update("Yeni konu", null, null, null, null);
        var priority = c.ChangePriority(CasePriority.Urgent, SlaPolicyDefaults.For(CasePriority.Urgent), CaseFixtures.Actor);
        var assign = c.Assign(Guid.NewGuid(), CaseFixtures.Actor);

        if (active)
        {
            update.IsSuccess.ShouldBeTrue();
            priority.IsSuccess.ShouldBeTrue();
            assign.IsSuccess.ShouldBeTrue();
        }
        else
        {
            update.Error.Code.ShouldBe(ServiceErrors.NotActive);
            priority.Error.Code.ShouldBe(ServiceErrors.NotActive);
            assign.Error.Code.ShouldBe(ServiceErrors.NotActive);
            update.Error.Type.ShouldBe(ErrorType.Conflict);
        }
    }

    [Fact]
    public void Update_IsAFullReplace_ButKeepsTheChannelWhenNotGiven()
    {
        var c = CaseFixtures.NewCase();
        c.Update("  Yeni konu  ", "  yeni  ", Guid.NewGuid(), Guid.NewGuid(), null).IsSuccess.ShouldBeTrue();
        c.Channel.ShouldBe(CaseChannel.Email);
        c.Subject.ShouldBe("Yeni konu");

        c.Update("Konu", null, null, null, CaseChannel.Phone).IsSuccess.ShouldBeTrue();

        (c.Description, c.AccountId, c.ContactId, c.Channel).ShouldBe((null, null, null, CaseChannel.Phone));
    }

    [Fact]
    public void Assign_IsIdempotent_AndWritesAnEventWithUserIds()
    {
        var user = Guid.NewGuid();
        var c = CaseFixtures.NewCase();

        var first = c.Assign(user, CaseFixtures.Actor).Value.Events.Single();
        (first.Type, first.FromValue, first.ToValue).ShouldBe((CaseEventType.Assigned, null, user.ToString()));

        c.Assign(user, CaseFixtures.Actor).Value.Events.ShouldBeEmpty();

        var cleared = c.Assign(null, CaseFixtures.Actor).Value.Events.Single();
        (cleared.FromValue, cleared.ToValue).ShouldBe((user.ToString(), null));
        c.AssignedUserId.ShouldBeNull();
    }

    [Fact]
    public void ChangePriority_SameValueIsIdempotent()
    {
        var c = CaseFixtures.NewCase();

        var result = c.ChangePriority(CasePriority.Normal, CaseFixtures.Normal, CaseFixtures.Actor).Value;

        result.Events.ShouldBeEmpty();
    }

    [Fact]
    public void ChangePriority_RecalculatesFromTheOriginalStart_NotFromNow()
    {
        var c = CaseFixtures.NewCase(CasePriority.Normal);

        var up = c.ChangePriority(CasePriority.Urgent, SlaPolicyDefaults.For(CasePriority.Urgent), CaseFixtures.Actor).Value;

        c.Priority.ShouldBe(CasePriority.Urgent);
        c.FirstResponseDueAt.ShouldBe(CaseFixtures.T0.AddMinutes(60), "yükseltme özgün başlangıçtan hesaplanır (hedef geçmişe düşebilir)");
        c.DueAt.ShouldBe(CaseFixtures.T0.AddMinutes(240));
        c.FirstResponseWarnAt.ShouldBe(CaseFixtures.T0.AddMinutes(48));
        c.ResolutionWarnAt.ShouldBe(CaseFixtures.T0.AddMinutes(192));
        var timeline = up.Events.Single();
        (timeline.Type, timeline.FromValue, timeline.ToValue).ShouldBe((CaseEventType.PriorityChanged, "normal", "urgent"));

        c.ChangePriority(CasePriority.Low, SlaPolicyDefaults.For(CasePriority.Low), CaseFixtures.Actor).IsSuccess.ShouldBeTrue();
        c.DueAt.ShouldBe(CaseFixtures.T0.AddMinutes(10080), "düşürme uzatır");
    }

    [Fact]
    public void ChangePriority_AfterTheFirstResponse_KeepsTheFirstResponseTarget()
    {
        var c = CaseFixtures.NewCase(CasePriority.Normal);
        c.AddComment(CommentVisibility.Public, "yanıt", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(10));
        var firstDue = c.FirstResponseDueAt;

        c.ChangePriority(CasePriority.Urgent, SlaPolicyDefaults.For(CasePriority.Urgent), CaseFixtures.Actor);

        c.FirstResponseDueAt.ShouldBe(firstDue);
        c.DueAt.ShouldBe(CaseFixtures.T0.AddMinutes(240));
    }

    [Fact]
    public void ChangePriority_UsesTheReopenAnchorForTheResolutionTarget()
    {
        var c = CaseFixtures.InStatus(CaseStatus.Resolved);
        var reopenedAt = CaseFixtures.T0.AddDays(5);
        c.ChangeStatus(CaseStatus.Open, null, CaseFixtures.Actor, reopenedAt, 14, CaseFixtures.Normal);

        c.ChangePriority(CasePriority.High, SlaPolicyDefaults.For(CasePriority.High), CaseFixtures.Actor);

        c.DueAt.ShouldBe(reopenedAt.AddMinutes(1440));
    }
}

public sealed class CaseCommentRulesTests
{
    [Fact]
    public void FirstPublicComment_WritesTheFirstResponse_AndOpensANewCase_WithTheAuthorAsActor()
    {
        var c = CaseFixtures.NewCase();
        var author = Guid.NewGuid();
        var at = CaseFixtures.T0.AddMinutes(20);

        var outcome = c.AddComment(CommentVisibility.Public, "  Merhaba, bakıyoruz.  ", author, at).Value;

        c.FirstResponseAt.ShouldBe(at);
        c.Status.ShouldBe(CaseStatus.Open);
        outcome.Comment.Body.ShouldBe("Merhaba, bakıyoruz.");
        outcome.Comment.Visibility.ShouldBe(CommentVisibility.Public);
        outcome.Comment.AuthorUserId.ShouldBe(author);
        outcome.Comment.CaseId.ShouldBe(c.Id);
        var opened = outcome.Events.ShouldHaveSingleItem();
        (opened.Type, opened.FromValue, opened.ToValue, opened.ActorUserId).ShouldBe((CaseEventType.StatusChanged, "new", "open", author));
    }

    [Fact]
    public void InternalComment_IsNotAFirstResponse_AndDoesNotOpenTheCase()
    {
        var c = CaseFixtures.NewCase();

        var outcome = c.AddComment(CommentVisibility.Internal, "dahili not", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(20)).Value;

        c.FirstResponseAt.ShouldBeNull();
        c.Status.ShouldBe(CaseStatus.New);
        outcome.Events.ShouldBeEmpty();
    }

    [Fact]
    public void SecondPublicComment_DoesNotChangeTheFirstResponse()
    {
        var c = CaseFixtures.NewCase();
        c.AddComment(CommentVisibility.Internal, "not", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(1));
        c.AddComment(CommentVisibility.Public, "ilk", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(10));

        var second = c.AddComment(CommentVisibility.Public, "ikinci", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(30)).Value;

        c.FirstResponseAt.ShouldBe(CaseFixtures.T0.AddMinutes(10));
        second.Events.ShouldBeEmpty();
    }

    [Fact]
    public void PublicComment_OnAnOpenOrPendingCase_KeepsTheStatus()
    {
        var pending = CaseFixtures.InStatus(CaseStatus.Pending);

        pending.AddComment(CommentVisibility.Public, "yanıt", CaseFixtures.Actor, CaseFixtures.T0.AddMinutes(10)).Value.Events.ShouldBeEmpty();

        pending.Status.ShouldBe(CaseStatus.Pending);
    }

    [Fact]
    public void ResolvedCase_AcceptsComments_WithoutTouchingTheFirstResponse_ButAClosedCaseDoesNot()
    {
        var resolved = CaseFixtures.InStatus(CaseStatus.Resolved);
        var firstResponse = resolved.FirstResponseAt;

        resolved.AddComment(CommentVisibility.Public, "sonradan", CaseFixtures.Actor, CaseFixtures.T0.AddHours(5)).IsSuccess.ShouldBeTrue();
        resolved.FirstResponseAt.ShouldBe(firstResponse);
        resolved.Status.ShouldBe(CaseStatus.Resolved);

        var closed = CaseFixtures.InStatus(CaseStatus.Closed);
        var rejected = closed.AddComment(CommentVisibility.Internal, "yok", CaseFixtures.Actor, CaseFixtures.T0.AddHours(5));
        rejected.Error.Code.ShouldBe(ServiceErrors.Closed);
        rejected.Error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public void CommentBody_IsSensitive_ForTheAuditTrail()
    {
        CaseComment.SensitiveFields.ShouldContain("Body");
    }
}

public sealed class SlaPolicyRulesTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(60, 240, true)]
    [InlineData(240, 240, true)]
    [InlineData(525_600, 525_600, true)]
    [InlineData(0, 10, false)]
    [InlineData(-5, 10, false)]
    [InlineData(241, 240, false)]
    [InlineData(10, 525_601, false)]
    public void Validity_FollowsThePlanBounds(int first, int resolution, bool valid) =>
        SlaPolicy.IsValid(new SlaMinutes(first, resolution)).ShouldBe(valid);

    [Fact]
    public void Update_ChangesTheMinutes()
    {
        var policy = SlaPolicy.CreateDefault(CaseFixtures.Tenant, CasePriority.High);

        policy.Update(new SlaMinutes(30, 120));

        policy.Minutes.ShouldBe(new SlaMinutes(30, 120));
        policy.Priority.ShouldBe(CasePriority.High);
    }

    [Fact]
    public void Priorities_AreListedInEnumOrder() =>
        SlaPolicyDefaults.Priorities.ShouldBe([CasePriority.Low, CasePriority.Normal, CasePriority.High, CasePriority.Urgent]);
}
