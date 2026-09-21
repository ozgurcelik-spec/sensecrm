using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Domain.Deletion;
using Sense.Crm.Shared.Contracts.Entitlements;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Platform.Tests.Domain;

/// <summary>C-SEC2 domain kuralları: korunan (aktif platform yöneticisi üyeli) kiracı, silme talebinin durum makinesi (Start/Retry/CancelBySystem), tombstone'da serbest metin temizliği.</summary>
public sealed class SecurityHardeningDomainTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    // ---- H1: koruma kuralı domain'de ------------------------------------------------------------------------------------

    [Fact]
    public void AnAccountWithAnActivePlatformAdminMember_CanNeverBeSuspendedDeletedOrTombstoned_EvenWithoutTheSystemFlag()
    {
        var account = NewAccount();
        account.IsSystem.ShouldBeFalse();

        account.Suspend("neden", SuspensionModes.ReadOnly, NowUtc, hasActivePlatformAdminMember: true).Error.Code.ShouldBe(PlatformErrors.SystemTenantProtected);
        account.Suspend("neden", SuspensionModes.Blocked, NowUtc, hasActivePlatformAdminMember: true).Error.Code.ShouldBe(PlatformErrors.SystemTenantProtected);
        account.MarkPendingDeletion(hasActivePlatformAdminMember: true).Error.Code.ShouldBe(PlatformErrors.SystemTenantProtected);
        account.Status.ShouldBe(AccountStatuses.Active, "reddedilen geçiş durumu değiştirmez");

        // Tombstone da reddedilir (pending_deletion'daki eski bir talep olsa bile).
        account.MarkPendingDeletion().IsSuccess.ShouldBeTrue();
        account.MarkDeleted(NowUtc, hasActivePlatformAdminMember: true).Error.Code.ShouldBe(PlatformErrors.SystemTenantProtected);
        account.Status.ShouldBe(AccountStatuses.PendingDeletion);
        account.MarkDeleted(NowUtc).IsSuccess.ShouldBeTrue();
    }

    // ---- L1: mezar taşında askı gerekçesi ------------------------------------------------------------------------------

    [Fact]
    public void MarkDeleted_AlsoClearsTheSuspensionReasonAndMode()
    {
        var account = NewAccount();
        account.Suspend("kisisel ali@example.com", SuspensionModes.Blocked, NowUtc).IsSuccess.ShouldBeTrue();
        account.MarkPendingDeletion().IsSuccess.ShouldBeTrue();

        account.MarkDeleted(NowUtc).IsSuccess.ShouldBeTrue();

        account.SuspendedReason.ShouldBeNull();
        account.SuspensionMode.ShouldBeNull();
    }

    // ---- M1: silme talebinin durum makinesi ------------------------------------------------------------------------------

    [Theory]
    [InlineData("scheduled", true)]
    [InlineData("running", true)]
    [InlineData("failed", true)]
    [InlineData("cancelled", false)]
    [InlineData("completed", false)]
    public void Start_IsRejectedForCancelledAndCompletedRequests(string state, bool allowed)
    {
        var request = InState(state);

        var result = request.Start(NowUtc);

        result.IsSuccess.ShouldBe(allowed);
        if (!allowed)
        {
            result.Error.Code.ShouldBe(PlatformErrors.InvalidTransition);
            request.Status.ShouldBe(state, "reddedilen başlatma durumu değiştirmez");
        }
        else
        {
            request.Status.ShouldBe(DeletionStatuses.Running);
        }
    }

    [Fact]
    public void Start_AfterACancel_NeverResurrectsTheRequest_EvenWithAFakeClockPastTheSchedule()
    {
        var request = DeletionRequest.Create(Guid.NewGuid(), null, "neden", 7, AccountStatuses.Active, NowUtc);
        request.Cancel(null, NowUtc.AddDays(1)).IsSuccess.ShouldBeTrue();

        request.Start(NowUtc.AddDays(30)).IsFailure.ShouldBeTrue();

        request.Status.ShouldBe(DeletionStatuses.Cancelled);
        request.StartedAt.ShouldBeNull();
    }

    [Fact]
    public void Retry_IsOnlyForFailedRequests_AndResetsTheAttempts()
    {
        var request = InState("failed");
        request.Fail("x");
        request.Fail("y");
        request.Attempts.ShouldBeGreaterThan(0);

        request.Retry().IsSuccess.ShouldBeTrue();

        request.Attempts.ShouldBe(0);
        request.Status.ShouldBe(DeletionStatuses.Failed);
        InState("scheduled").Retry().Error.Code.ShouldBe(PlatformErrors.DeletionNotRetryable);
        InState("completed").Retry().Error.Code.ShouldBe(PlatformErrors.DeletionNotRetryable);
    }

    [Fact]
    public void CancelBySystem_CancelsAnyActiveRequest_ButNotAFinishedOne()
    {
        foreach (var state in new[] { "scheduled", "running", "failed" })
        {
            var request = InState(state);
            request.CancelBySystem(NowUtc, "erasure.precondition_failed: protected_tenant").IsSuccess.ShouldBeTrue(state);
            request.Status.ShouldBe(DeletionStatuses.Cancelled);
            request.CancelledByUserId.ShouldBeNull();
            request.LastError!.ShouldContain("protected_tenant");
        }

        InState("completed").CancelBySystem(NowUtc, "x").IsFailure.ShouldBeTrue();
        InState("cancelled").CancelBySystem(NowUtc, "x").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void FailPermanently_StopsTheAutomaticRetries_AndRedactReasonIsIdempotent()
    {
        var request = InState("running");

        request.FailPermanently("erasure.precondition_failed", maxAttempts: 10);

        request.Attempts.ShouldBe(10);
        request.IsRunnable(NowUtc.AddYears(1), maxAttempts: 10).ShouldBeFalse();

        request.RedactReason();
        request.RedactReason();
        request.Reason.ShouldBe(PlatformLimits.RedactedReasonPlaceholder);
    }

    private static TenantAccount NewAccount() =>
        TenantAccount.Create(Guid.NewGuid(), "Musteri", "musteri", "internal", AccountSources.Signup, isSystem: false, trialEndsOn: null, trialEndsAt: null, NowUtc, onboardingDismissedAt: null);

    private static DeletionRequest InState(string state)
    {
        var request = DeletionRequest.Create(Guid.NewGuid(), null, "neden", 7, AccountStatuses.Active, NowUtc);
        switch (state)
        {
            case "running":
                request.Start(NowUtc).IsSuccess.ShouldBeTrue();
                break;
            case "failed":
                request.Fail("hata");
                break;
            case "cancelled":
                request.Cancel(null, NowUtc).IsSuccess.ShouldBeTrue();
                break;
            case "completed":
                request.Complete(NowUtc);
                break;
            default:
                break;
        }

        return request;
    }
}
