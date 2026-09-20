using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Domain.Deletion;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Entitlements;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Platform.Tests.Domain;

/// <summary>Kiracı yaşam döngüsü: saf değerlendirme tablosu, deneme günü sınırı (kiracı saat dilimi), geçiş tablosunun her hücresi.</summary>
public sealed class LifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowUtc = Now.UtcDateTime;

    // ---- TenantLifecycle.Evaluate: plan tablosunun her satırı ------------------------------------------------------

    [Theory]
    [InlineData("deleted", null, null, "deleted", AccessLevel.None)]
    [InlineData("pending_deletion", null, null, "pending_deletion", AccessLevel.None)]
    [InlineData("pending_deletion", null, "2000-01-01", "pending_deletion", AccessLevel.None)]
    [InlineData("suspended", "blocked", null, "suspended", AccessLevel.None)]
    [InlineData("suspended", "readOnly", null, "suspended", AccessLevel.ReadOnly)]
    [InlineData("suspended", null, null, "suspended", AccessLevel.ReadOnly)]
    [InlineData("suspended", "readOnly", "2000-01-01", "suspended", AccessLevel.ReadOnly)] // askı deneme bitişinden önceliklidir
    [InlineData("suspended", "blocked", "2099-01-01", "suspended", AccessLevel.None)]
    [InlineData("active", null, null, "active", AccessLevel.Full)]
    [InlineData("active", null, "2026-09-21", "trial", AccessLevel.Full)]
    [InlineData("active", null, "2026-09-19", "trial_expired", AccessLevel.ReadOnly)]
    public void Evaluate_FollowsThePlanTable(string raw, string? mode, string? trialEnd, string expectedStatus, AccessLevel expectedAccess)
    {
        DateTimeOffset? trialEndsAt = trialEnd is null ? null : DateTimeOffset.Parse($"{trialEnd}T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var (status, access) = TenantLifecycle.Evaluate(raw, mode, trialEndsAt, Now);

        status.ShouldBe(expectedStatus);
        access.ShouldBe(expectedAccess);
    }

    [Fact]
    public void Evaluate_TrialBoundary_NowEqualsTrialEndsAtMeansExpired_OneSecondBeforeIsStillTrial()
    {
        var end = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        TenantLifecycle.Evaluate("active", null, end, end.AddSeconds(-1)).ShouldBe(("trial", AccessLevel.Full));
        TenantLifecycle.Evaluate("active", null, end, end).ShouldBe(("trial_expired", AccessLevel.ReadOnly));
        TenantLifecycle.Evaluate("active", null, end, end.AddSeconds(1)).ShouldBe(("trial_expired", AccessLevel.ReadOnly));
    }

    [Fact]
    public void TrialEndsOn_IsInclusive_InTheTenantTimeZone_EuropeIstanbul()
    {
        // Europe/Istanbul UTC+3 (yaz saati yok): 2026-12-31 dahil → bitiş anı 2026-12-31T21:00:00Z.
        var endsAt = new DateTimeOffset(EntitlementMath.TrialEndsAtUtc(new DateOnly(2026, 12, 31), "Europe/Istanbul"), TimeSpan.Zero);

        endsAt.ShouldBe(new DateTimeOffset(2026, 12, 31, 21, 0, 0, TimeSpan.Zero));
        TenantLifecycle.Evaluate("active", null, endsAt, new DateTimeOffset(2026, 12, 31, 20, 59, 59, TimeSpan.Zero)).Status.ShouldBe("trial");
        TenantLifecycle.Evaluate("active", null, endsAt, new DateTimeOffset(2026, 12, 31, 21, 0, 0, TimeSpan.Zero)).Status.ShouldBe("trial_expired");
    }

    [Fact]
    public void TrialEndsAt_ForUtcTenants_IsMidnightAfterTheDay()
    {
        new DateTimeOffset(EntitlementMath.TrialEndsAtUtc(new DateOnly(2026, 12, 31), "UTC"), TimeSpan.Zero).ShouldBe(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TrialDaysLeft_CountsInTheTenantTimeZone_ZeroIsTheLastDay_AndOnlyDuringTrial()
    {
        var snapshot = Snapshot(trialEndsOn: new DateOnly(2026, 9, 22), timeZone: "Europe/Istanbul");

        snapshot.TrialDaysLeft("trial", new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero)).ShouldBe(2);
        snapshot.TrialDaysLeft("trial", new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero)).ShouldBe(0);
        snapshot.TrialDaysLeft("active", Now).ShouldBeNull();
        snapshot.TrialDaysLeft("trial_expired", Now).ShouldBeNull();
    }

    // ---- Geçiş tablosu: her hücre (izinli / platform.invalid_transition) -----------------------------------------------

    [Fact]
    public void Transitions_AllowedCells_Work()
    {
        var account = NewAccount();
        account.Suspend("neden", SuspensionModes.ReadOnly, NowUtc).IsSuccess.ShouldBeTrue();      // active -> suspended
        account.Status.ShouldBe(AccountStatuses.Suspended);
        account.Reactivate().IsSuccess.ShouldBeTrue();                                              // suspended -> active
        account.Status.ShouldBe(AccountStatuses.Active);

        var previous = account.MarkPendingDeletion();                                               // active -> pending_deletion
        previous.Value.ShouldBe(AccountStatuses.Active);
        account.RestoreAfterDeletionCancelled(previous.Value).IsSuccess.ShouldBeTrue();              // pending_deletion -> önceki
        account.Status.ShouldBe(AccountStatuses.Active);

        account.Suspend("neden", SuspensionModes.Blocked, NowUtc).IsSuccess.ShouldBeTrue();
        account.MarkPendingDeletion().Value.ShouldBe(AccountStatuses.Suspended);                     // suspended -> pending_deletion
        account.RestoreAfterDeletionCancelled(AccountStatuses.Suspended).IsSuccess.ShouldBeTrue();   // ... ve suspended'a geri döner

        account.MarkPendingDeletion().IsSuccess.ShouldBeTrue();
        account.MarkDeleted(NowUtc).IsSuccess.ShouldBeTrue();                                        // pending_deletion -> deleted
        account.Status.ShouldBe(AccountStatuses.Deleted);
        account.Name.ShouldBe("[deleted]");
        account.Slug.ShouldStartWith("deleted-");
        account.Overrides.ShouldBeNull();
    }

    [Theory]
    [InlineData("suspended", "suspend")]
    [InlineData("pending_deletion", "suspend")]
    [InlineData("deleted", "suspend")]
    [InlineData("active", "reactivate")]
    [InlineData("pending_deletion", "reactivate")]
    [InlineData("deleted", "reactivate")]
    [InlineData("pending_deletion", "deletion")]
    [InlineData("deleted", "deletion")]
    [InlineData("active", "cancel")]
    [InlineData("suspended", "cancel")]
    [InlineData("active", "delete")]
    [InlineData("suspended", "delete")]
    [InlineData("pending_deletion", "subscription")]
    [InlineData("deleted", "subscription")]
    public void Transitions_UndefinedCells_ReturnInvalidTransition_WithFromAndTo(string from, string action)
    {
        var account = InState(from);

        var result = action switch
        {
            "suspend" => account.Suspend("neden", SuspensionModes.ReadOnly, NowUtc),
            "reactivate" => account.Reactivate(),
            "deletion" => (Sense.Crm.Shared.Kernel.Results.Result)account.MarkPendingDeletion(),
            "cancel" => account.RestoreAfterDeletionCancelled(AccountStatuses.Active),
            "delete" => account.MarkDeleted(NowUtc),
            _ => account.ChangeSubscription("business", null, null, TenantOverrides.None, NowUtc),
        };

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(PlatformErrors.InvalidTransition);
        result.Error.Args.ShouldNotBeNull();
        result.Error.Args!["from"].ShouldBe(from);
        result.Error.Args.ContainsKey("to").ShouldBeTrue();
    }

    [Fact]
    public void SystemTenant_CannotBeSuspended_Deleted_OrHaveItsPlanChanged()
    {
        var system = TenantAccount.Create(Guid.NewGuid(), "Platform", "platform", "internal", AccountSources.Bootstrap, isSystem: true, null, null, NowUtc, NowUtc);

        system.Suspend("neden", SuspensionModes.ReadOnly, NowUtc).Error.Code.ShouldBe(PlatformErrors.SystemTenantProtected);
        system.MarkPendingDeletion().Error.Code.ShouldBe(PlatformErrors.SystemTenantProtected);
        system.ChangeSubscription("business", null, null, TenantOverrides.None, NowUtc).Error.Code.ShouldBe(PlatformErrors.SystemTenantProtected);
        system.Status.ShouldBe(AccountStatuses.Active);
    }

    [Fact]
    public void ChangeSubscription_NoOp_ReportsUnchanged_AndRealChangesReportTheirScope()
    {
        var account = NewAccount("starter");

        var same = account.ChangeSubscription("starter", null, null, TenantOverrides.None, NowUtc).Value;
        same.Changed.ShouldBeFalse();
        account.PlanChangedAt.ShouldBeNull();

        var plan = account.ChangeSubscription("business", null, null, TenantOverrides.None, NowUtc).Value;
        (plan.PlanChanged, plan.TrialChanged, plan.OverridesChanged).ShouldBe((true, false, false));

        var trial = account.ChangeSubscription("business", new DateOnly(2026, 10, 1), NowUtc.AddDays(10), TenantOverrides.None, NowUtc).Value;
        (trial.PlanChanged, trial.TrialChanged, trial.OverridesChanged).ShouldBe((false, true, false));

        var overrides = TenantOverrides.FromJson("""{"maxUsers":10}""");
        var over = account.ChangeSubscription("business", new DateOnly(2026, 10, 1), NowUtc.AddDays(10), overrides, NowUtc).Value;
        (over.PlanChanged, over.TrialChanged, over.OverridesChanged).ShouldBe((false, false, true));
    }

    [Fact]
    public void DeletionRequest_Cancel_OnlyWhileScheduledAndBeforeTheDeadline()
    {
        var request = DeletionRequest.Create(Guid.NewGuid(), Guid.NewGuid(), "neden", 30, AccountStatuses.Active, NowUtc);
        request.ScheduledFor.ShouldBe(NowUtc.AddDays(30));

        request.Cancel(null, request.ScheduledFor).Error.Code.ShouldBe(PlatformErrors.DeletionNotCancellable); // süre doldu (now == scheduled_for)
        request.Cancel(null, request.ScheduledFor.AddSeconds(-1)).IsSuccess.ShouldBeTrue();
        request.Status.ShouldBe(DeletionStatuses.Cancelled);
        request.Cancel(null, NowUtc).Error.Code.ShouldBe(PlatformErrors.DeletionNotCancellable);                 // artık scheduled değil

        var running = DeletionRequest.Create(Guid.NewGuid(), null, "neden", 7, AccountStatuses.Suspended, NowUtc);
        running.Start(NowUtc);
        running.Cancel(null, NowUtc).Error.Code.ShouldBe(PlatformErrors.DeletionNotCancellable);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(91)]
    public void DeletionRequest_RetentionMustBeBetween7And90Days(int days) =>
        Should.Throw<ArgumentOutOfRangeException>(() => DeletionRequest.Create(Guid.NewGuid(), null, "neden", days, AccountStatuses.Active, NowUtc));

    [Fact]
    public void DeletionRequest_StepsAccumulateInTheReport_AndAreIdempotent()
    {
        var request = DeletionRequest.Create(Guid.NewGuid(), null, "neden", 7, AccountStatuses.Active, NowUtc);
        request.StepCompleted("a", new Dictionary<string, long> { ["sales.accounts"] = 3 });
        request.StepCompleted("a", new Dictionary<string, long> { ["sales.accounts"] = 99 }); // ikinci kez: yok sayılır
        request.StepCompleted("b", new Dictionary<string, long> { ["audit.audit_log_entries"] = 5 });

        request.HasCompleted("a").ShouldBeTrue();
        request.ErasedSteps.ShouldBe(["a", "b"]);
        request.Report.ShouldNotBeNull().ShouldContain("\"sales.accounts\":3");
        request.Report.ShouldNotContain("99");
    }

    // ---- Etkin durum ifadesi (SQL) == saf fonksiyon -----------------------------------------------------------------

    [Fact]
    public void TenantStatusExpression_MatchesTheLifecycleFunction_ForEveryStoredState()
    {
        var end = NowUtc.AddDays(1);
        foreach (var (raw, mode, trial) in new (string, string?, DateTime?)[]
                 {
                     ("active", null, null), ("active", null, end), ("active", null, NowUtc.AddDays(-1)), ("active", null, NowUtc),
                     ("suspended", "readOnly", end), ("suspended", "blocked", null), ("pending_deletion", null, end), ("deleted", null, null),
                 })
        {
            var account = InState(raw);
            account.ChangeSubscriptionForTest(trial, mode);
            var expected = TenantLifecycle.Evaluate(raw, mode, trial is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null, Now).Status;

            TenantStatusExpression.Effective(NowUtc).Compile()(account).ShouldBe(expected, $"{raw}/{mode}/{trial}");
        }
    }

    // ---- yardımcılar -------------------------------------------------------------------------------------------------

    private static TenantAccount NewAccount(string plan = "internal") =>
        TenantAccount.Create(Guid.NewGuid(), "Acme", "acme-" + Guid.NewGuid().ToString("N")[..6], plan, AccountSources.Signup, isSystem: false, null, null, NowUtc, null);

    private static TenantAccount InState(string state)
    {
        var account = NewAccount();
        switch (state)
        {
            case AccountStatuses.Suspended:
                account.Suspend("neden", SuspensionModes.ReadOnly, NowUtc);
                break;
            case AccountStatuses.PendingDeletion:
                account.MarkPendingDeletion();
                break;
            case AccountStatuses.Deleted:
                account.MarkPendingDeletion();
                account.MarkDeleted(NowUtc);
                break;
            default:
                break;
        }

        return account;
    }

    private static EntitlementSnapshot Snapshot(DateOnly? trialEndsOn, string timeZone) =>
        new(Guid.NewGuid(), "starter", "Starter", "active", null, null, trialEndsOn, timeZone, new Dictionary<string, bool>(), null, new Dictionary<string, int?>());
}

internal static class TenantAccountTestExtensions
{
    /// <summary>Test yardımcısı: deneme bitişi ve askı kipini doğrudan kurar (üretimde yalnız komutlarla değişir).</summary>
    public static void ChangeSubscriptionForTest(this TenantAccount account, DateTime? trialEndsAt, string? mode)
    {
        typeof(TenantAccount).GetProperty(nameof(TenantAccount.TrialEndsAt))!.SetValue(account, trialEndsAt);
        typeof(TenantAccount).GetProperty(nameof(TenantAccount.SuspensionMode))!.SetValue(account, mode);
    }
}
