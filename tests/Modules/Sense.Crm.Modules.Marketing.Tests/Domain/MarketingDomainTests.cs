using Sense.Crm.Modules.Marketing.Application;
using Sense.Crm.Modules.Marketing.Domain;
using Sense.Crm.Modules.Marketing.Domain.Campaigns;
using Sense.Crm.Modules.Marketing.Domain.Members;
using Sense.Crm.Modules.Marketing.Domain.Metrics;
using Sense.Crm.Shared.Kernel.Results;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Marketing.Tests.Domain;

internal static class Fixtures
{
    public static readonly Guid Tenant = Guid.NewGuid();
    public static readonly Guid User = Guid.NewGuid();
    public static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public static Campaign NewCampaign(
        CampaignStatus? status = null,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        string? currency = null,
        decimal? budget = null,
        decimal? expectedRevenue = null,
        decimal? actualCost = null) =>
        Campaign.Create(Tenant, "  Sonbahar E-posta  ", CampaignType.Email, status, startDate, endDate, currency, budget, expectedRevenue, actualCost, "  detay  ", User).Value;

    public static CampaignMember NewMember(CampaignMemberType type = CampaignMemberType.Lead) =>
        CampaignMember.Create(Tenant, Guid.NewGuid(), type, Guid.NewGuid(), User, Now);
}

public sealed class CampaignCreationTests
{
    [Fact]
    public void NewCampaign_IsPlanned_WithTryCurrency_AndCleanedText()
    {
        var campaign = Fixtures.NewCampaign();

        campaign.Status.ShouldBe(CampaignStatus.Planned);
        campaign.Currency.ShouldBe("TRY");
        campaign.Name.ShouldBe("Sonbahar E-posta");
        campaign.Description.ShouldBe("detay");
        campaign.OwnerUserId.ShouldBe(Fixtures.User);
        campaign.Id.ShouldNotBe(Guid.Empty);
        campaign.AcceptsNewMembers.ShouldBeTrue();
    }

    [Theory]
    [InlineData(CampaignStatus.Planned)]
    [InlineData(CampaignStatus.Active)]
    public void Create_AllowsPlannedOrActiveOnly(CampaignStatus status) =>
        Fixtures.NewCampaign(status).Status.ShouldBe(status);

