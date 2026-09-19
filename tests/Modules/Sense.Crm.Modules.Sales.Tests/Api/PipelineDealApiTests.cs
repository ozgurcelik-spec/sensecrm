using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Sales.Tests.Api.SalesApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Sales.Tests.Api;

/// <summary>Satış hunisi yönetimi, fırsatlar, aşama değişimi (kazanma/kaybetme) ve kanban panosu.</summary>
[Collection(ApiCollection.Name)]
public sealed class PipelineDealApiTests(CrmApiFactory factory)
{
    private static readonly string[] TurkishStages = ["Nitelendirme", "İhtiyaç Analizi", "Teklif", "Pazarlık", "Kazanıldı", "Kaybedildi"];

    [Fact]
    public async Task Pipelines_DefaultPipelineIsAvailableRightAfterSignUp_WithContractStages()
    {
        var admin = (await factory.NewOrgAsync("Pipeline Org")).Admin;

        var pipelines = await admin.GetJsonAsync($"{Base}/pipelines");
        var pipeline = pipelines.EnumerateArray().Single();
        pipeline.GetProperty("isDefault").GetBoolean().ShouldBeTrue();

        var stages = pipeline.GetProperty("stages").EnumerateArray().ToList();
        stages.Select(s => s.GetProperty("name").GetString()).ShouldBe(TurkishStages);
        stages.Select(s => s.GetProperty("order").GetInt32()).ShouldBe([0, 1, 2, 3, 4, 5]);
        stages.Select(s => s.GetProperty("probability").GetInt32()).ShouldBe([10, 20, 50, 75, 100, 0]);
        stages.Select(s => s.GetProperty("kind").GetString()).ShouldBe(["open", "open", "open", "open", "won", "lost"]);

        var single = await admin.GetJsonAsync($"{Base}/pipelines/{pipeline.Id()}");
        single.Id().ShouldBe(pipeline.Id());
        await (await admin.GetAsync($"{Base}/pipelines/{Guid.NewGuid()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Pipelines_Create_Update_DefaultRules()
    {
        var admin = (await factory.NewOrgAsync("Pipeline Manage")).Admin;
        var first = await admin.DefaultPipelineAsync();

        var created = await admin.PostJsonAsync($"{Base}/pipelines", new { name = "Bayi Satışı" });
        created.GetProperty("isDefault").GetBoolean().ShouldBeFalse();
        created.GetProperty("stages").GetArrayLength().ShouldBe(6, "varsayılan aşamalarla açılır");
        await (await admin.PostAsJsonAsync($"{Base}/pipelines", new { name = " " }, Ct)).ShouldBeValidationErrorAsync("name");

        // Varsayılan huni "varsayılan değil" yapılamaz; başkasını varsayılan yapmak eskisini düşürür.
        await (await admin.PutAsJsonAsync($"{Base}/pipelines/{first.Id()}", new { name = first.GetProperty("name").GetString(), isDefault = false }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "pipeline.default_required");
        await admin.PutJsonAsync($"{Base}/pipelines/{created.Id()}", new { name = "Bayi Satışı 2", isDefault = true });

        var pipelines = (await admin.GetJsonAsync($"{Base}/pipelines")).EnumerateArray().ToList();
        pipelines.Count(p => p.GetProperty("isDefault").GetBoolean()).ShouldBe(1);
        pipelines.Single(p => p.GetProperty("isDefault").GetBoolean()).GetProperty("name").GetString().ShouldBe("Bayi Satışı 2");
        (await admin.DefaultPipelineAsync()).Id().ShouldBe(created.Id());

        // Yeni fırsat artık yeni varsayılan hunide başlar.
        var account = await admin.CreateAccountAsync("Bayi");
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Bayi fırsatı", accountId = account.Id() });
        deal.GetProperty("pipelineName").GetString().ShouldBe("Bayi Satışı 2");
    }

    [Fact]
    public async Task Pipelines_ReplaceStages_ValidatesKindsOrdersAndStagesInUse()
    {
        var admin = (await factory.NewOrgAsync("Stages Org")).Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var stages = pipeline.GetProperty("stages").EnumerateArray().ToList();
        object Existing(int index, string? name = null, int? probability = null) => new
        {
            id = stages[index].Id(),
            name = name ?? stages[index].GetProperty("name").GetString(),
            probability = probability ?? stages[index].GetProperty("probability").GetInt32(),
            kind = stages[index].GetProperty("kind").GetString(),
        };

        // Tam bir won ve bir lost şart.
        var noLost = new object[] { Existing(0), Existing(4) };
        var twoWon = new object[] { Existing(0), Existing(4), new { name = "Ikinci kazanç", probability = 100, kind = "won" }, Existing(5) };
        await (await admin.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages = noLost }, Ct)).ShouldBeValidationErrorAsync("stages");
        await (await admin.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages = twoWon }, Ct)).ShouldBeValidationErrorAsync("stages");
        await (await admin.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages = new object[] { Existing(0), Existing(4), Existing(5), new { name = "", probability = 5, kind = "open" } } }, Ct))
            .ShouldBeValidationErrorAsync("stages[3].name");
        await (await admin.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages = new object[] { Existing(0), Existing(4), Existing(5), new { name = "Kötü", probability = 101, kind = "open" } } }, Ct))
            .ShouldBeValidationErrorAsync("stages[3].probability");

        // Kullanımdaki aşama silinemez.
        var account = await admin.CreateAccountAsync("Aşama Kullanan");
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Nitelendirmede", accountId = account.Id() });
        var withoutFirst = new object[] { Existing(1), Existing(2), Existing(3), Existing(4), Existing(5) };
        await (await admin.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages = withoutFirst }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "pipeline.stage_in_use");
        (await admin.DefaultPipelineAsync()).GetProperty("stages").GetArrayLength().ShouldBe(6, "başarısız değişiklik hiçbir şeyi değiştirmez");

        // Yeniden sırala + yeniden adlandır + yeni aşama ekle + kullanılmayan aşamayı sil.
        var newStages = new object[]
        {
            Existing(0, "Ön görüşme", 5),
            new { name = "Demo", probability = 35, kind = "open" },
            Existing(2, "Teklif verildi"),
            Existing(4),
            Existing(5),
        };
        await admin.PutJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages = newStages });

        var after = (await admin.DefaultPipelineAsync()).GetProperty("stages").EnumerateArray().ToList();
        after.Select(s => s.GetProperty("name").GetString()).ShouldBe(["Ön görüşme", "Demo", "Teklif verildi", "Kazanıldı", "Kaybedildi"]);
        after.Select(s => s.GetProperty("order").GetInt32()).ShouldBe([0, 1, 2, 3, 4]);
        after[0].Id().ShouldBe(stages[0].Id(), "kimliği olan aşama güncellenir, yeniden oluşturulmaz");
        after[1].Id().ShouldNotBe(stages[1].Id());

        // Fırsat aşama kimliğini korudu ve yeni adı görür.
        var reloaded = await admin.GetJsonAsync($"{Base}/deals/{deal.Id()}");
        (reloaded.GetProperty("stageName").GetString(), reloaded.GetProperty("probability").GetInt32()).ShouldBe(("Ön görüşme", 5));

        // Başka huniye ait aşama kimliğiyle güncelleme bulunamaz.
        var another = await admin.PostJsonAsync($"{Base}/pipelines", new { name = "Diğer" });
        var foreign = another.GetProperty("stages").EnumerateArray().First();
        await (await admin.PutAsJsonAsync($"{Base}/pipelines/{pipeline.Id()}/stages", new { stages = new object[] { Existing(0), new { id = foreign.Id(), name = "x", probability = 1, kind = "open" }, Existing(4), Existing(5) } }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "pipeline.stage_not_found");
    }

    [Fact]
    public async Task Deal_Crud_DefaultsToFirstOpenStageAndDefaultPipeline()
    {
        var org = await factory.NewOrgAsync("Deal Crud");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { firstName = "Can", lastName = "Öz", accountId = account.Id() });

        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Yıllık lisans", accountId = account.Id(), contactId = contact.Id(), amount = 4200.75m, closingDate = "2026-11-30" });
        var id = deal.Id();
        (deal.GetProperty("stageName").GetString(), deal.GetProperty("stageKind").GetString(), deal.GetProperty("probability").GetInt32()).ShouldBe(("Nitelendirme", "open", 10));
        (deal.GetProperty("accountName").GetString(), deal.GetProperty("contactName").GetString(), deal.GetProperty("pipelineName").GetString()).ShouldBe(("Firma", "Can Öz", "Satış Hunisi"));
        (deal.GetProperty("currency").GetString(), deal.GetProperty("amount").GetDecimal(), deal.GetProperty("closingDate").GetString()).ShouldBe(("TRY", 4200.75m, "2026-11-30"));
        deal.GetProperty("ownerName").GetString().ShouldBe(org.AdminName);
        deal.TryGetProperty("closedAt", out _).ShouldBeFalse();
        deal.TryGetProperty("lostReason", out _).ShouldBeFalse();

        var stageBefore = deal.GetProperty("stageId").GetGuid();
        await admin.PutJsonAsync($"{Base}/deals/{id}", new { name = "Yıllık lisans (güncel)", accountId = account.Id(), amount = 5000, currency = "usd" });
        var updated = await admin.GetJsonAsync($"{Base}/deals/{id}");
        (updated.GetProperty("name").GetString(), updated.GetProperty("amount").GetDecimal(), updated.GetProperty("currency").GetString()).ShouldBe(("Yıllık lisans (güncel)", 5000m, "USD"));
        updated.GetProperty("stageId").GetGuid().ShouldBe(stageBefore, "aşama PUT ile değişmez");
        updated.TryGetProperty("contactId", out _).ShouldBeFalse("PUT tam değiştirmedir");

        (await admin.GetJsonAsync($"{Base}/accounts/{account.Id()}/deals")).EnumerateArray().Single().Id().ShouldBe(id);

        await (await admin.PostAsJsonAsync($"{Base}/deals", new { name = "", accountId = account.Id() }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await admin.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = account.Id(), amount = -1 }, Ct)).ShouldBeValidationErrorAsync("amount");
        await (await admin.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = account.Id(), currency = "XYZ" }, Ct)).ShouldBeValidationErrorAsync("currency");
        await (await admin.PostAsJsonAsync($"{Base}/deals", new { name = "X" }, Ct)).ShouldBeValidationErrorAsync("accountId");
        await (await admin.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = account.Id(), stageId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "pipeline.stage_not_found");

        // Fırsatı olan firma silinemez; fırsat silinince silinebilir.
        await (await admin.DeleteAsync($"{Base}/accounts/{account.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "account.has_dependents");
        await admin.DeleteJsonAsync($"{Base}/deals/{id}");
        await (await admin.GetAsync($"{Base}/deals/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Deal_MoveStage_WinLoseAndReopen()
    {
        var admin = (await factory.NewOrgAsync("Deal Stages")).Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var account = await admin.CreateAccountAsync("Firma");
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Hareketli", accountId = account.Id(), amount = 100 });
        var id = deal.Id();

        await admin.PostJsonAsync($"{Base}/deals/{id}/stage", new { stageId = pipeline.StageId("Teklif") }, HttpStatusCode.NoContent);
        var proposal = await admin.GetJsonAsync($"{Base}/deals/{id}");
        (proposal.GetProperty("stageName").GetString(), proposal.GetProperty("probability").GetInt32()).ShouldBe(("Teklif", 50));
        proposal.TryGetProperty("closedAt", out _).ShouldBeFalse();

        await admin.PostJsonAsync($"{Base}/deals/{id}/stage", new { stageId = pipeline.StageId("Kazanıldı") }, HttpStatusCode.NoContent);
        var won = await admin.GetJsonAsync($"{Base}/deals/{id}");
        (won.GetProperty("stageKind").GetString(), won.GetProperty("probability").GetInt32()).ShouldBe(("won", 100));
        won.GetProperty("closedAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        // Kaybedildi aşaması neden ister.
        await (await admin.PostAsJsonAsync($"{Base}/deals/{id}/stage", new { stageId = pipeline.StageId("Kaybedildi") }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "deal.lost_reason_required");
        await (await admin.PostAsJsonAsync($"{Base}/deals/{id}/stage", new { stageId = pipeline.StageId("Kaybedildi"), lostReason = "  " }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "deal.lost_reason_required");
        (await admin.GetJsonAsync($"{Base}/deals/{id}")).GetProperty("stageKind").GetString().ShouldBe("won", "başarısız geçiş hiçbir şeyi değiştirmez");

        await admin.PostJsonAsync($"{Base}/deals/{id}/stage", new { stageId = pipeline.StageId("Kaybedildi"), lostReason = "Bütçe yok" }, HttpStatusCode.NoContent);
        var lost = await admin.GetJsonAsync($"{Base}/deals/{id}");
        (lost.GetProperty("stageKind").GetString(), lost.GetProperty("probability").GetInt32(), lost.GetProperty("lostReason").GetString()).ShouldBe(("lost", 0, "Bütçe yok"));
        lost.GetProperty("closedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(won.GetProperty("closedAt").GetDateTimeOffset());

        // Yeniden açma kapanış bilgisini temizler.
        await admin.PostJsonAsync($"{Base}/deals/{id}/stage", new { stageId = pipeline.StageId("Pazarlık") }, HttpStatusCode.NoContent);
        var reopened = await admin.GetJsonAsync($"{Base}/deals/{id}");
        reopened.TryGetProperty("closedAt", out _).ShouldBeFalse();
        reopened.TryGetProperty("lostReason", out _).ShouldBeFalse();

        // Başka huninin aşaması / bilinmeyen aşama.
        var other = await admin.PostJsonAsync($"{Base}/pipelines", new { name = "Diğer Huni" });
        await (await admin.PostAsJsonAsync($"{Base}/deals/{id}/stage", new { stageId = other.GetProperty("stages").EnumerateArray().First().Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "pipeline.stage_not_found");
        await (await admin.PostAsJsonAsync($"{Base}/deals/{id}/stage", new { stageId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "pipeline.stage_not_found");
        await (await admin.PostAsJsonAsync($"{Base}/deals/{Guid.NewGuid()}/stage", new { stageId = pipeline.StageId("Teklif") }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.PostAsJsonAsync($"{Base}/deals/{id}/stage", new { stageId = Guid.Empty }, Ct)).ShouldBeValidationErrorAsync("stageId");

        // DealStageChanged domain event'leri aynı transaction'da outbox'a yazıldı (won, lost, reopen dahil 4 geçiş).
        var events = await factory.OutboxMessagesAsync<SalesDbContext>("Sales.DealStageChanged", id.ToString());
        events.Count.ShouldBe(4);
        await factory.DrainOutboxAsync<SalesDbContext>();
        (await factory.OutboxMessagesAsync<SalesDbContext>("Sales.DealStageChanged", id.ToString())).ShouldAllBe(m => m.ProcessedAt != null);
    }

    [Fact]
    public async Task Deals_List_FiltersAndSorts()
    {
        var org = await factory.NewOrgAsync("Deal Lists");
        var admin = org.Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var acme = await admin.CreateAccountAsync("Acme Corp");
        var beta = await admin.CreateAccountAsync("Beta Ltd");
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.deals.read");
        var d1 = await admin.PostJsonAsync($"{Base}/deals", new { name = "Alfa", accountId = acme.Id(), amount = 300 });
        var d2 = await admin.PostJsonAsync($"{Base}/deals", new { name = "Bravo", accountId = beta.Id(), amount = 100, ownerUserId = memberId });
        var d3 = await admin.PostJsonAsync($"{Base}/deals", new { name = "Charlie", accountId = acme.Id(), amount = 200 });
        await admin.PostJsonAsync($"{Base}/deals/{d3.Id()}/stage", new { stageId = pipeline.StageId("Kazanıldı") }, HttpStatusCode.NoContent);

        (await admin.GetJsonAsync($"{Base}/deals")).GetProperty("totalCount").GetInt32().ShouldBe(3);
        Names(await admin.GetJsonAsync($"{Base}/deals?stageKind=won")).ShouldBe(["Charlie"]);
        Names(await admin.GetJsonAsync($"{Base}/deals?stageKind=open&sort=name")).ShouldBe(["Alfa", "Bravo"]);
        Names(await admin.GetJsonAsync($"{Base}/deals?stageId={pipeline.StageId("Kazanıldı")}")).ShouldBe(["Charlie"]);
        Names(await admin.GetJsonAsync($"{Base}/deals?accountId={acme.Id()}&sort=-amount")).ShouldBe(["Alfa", "Charlie"]);
        Names(await admin.GetJsonAsync($"{Base}/deals?ownerUserId={memberId}")).ShouldBe(["Bravo"]);
        Names(await admin.GetJsonAsync($"{Base}/deals?pipelineId={pipeline.Id()}&sort=amount")).ShouldBe(["Bravo", "Charlie", "Alfa"]);
        Names(await admin.GetJsonAsync($"{Base}/deals?pipelineId={Guid.NewGuid()}")).ShouldBeEmpty();
        Names(await admin.GetJsonAsync($"{Base}/deals?q=beta")).ShouldBe(["Bravo"], "firma adıyla arama");
        Names(await admin.GetJsonAsync($"{Base}/deals?q=char")).ShouldBe(["Charlie"]);
        Names(await admin.GetJsonAsync($"{Base}/deals?pageSize=2&page=2&sort=name")).ShouldBe(["Charlie"]);
        (await admin.GetAsync($"{Base}/deals?stageKind=bogus", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        d1.Id().ShouldNotBe(d2.Id());
    }

    [Fact]
    public async Task Board_ReturnsStageTotalsCountsAndCards()
    {
        var org = await factory.NewOrgAsync("Board Org");
        var admin = org.Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var (_, memberId) = await factory.AddMemberAsync(org, "Kart Sahibi", "crm.deals.read");
        var account = await admin.CreateAccountAsync("Pano Firması");

        await admin.PostJsonAsync($"{Base}/deals", new { name = "Q1", accountId = account.Id(), amount = 1000, closingDate = "2026-10-01" });
        await admin.PostJsonAsync($"{Base}/deals", new { name = "Q2", accountId = account.Id(), amount = 2500.5m, ownerUserId = memberId });
        var moved = await admin.PostJsonAsync($"{Base}/deals", new { name = "Q3", accountId = account.Id(), amount = 400 });
        await admin.PostJsonAsync($"{Base}/deals", new { name = "Q4 tutarsız", accountId = account.Id() });
        await admin.PostJsonAsync($"{Base}/deals/{moved.Id()}/stage", new { stageId = pipeline.StageId("Kazanıldı") }, HttpStatusCode.NoContent);

        var board = await admin.GetJsonAsync($"{Base}/deals/board");
        board.GetProperty("pipelineId").GetGuid().ShouldBe(pipeline.Id());
        var stages = board.GetProperty("stages").EnumerateArray().ToList();
        stages.Select(s => s.GetProperty("name").GetString()).ShouldBe(TurkishStages);

        var qualification = stages[0];
        (qualification.GetProperty("count").GetInt32(), qualification.GetProperty("totalAmount").GetDecimal(), qualification.GetProperty("kind").GetString(), qualification.GetProperty("probability").GetInt32())
            .ShouldBe((3, 3500.5m, "open", 10));
        var cards = qualification.GetProperty("deals").EnumerateArray().ToList();
        cards.Count.ShouldBe(3);
        var q1 = cards.Single(c => c.GetProperty("name").GetString() == "Q1");
        (q1.GetProperty("accountName").GetString(), q1.GetProperty("amount").GetDecimal(), q1.GetProperty("currency").GetString(), q1.GetProperty("closingDate").GetString(), q1.GetProperty("ownerName").GetString())
            .ShouldBe(("Pano Firması", 1000m, "TRY", "2026-10-01", org.AdminName));
        cards.Single(c => c.GetProperty("name").GetString() == "Q4 tutarsız").TryGetProperty("amount", out _).ShouldBeFalse();

        var won = stages.Single(s => s.GetProperty("kind").GetString() == "won");
        (won.GetProperty("count").GetInt32(), won.GetProperty("totalAmount").GetDecimal()).ShouldBe((1, 400m));
        var lost = stages.Single(s => s.GetProperty("kind").GetString() == "lost");
        (lost.GetProperty("count").GetInt32(), lost.GetProperty("totalAmount").GetDecimal(), lost.GetProperty("deals").GetArrayLength()).ShouldBe((0, 0m, 0));
        stages.Single(s => s.GetProperty("name").GetString() == "Teklif").GetProperty("count").GetInt32().ShouldBe(0);

        // Sahibe göre süzme ve açık huni kimliği.
        var mine = await admin.GetJsonAsync($"{Base}/deals/board?pipelineId={pipeline.Id()}&ownerUserId={memberId}");
        var mineStages = mine.GetProperty("stages").EnumerateArray().ToList();
        (mineStages[0].GetProperty("count").GetInt32(), mineStages[0].GetProperty("totalAmount").GetDecimal()).ShouldBe((1, 2500.5m));
        mineStages.Sum(s => s.GetProperty("count").GetInt32()).ShouldBe(1);

        await (await admin.GetAsync($"{Base}/deals/board?pipelineId={Guid.NewGuid()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Board_CapsCardsAtHundred_ButCountsEverything()
    {
        var admin = (await factory.NewOrgAsync("Board Cap")).Admin;
        var account = await admin.CreateAccountAsync("Kalabalık");
        for (var i = 0; i < 101; i++)
        {
            await admin.PostJsonAsync($"{Base}/deals", new { name = $"Fırsat {i:000}", accountId = account.Id(), amount = 10 });
        }

        var first = (await admin.GetJsonAsync($"{Base}/deals/board")).GetProperty("stages").EnumerateArray().First();

        (first.GetProperty("count").GetInt32(), first.GetProperty("totalAmount").GetDecimal(), first.GetProperty("deals").GetArrayLength()).ShouldBe((101, 1010m, 100));
    }

    private static List<string?> Names(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
}
