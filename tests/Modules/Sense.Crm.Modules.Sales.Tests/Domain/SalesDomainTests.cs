using Sense.Crm.Modules.Sales.Domain;
using Sense.Crm.Modules.Sales.Domain.Accounts;
using Sense.Crm.Modules.Sales.Domain.Contacts;
using Sense.Crm.Modules.Sales.Domain.Deals;
using Sense.Crm.Modules.Sales.Domain.Leads;
using Sense.Crm.Modules.Sales.Domain.Pipelines;
using Sense.Crm.Shared.Kernel.Results;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Sales.Tests.Domain;

internal static class Fixtures
{
    public static readonly Guid Tenant = Guid.NewGuid();
    public static readonly Guid Owner = Guid.NewGuid();
    public static readonly DateTime Now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    public static Pipeline NewPipeline(string locale = "tr") =>
        Pipeline.Create(Tenant, DefaultPipelineTemplate.PipelineName(locale), isDefault: true, DefaultPipelineTemplate.Stages(locale)).Value;

    public static PipelineStage Stage(this Pipeline pipeline, StageKind kind) => pipeline.Stages.Single(s => s.Kind == kind);

    public static Deal NewDeal(Pipeline pipeline, PipelineStage? stage = null) =>
        Deal.Create(Tenant, "Yeni fırsat", Guid.NewGuid(), Owner, pipeline, stage, contactId: null, amount: 1000m, currency: null, closingDate: null, lostReason: null, Now).Value;
}

public sealed class LeadTests
{
    private static Lead NewLead() => Lead.Create(Fixtures.Tenant, "Ayşe", "Yılmaz", "Acme A.Ş.", Fixtures.Owner, email: "Ayse@Acme.com", phone: "0532 000 00 00");

    [Fact]
    public void New_Lead_StartsAsNew_WithNormalizedFields()
    {
        var lead = NewLead();

        lead.Status.ShouldBe(LeadStatus.New);
        lead.Source.ShouldBe(LeadSource.Other);
        lead.Email.ShouldBe("ayse@acme.com");
        lead.FullName.ShouldBe("Ayşe Yılmaz");
        lead.IsConverted.ShouldBeFalse();
    }

    [Fact]
    public void Convert_MarksLeadConverted_AndKeepsTheCreatedRecordIds()
    {
        var lead = NewLead();
        var (accountId, contactId, dealId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        lead.Convert(accountId, contactId, dealId, Fixtures.Now).IsSuccess.ShouldBeTrue();

        lead.Status.ShouldBe(LeadStatus.Converted);
        lead.ConvertedAccountId.ShouldBe(accountId);
        lead.ConvertedContactId.ShouldBe(contactId);
        lead.ConvertedDealId.ShouldBe(dealId);
        lead.ConvertedAt.ShouldBe(Fixtures.Now);
    }

    [Fact]
    public void Convert_WithoutDeal_LeavesDealIdEmpty()
    {
        var lead = NewLead();

        lead.Convert(Guid.NewGuid(), Guid.NewGuid(), dealId: null, Fixtures.Now).IsSuccess.ShouldBeTrue();

        lead.ConvertedDealId.ShouldBeNull();
    }

    [Fact]
    public void ConvertedLead_CannotBeConvertedAgain_OrUpdated()
    {
        var lead = NewLead();
        var accountId = Guid.NewGuid();
        lead.Convert(accountId, Guid.NewGuid(), null, Fixtures.Now).IsSuccess.ShouldBeTrue();

        var again = lead.Convert(Guid.NewGuid(), Guid.NewGuid(), null, Fixtures.Now);
        again.Error.Code.ShouldBe(SalesErrors.LeadAlreadyConverted);
        again.Error.Type.ShouldBe(ErrorType.Conflict);

        var update = lead.Update("X", "Y", "Z", Fixtures.Owner, LeadSource.Web, LeadStatus.Qualified, LeadRating.Hot, null, null);
        update.Error.Code.ShouldBe(SalesErrors.LeadAlreadyConverted);

        lead.LastName.ShouldBe("Yılmaz");
        lead.ConvertedAccountId.ShouldBe(accountId);
    }

    [Fact]
    public void Update_CannotSetStatusToConverted_Directly()
    {
        var lead = NewLead();

        lead.Update("A", "B", "C", Fixtures.Owner, LeadSource.Web, LeadStatus.Converted, null, null, null).Error.Code.ShouldBe(SalesErrors.LeadStatusNotSettable);
        lead.Status.ShouldBe(LeadStatus.New);
    }

    [Fact]
    public void Update_ChangesFields_ForOpenLead()
    {
        var lead = NewLead();

        lead.Update("A", "B", "C", Fixtures.Owner, LeadSource.Referral, LeadStatus.Qualified, LeadRating.Warm, "x@y.com", null).IsSuccess.ShouldBeTrue();

        (lead.Company, lead.Status, lead.Source, lead.Rating).ShouldBe(("C", LeadStatus.Qualified, LeadSource.Referral, (LeadRating?)LeadRating.Warm));
    }
}

public sealed class PersonalDataTests
{
    [Fact]
    public void PersonalFields_AreMarkedSensitive_ForAuditMasking()
    {
        Account.SensitiveFields.ShouldBe(["Email", "Phone"], ignoreOrder: true);
        Contact.SensitiveFields.ShouldBe(["Email", "Phone", "Mobile"], ignoreOrder: true);
        Lead.SensitiveFields.ShouldBe(["Email", "Phone"], ignoreOrder: true);
    }