    [Theory]
    [InlineData(CampaignStatus.Completed)]
    [InlineData(CampaignStatus.Cancelled)]
    public void Create_RejectsClosedStatuses(CampaignStatus status)
    {
        var result = Campaign.Create(Fixtures.Tenant, "x", CampaignType.Other, status, null, null, null, null, null, null, null, Fixtures.User);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(MarketingErrors.InvalidCreateStatus);
        result.Error.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void DateRange_EqualDatesAreValid_EarlierEndIsRejected_OneSidedIsValid()
    {
        var day = new DateOnly(2026, 10, 1);

        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Event, null, day, day, null, null, null, null, null, Fixtures.User).IsSuccess.ShouldBeTrue();
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Event, null, day, null, null, null, null, null, null, Fixtures.User).IsSuccess.ShouldBeTrue();
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Event, null, null, day, null, null, null, null, null, Fixtures.User).IsSuccess.ShouldBeTrue();

        var reversed = Campaign.Create(Fixtures.Tenant, "x", CampaignType.Event, null, day, day.AddDays(-1), null, null, null, null, null, Fixtures.User);
        reversed.IsFailure.ShouldBeTrue();
        reversed.Error.Code.ShouldBe(MarketingErrors.InvalidDateRange);
        reversed.Error.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void Amounts_MustBeNonNegative_AndAtMostTheLimit()
    {
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Email, null, null, null, null, 0m, 0m, 0m, null, Fixtures.User).IsSuccess.ShouldBeTrue();
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Email, null, null, null, null, -0.01m, null, null, null, Fixtures.User).Error.Code.ShouldBe(MarketingErrors.InvalidAmount);
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Email, null, null, null, null, null, -1m, null, null, Fixtures.User).Error.Code.ShouldBe(MarketingErrors.InvalidAmount);
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Email, null, null, null, null, null, null, -5m, null, Fixtures.User).Error.Code.ShouldBe(MarketingErrors.InvalidAmount);
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Email, null, null, null, null, MarketingLimits.MaxAmount + 1, null, null, null, Fixtures.User).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" eur ", "EUR")]
    [InlineData(null, "TRY")]
    [InlineData("", "TRY")]
    public void Currency_IsNormalizedToUpperCase_OrDefaultsToTry(string? input, string expected) =>
        Fixtures.NewCampaign(currency: input).Currency.ShouldBe(expected);

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("U1D")]
    public void Currency_MustBeThreeLetters(string input) =>
        Campaign.Create(Fixtures.Tenant, "x", CampaignType.Email, null, null, null, input, null, null, null, null, Fixtures.User).Error.Code.ShouldBe(MarketingErrors.InvalidCurrency);

    [Fact]
    public void Update_IsFullReplace_ClearsOmittedOptionalFields_AndKeepsStatus()
    {
        var campaign = Fixtures.NewCampaign(CampaignStatus.Active, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 9), "USD", 100m, 500m, 40m);

        var result = campaign.Update("Yeni ad", CampaignType.Webinar, null, null, null, null, null, null, null, Fixtures.User);

        result.IsSuccess.ShouldBeTrue();
        (campaign.Name, campaign.Type, campaign.Status, campaign.Currency).ShouldBe(("Yeni ad", CampaignType.Webinar, CampaignStatus.Active, "TRY"));
        (campaign.StartDate, campaign.EndDate, campaign.Budget, campaign.ExpectedRevenue, campaign.ActualCost, campaign.Description).ShouldBe((null, null, null, null, null, null));
    }

    [Fact]
    public void Update_RejectedInput_LeavesCampaignUnchanged()
    {
        var campaign = Fixtures.NewCampaign(startDate: new DateOnly(2026, 10, 1), budget: 10m);

        var result = campaign.Update("Değişti", CampaignType.Event, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 1), "USD", 99m, null, null, null, Fixtures.User);

        result.Error.Code.ShouldBe(MarketingErrors.InvalidDateRange);
        (campaign.Name, campaign.Type, campaign.Currency, campaign.Budget).ShouldBe(("Sonbahar E-posta", CampaignType.Email, "TRY", 10m));
    }
}

public sealed class CampaignStatusTransitionTests
{
    private static readonly CampaignStatus[] All = [CampaignStatus.Planned, CampaignStatus.Active, CampaignStatus.Completed, CampaignStatus.Cancelled];

    private static readonly HashSet<(CampaignStatus, CampaignStatus)> Allowed =
    [
        (CampaignStatus.Planned, CampaignStatus.Active),
        (CampaignStatus.Planned, CampaignStatus.Cancelled),
        (CampaignStatus.Active, CampaignStatus.Completed),
        (CampaignStatus.Active, CampaignStatus.Cancelled),
        (CampaignStatus.Completed, CampaignStatus.Active),
        (CampaignStatus.Cancelled, CampaignStatus.Planned),
    ];

    public static IEnumerable<object[]> Cells() => All.SelectMany(from => All.Select(to => new object[] { from, to }));

