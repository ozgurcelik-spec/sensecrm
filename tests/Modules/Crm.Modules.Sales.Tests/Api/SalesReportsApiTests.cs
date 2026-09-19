using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Shared.Kernel.Time;
using Crm.Tests.Shared.Fixtures;
using Npgsql;
using Shouldly;
using Xunit;
using static Crm.Modules.Sales.Tests.Api.SalesApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Sales.Tests.Api;

/// <summary>
/// Satış raporları (huni, kazanılan/kaybedilen, potansiyel kaynakları, satış temsilcisi): bilinen veriyle toplamlar, boş dönem
/// doldurma, aralık sınırları (kiracı saat dilimi Europe/Istanbul, UTC+3), izin ve kiracı izolasyonu.
/// Kapanış/oluşturulma anları SQL ile sabit tarihlere çekilir (kayıt anı testten bağımsız kalsın diye).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SalesReportsApiTests(CrmApiFactory factory)
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static async Task ExecuteAsync(CrmApiFactory f, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(f.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
    }

    private static Task SetClosedAtAsync(CrmApiFactory f, Guid dealId, DateTime closedAt) =>
        ExecuteAsync(f, "UPDATE sales.deals SET closed_at = @at WHERE id = @id", ("at", closedAt), ("id", dealId));

    private static Task SetLeadCreatedAtAsync(CrmApiFactory f, Guid leadId, DateTime createdAt) =>
        ExecuteAsync(f, "UPDATE sales.leads SET created_at = @at WHERE id = @id", ("at", createdAt), ("id", leadId));

    private static async Task<Guid> CreateDealAsync(HttpClient client, Guid accountId, string name, decimal? amount, Guid? owner = null, Guid? stageId = null, string? lostReason = null)
    {
        var deal = await client.PostJsonAsync($"{Base}/deals", new { name, accountId, amount, ownerUserId = owner });
        if (stageId is { } stage)
        {
            await client.PostJsonAsync($"{Base}/deals/{deal.Id()}/stage", new { stageId = stage, lostReason }, HttpStatusCode.NoContent);
        }

        return deal.Id();
    }

    private static JsonElement Period(JsonElement report, string period) =>
        report.EnumerateArray().Single(p => p.GetProperty("period").GetString() == period);

    private static (int WonCount, decimal WonAmount, int LostCount, decimal LostAmount) Totals(JsonElement p) =>
        (p.GetProperty("wonCount").GetInt32(), p.GetProperty("wonAmount").GetDecimal(), p.GetProperty("lostCount").GetInt32(), p.GetProperty("lostAmount").GetDecimal());

    [Fact]
    public async Task Funnel_CountsAndSumsCurrentDealsPerStage_IncludingEmptyStages()
    {
        var org = await factory.NewOrgAsync("Funnel Org");
        var admin = org.Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var account = await admin.CreateAccountAsync("Firma");

        await CreateDealAsync(admin, account.Id(), "A", 100m);
        await CreateDealAsync(admin, account.Id(), "B", 200.5m);
        await CreateDealAsync(admin, account.Id(), "C", 1000m, stageId: pipeline.StageId("Teklif"));
        await CreateDealAsync(admin, account.Id(), "D", 700m, stageId: pipeline.StageId("Kazanıldı"));
        await CreateDealAsync(admin, account.Id(), "E", null, stageId: pipeline.StageId("Kaybedildi"), lostReason: "Bütçe");
        var deleted = await CreateDealAsync(admin, account.Id(), "F", 9999m);
        await admin.DeleteJsonAsync($"{Base}/deals/{deleted}");

        var funnel = await admin.GetJsonAsync($"{Base}/reports/sales/funnel");

        funnel.GetProperty("pipelineId").GetGuid().ShouldBe(pipeline.Id());
        var stages = funnel.GetProperty("stages").EnumerateArray().ToList();
        stages.Select(s => s.GetProperty("name").GetString()).ShouldBe(["Nitelendirme", "İhtiyaç Analizi", "Teklif", "Pazarlık", "Kazanıldı", "Kaybedildi"]);
        stages.Select(s => s.GetProperty("order").GetInt32()).ShouldBe([0, 1, 2, 3, 4, 5]);
        stages.Select(s => s.GetProperty("kind").GetString()).ShouldBe(["open", "open", "open", "open", "won", "lost"]);
        stages.Select(s => s.GetProperty("probability").GetInt32()).ShouldBe([10, 20, 50, 75, 100, 0]);
        stages.Select(s => (s.GetProperty("count").GetInt32(), s.GetProperty("totalAmount").GetDecimal()))
            .ShouldBe([(2, 300.5m), (0, 0m), (1, 1000m), (0, 0m), (1, 700m), (1, 0m)]);

        // Açıkça verilen huni aynı sonucu verir; bilinmeyen huni 404.
        (await admin.GetJsonAsync($"{Base}/reports/sales/funnel?pipelineId={pipeline.Id()}")).GetProperty("stages").GetArrayLength().ShouldBe(6);
        await (await admin.GetAsync($"{Base}/reports/sales/funnel?pipelineId={Guid.NewGuid()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task WonLost_GroupsByMonthInTenantTimeZone_FillsEmptyPeriods_AndRespectsRangeEdges()
    {
        var org = await factory.NewOrgAsync("WonLost Month");
        var admin = org.Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var (won, lost) = (pipeline.StageId("Kazanıldı"), pipeline.StageId("Kaybedildi"));
        var account = await admin.CreateAccountAsync("Firma");

        // Kiracı saat dilimi Europe/Istanbul (UTC+3): 28 Şubat 22:00Z = 1 Mart 01:00 yerel; 31 Mart 21:30Z = 1 Nisan 00:30 yerel.
        var w0 = await CreateDealAsync(admin, account.Id(), "W0", 100m, stageId: won);
        var w1 = await CreateDealAsync(admin, account.Id(), "W1", 1000m, stageId: won);
        var w2 = await CreateDealAsync(admin, account.Id(), "W2", 500m, stageId: won);
        var w3 = await CreateDealAsync(admin, account.Id(), "W3", 250m, stageId: won);
        var l1 = await CreateDealAsync(admin, account.Id(), "L1", 300m, stageId: lost, lostReason: "Fiyat");
        var l2 = await CreateDealAsync(admin, account.Id(), "L2", null, stageId: lost, lostReason: "Vazgeçti");
        await CreateDealAsync(admin, account.Id(), "Open", 50m); // açık fırsat kapanış raporunda yok
        await SetClosedAtAsync(factory, w0, Utc(2026, 2, 28, 22));
        await SetClosedAtAsync(factory, w1, Utc(2026, 3, 15, 10));
        await SetClosedAtAsync(factory, w2, Utc(2026, 3, 31, 21, 30));
        await SetClosedAtAsync(factory, w3, Utc(2026, 5, 10, 12));
        await SetClosedAtAsync(factory, l1, Utc(2026, 3, 20, 12));
        await SetClosedAtAsync(factory, l2, Utc(2026, 5, 11, 8));

        var report = await admin.GetJsonAsync($"{Base}/reports/sales/won-lost?from=2026-03-01&to=2026-06-30&groupBy=month");

        report.EnumerateArray().Select(p => p.GetProperty("period").GetString()).ShouldBe(["2026-03", "2026-04", "2026-05", "2026-06"]);
        Totals(Period(report, "2026-03")).ShouldBe((2, 1100m, 1, 300m)); // W0 (yerelde 1 Mart) + W1
        Totals(Period(report, "2026-04")).ShouldBe((1, 500m, 0, 0m)); // W2 yerelde 1 Nisan
        Totals(Period(report, "2026-05")).ShouldBe((1, 250m, 1, 0m));
        Totals(Period(report, "2026-06")).ShouldBe((0, 0m, 0, 0m)); // boş dönem 0 ile dolu

        // groupBy verilmezse ay; aralığın iki ucu dahil: 31 Mart'a kadar → W2 (1 Nisan yerel) dışarıda, 1 Mart 01:00 yerel (W0) içeride.
        var march = await admin.GetJsonAsync($"{Base}/reports/sales/won-lost?from=2026-03-01&to=2026-03-31");
        march.GetArrayLength().ShouldBe(1);
        Totals(march[0]).ShouldBe((2, 1100m, 1, 300m));

        // Tek gün: 1 Mart yerel günü yalnız W0'ı içerir; 28 Şubat yerel günü hiçbirini.
        Totals((await admin.GetJsonAsync($"{Base}/reports/sales/won-lost?from=2026-03-01&to=2026-03-01"))[0]).ShouldBe((1, 100m, 0, 0m));
        Totals((await admin.GetJsonAsync($"{Base}/reports/sales/won-lost?from=2026-02-28&to=2026-02-28"))[0]).ShouldBe((0, 0m, 0, 0m));
    }

    [Fact]
    public async Task WonLost_GroupsByIsoWeek_AndFillsEmptyWeeks()
    {
        var org = await factory.NewOrgAsync("WonLost Week");
        var admin = org.Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var account = await admin.CreateAccountAsync("Firma");

        var sunday = await CreateDealAsync(admin, account.Id(), "Pazar", 1000m, stageId: pipeline.StageId("Kazanıldı"));
        var friday = await CreateDealAsync(admin, account.Id(), "Cuma", 300m, stageId: pipeline.StageId("Kaybedildi"), lostReason: "x");
        await SetClosedAtAsync(factory, sunday, Utc(2026, 3, 15, 10)); // pazar, ISO hafta 11 (9-15 Mart)
        await SetClosedAtAsync(factory, friday, Utc(2026, 3, 20, 12)); // cuma, hafta 12

        var report = await admin.GetJsonAsync($"{Base}/reports/sales/won-lost?from=2026-03-09&to=2026-03-29&groupBy=week");

        report.EnumerateArray().Select(p => p.GetProperty("period").GetString()).ShouldBe(["2026-W11", "2026-W12", "2026-W13"]);
        Totals(Period(report, "2026-W11")).ShouldBe((1, 1000m, 0, 0m));
        Totals(Period(report, "2026-W12")).ShouldBe((0, 0m, 1, 300m));
        Totals(Period(report, "2026-W13")).ShouldBe((0, 0m, 0, 0m));
    }

    [Fact]
    public async Task WonLost_WithoutRange_DefaultsToLastTwelveMonths_IncludingThisMonth()
    {
        var org = await factory.NewOrgAsync("WonLost Default");
        var admin = org.Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var account = await admin.CreateAccountAsync("Firma");
        await CreateDealAsync(admin, account.Id(), "Şimdi kazanıldı", 800m, stageId: pipeline.StageId("Kazanıldı"));

        var report = await admin.GetJsonAsync($"{Base}/reports/sales/won-lost");

        var thisMonth = TenantCalendar.MonthLabel(TenantCalendar.For("Europe/Istanbul").Today(DateTimeOffset.UtcNow));
        report.GetArrayLength().ShouldBe(12);
        report.EnumerateArray().Last().GetProperty("period").GetString().ShouldBe(thisMonth);
        Totals(Period(report, thisMonth)).ShouldBe((1, 800m, 0, 0m));
    }

    [Fact]
    public async Task LeadsBySource_CountsByCreatedAt_WithConvertedCount_AndRangeEdges()
    {
        var org = await factory.NewOrgAsync("Lead Sources");
        var admin = org.Admin;

        var web1 = await admin.CreateLeadAsync("Web1", "A", new { source = "web" });
        var web2 = await admin.CreateLeadAsync("Web2", "A", new { source = "web" });
        var web3 = await admin.CreateLeadAsync("Web3", "A", new { source = "web" });
        var referral = await admin.CreateLeadAsync("Ref", "A", new { source = "referral" });
        var cold1 = await admin.CreateLeadAsync("Cold1", "A", new { source = "coldCall" });
        var cold2 = await admin.CreateLeadAsync("Cold2", "A", new { source = "coldCall" });
        var outside = await admin.CreateLeadAsync("Eski", "A", new { source = "campaign" });
        await admin.PostJsonAsync($"{Base}/leads/{web1.Id()}/convert", new { createDeal = false }, HttpStatusCode.OK);

        // Yerel saat: Istanbul. 1 Mart 00:30 yerel (28 Şubat 21:30Z) aralığa girer; 31 Mart 23:30 yerel (31 Mart 20:30Z) girer;
        // 1 Nisan 00:30 yerel (31 Mart 21:30Z) girmez.
        await SetLeadCreatedAtAsync(factory, web1.Id(), Utc(2026, 2, 28, 21, 30));
        await SetLeadCreatedAtAsync(factory, web2.Id(), Utc(2026, 3, 10, 9));
        await SetLeadCreatedAtAsync(factory, web3.Id(), Utc(2026, 3, 31, 20, 30));
        await SetLeadCreatedAtAsync(factory, referral.Id(), Utc(2026, 3, 12, 9));
        await SetLeadCreatedAtAsync(factory, cold1.Id(), Utc(2026, 3, 13, 9));
        await SetLeadCreatedAtAsync(factory, cold2.Id(), Utc(2026, 3, 31, 21, 30));
        await SetLeadCreatedAtAsync(factory, outside.Id(), Utc(2025, 1, 1));

        var report = await admin.GetJsonAsync($"{Base}/reports/sales/leads-by-source?from=2026-03-01&to=2026-03-31");

        report.EnumerateArray().Select(r => (r.GetProperty("source").GetString(), r.GetProperty("count").GetInt32(), r.GetProperty("convertedCount").GetInt32()))
            .ShouldBe([("web", 3, 1), ("referral", 1, 0), ("coldCall", 1, 0)]);

        // Aralık verilmezse son 12 ay: bugün oluşturulan lead'ler girer, 2025-01-01'deki girmez (bugünün tarihine göre 12 ay öncesi).
        var fresh = await factory.NewOrgAsync("Lead Sources Default");
        await fresh.Admin.CreateLeadAsync("Yeni", "B", new { source = "campaign" });
        var defaults = await fresh.Admin.GetJsonAsync($"{Base}/reports/sales/leads-by-source");
        defaults.EnumerateArray().Select(r => (r.GetProperty("source").GetString(), r.GetProperty("count").GetInt32())).ShouldBe([("campaign", 1)]);
    }

    [Fact]
    public async Task ByOwner_SumsOpenWonAndLeadsPerOwner_WithNames()
    {
        var org = await factory.NewOrgAsync("By Owner");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Satışçı Sema", "crm.deals.read");
        var pipeline = await admin.DefaultPipelineAsync();
        var account = await admin.CreateAccountAsync("Firma");
        var adminId = org.AdminUserId;

        var adminWon = await CreateDealAsync(admin, account.Id(), "Admin kazandı", 1000m, adminId, pipeline.StageId("Kazanıldı"));
        var adminOutsideWon = await CreateDealAsync(admin, account.Id(), "Admin eski kazanç", 5000m, adminId, pipeline.StageId("Kazanıldı"));
        await CreateDealAsync(admin, account.Id(), "Admin açık", 100m, adminId);
        await CreateDealAsync(admin, account.Id(), "Admin kaybetti", 900m, adminId, pipeline.StageId("Kaybedildi"), "Fiyat");
        var memberWon = await CreateDealAsync(admin, account.Id(), "Üye kazandı", 500m, memberId, pipeline.StageId("Kazanıldı"));
        await CreateDealAsync(admin, account.Id(), "Üye açık 1", 200m, memberId, pipeline.StageId("Teklif"));
        await CreateDealAsync(admin, account.Id(), "Üye açık 2", null, memberId);
        await SetClosedAtAsync(factory, adminWon, Utc(2026, 3, 10, 9));
        await SetClosedAtAsync(factory, adminOutsideWon, Utc(2025, 12, 31, 9));
        await SetClosedAtAsync(factory, memberWon, Utc(2026, 4, 2, 9));

        var leadA1 = await admin.CreateLeadAsync("A1", "X", new { ownerUserId = adminId });
        var leadA2 = await admin.CreateLeadAsync("A2", "X", new { ownerUserId = adminId });
        var leadM1 = await admin.CreateLeadAsync("M1", "X", new { ownerUserId = memberId });
        var leadOld = await admin.CreateLeadAsync("Eski", "X", new { ownerUserId = memberId });
        await SetLeadCreatedAtAsync(factory, leadA1.Id(), Utc(2026, 3, 5, 9));
        await SetLeadCreatedAtAsync(factory, leadA2.Id(), Utc(2026, 5, 5, 9));
        await SetLeadCreatedAtAsync(factory, leadM1.Id(), Utc(2026, 6, 30, 9));
        await SetLeadCreatedAtAsync(factory, leadOld.Id(), Utc(2025, 6, 1, 9));

        var report = await admin.GetJsonAsync($"{Base}/reports/sales/by-owner?from=2026-01-01&to=2026-06-30");

        var rows = report.EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        // Kazanılan tutara göre azalan: admin 1000, üye 500.
        rows[0].GetProperty("ownerUserId").GetGuid().ShouldBe(adminId);
        rows[0].GetProperty("ownerName").GetString().ShouldBe(org.AdminName);
        (rows[0].GetProperty("openDealCount").GetInt32(), rows[0].GetProperty("openDealAmount").GetDecimal(), rows[0].GetProperty("wonCount").GetInt32(), rows[0].GetProperty("wonAmount").GetDecimal(), rows[0].GetProperty("leadCount").GetInt32())
            .ShouldBe((1, 100m, 1, 1000m, 2));
        rows[1].GetProperty("ownerUserId").GetGuid().ShouldBe(memberId);
        rows[1].GetProperty("ownerName").GetString().ShouldBe("Satışçı Sema");
        (rows[1].GetProperty("openDealCount").GetInt32(), rows[1].GetProperty("openDealAmount").GetDecimal(), rows[1].GetProperty("wonCount").GetInt32(), rows[1].GetProperty("wonAmount").GetDecimal(), rows[1].GetProperty("leadCount").GetInt32())
            .ShouldBe((2, 200m, 1, 500m, 1));
    }

    [Fact]
    public async Task Reports_RequireReportsReadPermission_AndValidateTheRange()
    {
        var org = await factory.NewOrgAsync("Report Permissions");
        var (noReports, _) = await factory.AddMemberAsync(org, "Raporsuz", "crm.deals.read", "crm.leads.read", "crm.activities.read");
        var (reports, _) = await factory.AddMemberAsync(org, "Raporlu", "crm.reports.read");
        _ = await org.Admin.DefaultPipelineAsync();

        foreach (var path in new[] { "funnel", "won-lost", "leads-by-source", "by-owner" })
        {
            await (await noReports.GetAsync($"{Base}/reports/sales/{path}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            (await reports.GetAsync($"{Base}/reports/sales/{path}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Ters aralık ve 10 yılı aşan aralık: validation + errors.to.
        await (await org.Admin.GetAsync($"{Base}/reports/sales/won-lost?from=2026-06-30&to=2026-06-01", Ct)).ShouldBeValidationErrorAsync("to");
        await (await org.Admin.GetAsync($"{Base}/reports/sales/by-owner?from=2000-01-01&to=2026-06-01", Ct)).ShouldBeValidationErrorAsync("to");
        // Yalnız from verilip bugünden sonraya düşerse çözülmüş aralık ters → validation.
        await (await org.Admin.GetAsync($"{Base}/reports/sales/leads-by-source?from=2999-01-01", Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        // Geçersiz tarih biçimi 400.
        (await org.Admin.GetAsync($"{Base}/reports/sales/won-lost?from=bugun", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CrossTenantIsolation_ReportsOfA_NeverIncludeDataOfB()
    {
        var a = await factory.NewOrgAsync("Report Iso A");
        var b = await factory.NewOrgAsync("Report Iso B");
        var pipelineB = await b.Admin.DefaultPipelineAsync();
        var accountB = await b.Admin.CreateAccountAsync("B Firması");
        await CreateDealAsync(b.Admin, accountB.Id(), "B kazandı", 7777m, stageId: pipelineB.StageId("Kazanıldı"));
        await CreateDealAsync(b.Admin, accountB.Id(), "B açık", 123m);
        await b.Admin.CreateLeadAsync("B", "B Şirketi", new { source = "web" });
        _ = await a.Admin.DefaultPipelineAsync();

        var funnelA = await a.Admin.GetJsonAsync($"{Base}/reports/sales/funnel");
        funnelA.GetProperty("stages").EnumerateArray().Sum(s => s.GetProperty("count").GetInt32()).ShouldBe(0);
        await (await a.Admin.GetAsync($"{Base}/reports/sales/funnel?pipelineId={pipelineB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        var wonLostA = await a.Admin.GetJsonAsync($"{Base}/reports/sales/won-lost");
        wonLostA.EnumerateArray().ShouldAllBe(p => p.GetProperty("wonCount").GetInt32() == 0 && p.GetProperty("wonAmount").GetDecimal() == 0m);
        (await a.Admin.GetJsonAsync($"{Base}/reports/sales/leads-by-source")).GetArrayLength().ShouldBe(0);
        (await a.Admin.GetJsonAsync($"{Base}/reports/sales/by-owner")).GetArrayLength().ShouldBe(0);

        // B kendi verisini görür.
        (await b.Admin.GetJsonAsync($"{Base}/reports/sales/by-owner"))[0].GetProperty("wonAmount").GetDecimal().ShouldBe(7777m);
    }
}