    [Fact]
    public void Address_IsFlattenedAndNormalized()
    {
        var account = Account.Create(Fixtures.Tenant, "Acme", Fixtures.Owner, billingAddress: new Address(" Cad. 1 ", "  ", null, null, "TR"));

        account.BillingStreet.ShouldBe("Cad. 1");
        account.BillingCity.ShouldBeNull();
        account.BillingAddress.ShouldBe(new Address("Cad. 1", null, null, null, "TR"));
        Account.Create(Fixtures.Tenant, "Empty", Fixtures.Owner, billingAddress: new Address(" ", null, null, null, null)).BillingAddress.ShouldBeNull();
    }
}

public sealed class PipelineTests
{
    [Theory]
    [InlineData("tr", "Nitelendirme", "Kazanıldı", "Kaybedildi")]
    [InlineData("en", "Qualification", "Closed Won", "Closed Lost")]
    public void DefaultTemplate_HasSixStages_InOrganizationLanguage(string locale, string first, string won, string lost)
    {
        var pipeline = Fixtures.NewPipeline(locale);

        pipeline.Stages.Select(s => s.Order).ShouldBe([0, 1, 2, 3, 4, 5]);
        pipeline.Stages.Select(s => s.Probability).ShouldBe([10, 20, 50, 75, 100, 0]);
        pipeline.Stages[0].Name.ShouldBe(first);
        pipeline.Stage(StageKind.Won).Name.ShouldBe(won);
        pipeline.Stage(StageKind.Lost).Name.ShouldBe(lost);
        pipeline.Stages.Count(s => s.Kind == StageKind.Open).ShouldBe(4);
        pipeline.FirstOpenStage!.Name.ShouldBe(first);
    }

    [Fact]
    public void Pipeline_RequiresExactlyOneWonAndOneLostStage()
    {
        var open = new StageDefinition(null, "A", 10, StageKind.Open);
        var won = new StageDefinition(null, "W", 100, StageKind.Won);
        var lost = new StageDefinition(null, "L", 0, StageKind.Lost);

        Pipeline.Create(Fixtures.Tenant, "P", false, [open, won, lost]).IsSuccess.ShouldBeTrue();
        Pipeline.Create(Fixtures.Tenant, "P", false, [open, won]).IsFailure.ShouldBeTrue("lost aşama yok");
        Pipeline.Create(Fixtures.Tenant, "P", false, [open, lost]).IsFailure.ShouldBeTrue("won aşama yok");
        Pipeline.Create(Fixtures.Tenant, "P", false, [open, won, won, lost]).IsFailure.ShouldBeTrue("iki won");
        Pipeline.Create(Fixtures.Tenant, "P", false, [open, won, lost, lost]).IsFailure.ShouldBeTrue("iki lost");
        Pipeline.Create(Fixtures.Tenant, "P", false, [won, lost]).IsFailure.ShouldBeTrue("açık aşama yok");
        Pipeline.Create(Fixtures.Tenant, "P", false, [new StageDefinition(null, "  ", 10, StageKind.Open), won, lost]).IsFailure.ShouldBeTrue("boş ad");
        Pipeline.Create(Fixtures.Tenant, "P", false, [new StageDefinition(null, "A", 101, StageKind.Open), won, lost]).IsFailure.ShouldBeTrue("olasılık > 100");
    }