    [Theory]
    [MemberData(nameof(Cells))]
    public void EveryCell_MatchesTheBindingTable(CampaignStatus from, CampaignStatus to)
    {
        var campaign = CampaignIn(from);

        var result = campaign.ChangeStatus(to);

        if (from == to)
        {
            result.IsSuccess.ShouldBeTrue("aynı duruma geçiş no-op");
            campaign.Status.ShouldBe(from);
        }
        else if (Allowed.Contains((from, to)))
        {
            result.IsSuccess.ShouldBeTrue();
            campaign.Status.ShouldBe(to);
            Campaign.CanTransition(from, to).ShouldBeTrue();
        }
        else
        {
            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe(MarketingErrors.InvalidStatusTransition);
            result.Error.Type.ShouldBe(ErrorType.Conflict);
            result.Error.Args!["from"].ShouldBe(char.ToLowerInvariant(from.ToString()[0]) + from.ToString()[1..]);
            result.Error.Args["to"].ShouldBe(char.ToLowerInvariant(to.ToString()[0]) + to.ToString()[1..]);
            campaign.Status.ShouldBe(from);
            Campaign.CanTransition(from, to).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(CampaignStatus.Planned, true)]
    [InlineData(CampaignStatus.Active, true)]
    [InlineData(CampaignStatus.Completed, false)]
    [InlineData(CampaignStatus.Cancelled, false)]
    public void ClosedCampaigns_DoNotAcceptNewMembers(CampaignStatus status, bool accepts) =>
        CampaignIn(status).AcceptsNewMembers.ShouldBe(accepts);

    /// <summary>Hedef duruma tablodaki geçerli adımlarla getirir.</summary>
    private static Campaign CampaignIn(CampaignStatus target)
    {
        var campaign = Fixtures.NewCampaign();
        switch (target)
        {
            case CampaignStatus.Active:
                campaign.ChangeStatus(CampaignStatus.Active);
                break;
            case CampaignStatus.Completed:
                campaign.ChangeStatus(CampaignStatus.Active);
                campaign.ChangeStatus(CampaignStatus.Completed);
                break;
            case CampaignStatus.Cancelled:
                campaign.ChangeStatus(CampaignStatus.Cancelled);
                break;
            default:
                break;
        }

        campaign.Status.ShouldBe(target);
        return campaign;
    }
}

public sealed class CampaignMemberTests
{
    [Fact]
    public void NewMember_StartsAsAdded_WithTimestamps()
    {
        var member = Fixtures.NewMember();

        member.Status.ShouldBe(CampaignMemberStatus.Added);
        (member.AddedAt, member.StatusChangedAt).ShouldBe((Fixtures.Now, Fixtures.Now));
        member.AddedByUserId.ShouldBe(Fixtures.User);
        member.IsLocked.ShouldBeFalse();
    }

    [Theory]
    [InlineData(CampaignMemberStatus.Added)]
    [InlineData(CampaignMemberStatus.Sent)]
    [InlineData(CampaignMemberStatus.Responded)]
    [InlineData(CampaignMemberStatus.Unsubscribed)]
    public void ManualStatuses_MoveFreelyBetweenEachOther(CampaignMemberStatus target)
    {
        var member = Fixtures.NewMember();
        member.ChangeStatus(CampaignMemberStatus.Responded, Fixtures.Now.AddHours(1));
        var later = Fixtures.Now.AddHours(2);

        var result = member.ChangeStatus(target, later);

        result.IsSuccess.ShouldBeTrue();
        member.Status.ShouldBe(target);
        if (target == CampaignMemberStatus.Responded)
        {
            result.Value.ShouldBe(MemberStatusChange.Unchanged);
            member.StatusChangedAt.ShouldBe(Fixtures.Now.AddHours(1));
        }
        else
        {
            result.Value.ShouldBe(MemberStatusChange.Changed);
            member.StatusChangedAt.ShouldBe(later);
        }
    }

    [Fact]
    public void Converted_CannotBeSetManually_EvenForLeads()
    {
        var member = Fixtures.NewMember();

        var result = member.ChangeStatus(CampaignMemberStatus.Converted, Fixtures.Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(MarketingErrors.ManualConvertedStatus);
        result.Error.Type.ShouldBe(ErrorType.Validation);
        member.Status.ShouldBe(CampaignMemberStatus.Added);
    }

    [Fact]
    public void ConvertedMember_IsLocked_ButMarkConvertedIsIdempotent()
    {
        var member = Fixtures.NewMember();

        member.MarkConverted(Fixtures.Now.AddHours(1)).ShouldBeTrue();
        member.Status.ShouldBe(CampaignMemberStatus.Converted);
        member.StatusChangedAt.ShouldBe(Fixtures.Now.AddHours(1));
        member.IsLocked.ShouldBeTrue();

        member.ChangeStatus(CampaignMemberStatus.Sent, Fixtures.Now.AddHours(2)).Value.ShouldBe(MemberStatusChange.Locked);
        member.Status.ShouldBe(CampaignMemberStatus.Converted);
        member.MarkConverted(Fixtures.Now.AddHours(3)).ShouldBeFalse();
        member.StatusChangedAt.ShouldBe(Fixtures.Now.AddHours(1));
    }

    [Fact]
    public void Contacts_NeverBecomeConverted()
    {
        var contact = Fixtures.NewMember(CampaignMemberType.Contact);

        contact.MarkConverted(Fixtures.Now).ShouldBeFalse();
        contact.Status.ShouldBe(CampaignMemberStatus.Added);
    }
}

public sealed class MarketingMetricsTests
{
    [Fact]
    public void ZeroDenominators_YieldZeroRates_AndNoCostPerLead()
    {
        var empty = default(MemberCounts);

        MarketingMetrics.ResponseRate(empty).ShouldBe(0m);
        MarketingMetrics.ConversionRate(empty).ShouldBe(0m);
        MarketingMetrics.CostPerLead(1000m, 0).ShouldBeNull();
        MarketingMetrics.CostPerLead(null, 5).ShouldBeNull();
    }