    [Fact]
    public void ReplaceStages_ReordersUpdatesAddsAndRemoves()
    {
        var pipeline = Fixtures.NewPipeline();
        var stages = pipeline.Stages.ToList();
        var definitions = new List<StageDefinition>
        {
            new(stages[2].Id, "Teklif (yeni ad)", 55, StageKind.Open),
            new(stages[0].Id, stages[0].Name, stages[0].Probability, StageKind.Open),
            new(null, "Yeni aşama", 30, StageKind.Open),
            new(stages[4].Id, stages[4].Name, 100, StageKind.Won),
            new(stages[5].Id, stages[5].Name, 0, StageKind.Lost),
        };

        var removed = pipeline.ReplaceStages(definitions, new HashSet<Guid>());

        removed.IsSuccess.ShouldBeTrue();
        removed.Value.Select(s => s.Id).ShouldBe([stages[1].Id, stages[3].Id], ignoreOrder: true);
        pipeline.Stages.Select(s => s.Name).ShouldBe(["Teklif (yeni ad)", stages[0].Name, "Yeni aşama", stages[4].Name, stages[5].Name]);
        pipeline.Stages.Select(s => s.Order).ShouldBe([0, 1, 2, 3, 4]);
        pipeline.Stages[0].Probability.ShouldBe(55);
    }

    [Fact]
    public void ReplaceStages_RefusesToRemoveAStageInUse_AndUnknownIds()
    {
        var pipeline = Fixtures.NewPipeline();
        var stages = pipeline.Stages.ToList();
        var withoutNegotiation = stages.Where(s => s.Id != stages[3].Id).Select(s => new StageDefinition(s.Id, s.Name, s.Probability, s.Kind)).ToList();

        var inUse = pipeline.ReplaceStages(withoutNegotiation, new HashSet<Guid> { stages[3].Id });
        inUse.Error.Code.ShouldBe(SalesErrors.StageInUse);
        pipeline.Stages.Count.ShouldBe(6, "başarısız değişiklik hiçbir şeyi değiştirmez");

        var unknown = pipeline.ReplaceStages([.. withoutNegotiation, new StageDefinition(Guid.NewGuid(), "Yabancı", 10, StageKind.Open)], new HashSet<Guid>());
        unknown.Error.Code.ShouldBe(SalesErrors.StageNotFound);
    }

    [Fact]
    public void Stage_Probability_ComesFromTheStage()
    {
        var pipeline = Fixtures.NewPipeline();

        pipeline.Stages.Where(s => s.Kind == StageKind.Open).Select(s => s.Probability).ShouldBe([10, 20, 50, 75]);
        pipeline.Stage(StageKind.Won).Probability.ShouldBe(100);
        pipeline.Stage(StageKind.Lost).Probability.ShouldBe(0);
    }
}

public sealed class DealTests
{
    [Fact]
    public void NewDeal_StartsInFirstOpenStage_WithDefaultCurrency()
    {
        var pipeline = Fixtures.NewPipeline();

        var deal = Fixtures.NewDeal(pipeline);

        deal.StageId.ShouldBe(pipeline.FirstOpenStage!.Id);
        deal.PipelineId.ShouldBe(pipeline.Id);
        deal.Currency.ShouldBe("TRY");
        deal.ClosedAt.ShouldBeNull();
        deal.DomainEvents.ShouldBeEmpty("oluşturma aşama değişimi sayılmaz");
    }

    [Fact]
    public void MoveToStage_RaisesDealStageChanged_WithTargetKind()
    {
        var pipeline = Fixtures.NewPipeline();
        var deal = Fixtures.NewDeal(pipeline);
        var from = deal.StageId;
        var proposal = pipeline.Stages[2];

        deal.MoveToStage(proposal, null, Fixtures.Now).IsSuccess.ShouldBeTrue();

        deal.StageId.ShouldBe(proposal.Id);
        deal.ClosedAt.ShouldBeNull();
        var changed = deal.DomainEvents.OfType<DealStageChanged>().Single();
        (changed.DealId, changed.FromStageId, changed.ToStageId, changed.ToKind).ShouldBe((deal.Id, (Guid?)from, proposal.Id, StageKind.Open));
    }