    [Fact]
    public void Rates_AreRoundedHalfAwayFromZeroToTwoDecimals()
    {
        // 14 yanıt / 30 ulaşılan = 46,666… → 46,67; 6 dönüşen / 30 lead = 20.
        var counts = new MemberCounts(LeadCount: 30, ContactCount: 10, Added: 10, Sent: 12, Responded: 8, Converted: 6, Unsubscribed: 4);

        counts.MemberCount.ShouldBe(40);
        counts.ContactedCount.ShouldBe(30);
        counts.ResponseCount.ShouldBe(14);
        MarketingMetrics.ResponseRate(counts).ShouldBe(46.67m);
        MarketingMetrics.ConversionRate(counts).ShouldBe(20m);

        MarketingMetrics.Percent(1, 8).ShouldBe(12.5m);
        MarketingMetrics.Percent(1, 16).ShouldBe(6.25m);
        MarketingMetrics.Percent(1, 3).ShouldBe(33.33m);
        MarketingMetrics.Percent(2, 3).ShouldBe(66.67m);
        MarketingMetrics.Percent(1, 200).ShouldBe(0.5m);
        MarketingMetrics.Percent(1, 800).ShouldBe(0.13m, "0,125 yarıdan yukarı → 0,13");
    }

    [Fact]
    public void CostPerLead_DividesActualCostByLeadCount_TwoDecimals()
    {
        MarketingMetrics.CostPerLead(12000m, 30).ShouldBe(400m);
        MarketingMetrics.CostPerLead(100m, 3).ShouldBe(33.33m);
        MarketingMetrics.CostPerLead(0m, 3).ShouldBe(0m);
    }

    [Fact]
    public void Rates_NeverExceedOneHundred_WhenNumeratorIsASubsetOfDenominator()
    {
        var allResponded = new MemberCounts(LeadCount: 4, ContactCount: 0, Added: 0, Sent: 0, Responded: 1, Converted: 3, Unsubscribed: 0);

        MarketingMetrics.ResponseRate(allResponded).ShouldBe(100m);
        MarketingMetrics.ConversionRate(allResponded).ShouldBe(75m);
    }

    [Fact]
    public void MemberCounts_AddUp()
    {
        var a = new MemberCounts(1, 2, 3, 4, 5, 6, 7);
        var b = new MemberCounts(10, 20, 30, 40, 50, 60, 70);

        (a + b).ShouldBe(new MemberCounts(11, 22, 33, 44, 55, 66, 77));
    }
}

public sealed class EnumListParserTests
{
    [Fact]
    public void ParsesCommaSeparatedValues_CaseInsensitive_Deduplicated()
    {
        EnumListParser.TryParse<CampaignStatus>("planned, ACTIVE,planned", out var values).ShouldBeTrue();
        values.ShouldBe([CampaignStatus.Planned, CampaignStatus.Active]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyInput_MeansNoFilter(string? raw)
    {
        EnumListParser.TryParse<CampaignStatus>(raw, out var values).ShouldBeTrue();
        values.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("planned,bogus")]
    [InlineData("1")]
    public void UnknownValue_IsRejected(string raw) => EnumListParser.TryParse<CampaignStatus>(raw, out _).ShouldBeFalse();
}