    [Fact]
    public void MoveToSameStage_IsANoOp_WithoutEvent()
    {
        var pipeline = Fixtures.NewPipeline();
        var deal = Fixtures.NewDeal(pipeline);

        deal.MoveToStage(pipeline.FirstOpenStage!, null, Fixtures.Now).IsSuccess.ShouldBeTrue();

        deal.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Winning_WritesClosedAt_AndClearsLostReason()
    {
        var pipeline = Fixtures.NewPipeline();
        var deal = Fixtures.NewDeal(pipeline);

        deal.MoveToStage(pipeline.Stage(StageKind.Won), "yoksayılır", Fixtures.Now).IsSuccess.ShouldBeTrue();

        deal.ClosedAt.ShouldBe(Fixtures.Now);
        deal.LostReason.ShouldBeNull();
        deal.DomainEvents.OfType<DealStageChanged>().Single().ToKind.ShouldBe(StageKind.Won);
    }

    [Fact]
    public void Losing_RequiresAReason()
    {
        var pipeline = Fixtures.NewPipeline();
        var deal = Fixtures.NewDeal(pipeline);
        var lost = pipeline.Stage(StageKind.Lost);
        var before = deal.StageId;

        deal.MoveToStage(lost, null, Fixtures.Now).Error.Code.ShouldBe(SalesErrors.LostReasonRequired);
        deal.MoveToStage(lost, "   ", Fixtures.Now).Error.Code.ShouldBe(SalesErrors.LostReasonRequired);
        deal.StageId.ShouldBe(before);
        deal.ClosedAt.ShouldBeNull();
        deal.DomainEvents.ShouldBeEmpty();

        deal.MoveToStage(lost, "  Bütçe yok ", Fixtures.Now).IsSuccess.ShouldBeTrue();
        deal.LostReason.ShouldBe("Bütçe yok");
        deal.ClosedAt.ShouldBe(Fixtures.Now);
    }

    [Fact]
    public void Reopening_ClearsClosedAtAndReason_AndSwitchingClosedStagesRestampsClosedAt()
    {
        var pipeline = Fixtures.NewPipeline();
        var deal = Fixtures.NewDeal(pipeline);
        deal.MoveToStage(pipeline.Stage(StageKind.Won), null, Fixtures.Now).IsSuccess.ShouldBeTrue();

        var later = Fixtures.Now.AddDays(1);
        deal.MoveToStage(pipeline.Stage(StageKind.Lost), "Vazgeçti", later).IsSuccess.ShouldBeTrue();
        deal.ClosedAt.ShouldBe(later);

        deal.MoveToStage(pipeline.Stages[1], null, later.AddDays(1)).IsSuccess.ShouldBeTrue();
        deal.ClosedAt.ShouldBeNull();
        deal.LostReason.ShouldBeNull();
    }

    [Fact]
    public void Stage_FromAnotherPipeline_IsRejected()
    {
        var deal = Fixtures.NewDeal(Fixtures.NewPipeline());
        var foreign = Fixtures.NewPipeline().Stages[1];

        deal.MoveToStage(foreign, null, Fixtures.Now).Error.Code.ShouldBe(SalesErrors.StageNotFound);
    }

    [Fact]
    public void Create_WithExplicitLostStage_NeedsAReason_AndStageMustBelongToPipeline()
    {
        var pipeline = Fixtures.NewPipeline();
        var lost = pipeline.Stage(StageKind.Lost);

        Deal.Create(Fixtures.Tenant, "D", Guid.NewGuid(), Fixtures.Owner, pipeline, lost, null, null, null, null, null, Fixtures.Now)
            .Error.Code.ShouldBe(SalesErrors.LostReasonRequired);
        Deal.Create(Fixtures.Tenant, "D", Guid.NewGuid(), Fixtures.Owner, pipeline, Fixtures.NewPipeline().Stages[0], null, null, null, null, null, Fixtures.Now)
            .Error.Code.ShouldBe(SalesErrors.StageNotFound);
        Deal.Create(Fixtures.Tenant, "D", Guid.NewGuid(), Fixtures.Owner, pipeline, lost, null, null, null, null, "Rakip", Fixtures.Now)
            .Value.ClosedAt.ShouldBe(Fixtures.Now);
    }

    [Fact]
    public void Update_DoesNotTouchTheStage()
    {
        var pipeline = Fixtures.NewPipeline();
        var deal = Fixtures.NewDeal(pipeline, pipeline.Stages[2]);

        deal.Update("Yeni ad", deal.AccountId, Fixtures.Owner, null, 2500m, "usd", null, null);

        (deal.Name, deal.Amount, deal.Currency, deal.StageId).ShouldBe(("Yeni ad", (decimal?)2500m, "USD", pipeline.Stages[2].Id));
    }
}
