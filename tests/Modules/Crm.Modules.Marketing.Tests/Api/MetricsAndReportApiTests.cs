using System.Net;
using System.Net.Http.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Marketing.Tests.Api.MarketingApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Marketing.Tests.Api;

/// <summary>Kampanya metrikleri ve pazarlama raporu: bilinen veri kümeleriyle elle hesaplanmış değerlerle karşılaştırılır.</summary>
[Collection(ApiCollection.Name)]
public sealed class MetricsAndReportApiTests(CrmApiFactory factory)
{
    private const string Summary = $"{Base}/reports/marketing/summary";

    private async Task SetStatusAsync(HttpClient admin, Guid campaign, Guid memberRecordId, string status)
    {
        var row = (await admin.StatusOfAsync(campaign, memberRecordId)).Id();
        await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/status", new { memberIds = new[] { row }, status }, HttpStatusCode.OK);
    }

    private async Task<Guid> AddLeadsAsync(Org org, Guid campaign, params string[] statuses)
    {
        Guid last = Guid.Empty;
        var leads = await org.Admin.NewLeadIdsAsync(statuses.Length, "M");
        await org.Admin.AddMembersAsync(campaign, "lead", leads);
        for (var i = 0; i < statuses.Length; i++)
        {
            last = leads[i];
            if (statuses[i] == "converted")
            {
                await factory.PublishLeadConvertedAsync(org.TenantId, leads[i]);
            }
            else if (statuses[i] != "added")
            {
                await SetStatusAsync(org.Admin, campaign, leads[i], statuses[i]);
            }
        }

        return last;
    }

    private async Task<List<Guid>> AddContactsAsync(Org org, Guid campaign, params string[] statuses)
    {
        var ids = new List<Guid>();
        foreach (var status in statuses)
        {
            var contact = (await org.Admin.CreateContactAsync($"K{ids.Count}")).Id();
            ids.Add(contact);
            await org.Admin.AddMembersAsync(campaign, "contact", [contact]);
            if (status != "added")
            {
                await SetStatusAsync(org.Admin, campaign, contact, status);
            }
        }

        return ids;
    }

    [Fact]
    public async Task Metrics_MatchHandComputedValues_AndRecomputeWhenStatusesChange()
    {
        var org = await factory.NewOrgAsync("Mkt Metrics");
        var admin = org.Admin;
        var campaign = await admin.NewCampaignIdAsync("Ölçüm", "email", new { actualCost = 12000, budget = 20000, currency = "usd" });

        // 10 lead: 2 added, 3 sent, 3 responded, 2 converted. 4 kişi: 1 added, 2 sent, 1 responded.
        await AddLeadsAsync(org, campaign, "added", "added", "sent", "sent", "sent", "responded", "responded", "responded", "converted", "converted");
        var contacts = await AddContactsAsync(org, campaign, "added", "sent", "sent", "responded");

        var m = await admin.MetricsAsync(campaign);
        m.GetProperty("campaignId").GetGuid().ShouldBe(campaign);
        (m.Int("memberCount"), m.Int("leadCount"), m.Int("contactCount")).ShouldBe((14, 10, 4));
        var counts = m.GetProperty("statusCounts");
        (counts.Int("added"), counts.Int("sent"), counts.Int("responded"), counts.Int("converted"), counts.Int("unsubscribed")).ShouldBe((3, 5, 4, 2, 0));
        (m.Int("contactedCount"), m.Int("responseCount")).ShouldBe((11, 6));
        m.Dec("responseRate").ShouldBe(54.55m);
        (m.Int("convertedCount"), m.Dec("conversionRate")).ShouldBe((2, 20m));
        (m.Str("currency"), m.Dec("costPerLead")).ShouldBe(("USD", 1200m));

        // Bir kişi "abonelikten çıktı", bir lead "yanıtladı" → yeniden hesaplanır (saklanmaz).
        await SetStatusAsync(admin, campaign, contacts[1], "unsubscribed");
        var another = (await admin.MembersAsync(campaign, "?memberType=lead&status=sent")).First();
        await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/status", new { memberIds = new[] { another.Id() }, status = "responded" }, HttpStatusCode.OK);

        var m2 = await admin.MetricsAsync(campaign);
        var counts2 = m2.GetProperty("statusCounts");
        (counts2.Int("sent"), counts2.Int("responded"), counts2.Int("unsubscribed")).ShouldBe((3, 5, 1));
        (m2.Int("contactedCount"), m2.Int("responseCount")).ShouldBe((11, 7));
        m2.Dec("responseRate").ShouldBe(63.64m);
        (m2.Int("memberCount"), m2.Dec("conversionRate")).ShouldBe((14, 20m));

        // Üye çıkarılınca sayımlar düşer.
        var row = (await admin.StatusOfAsync(campaign, contacts[0])).Id();
        await admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/remove", new { memberIds = new[] { row } }, HttpStatusCode.OK);
        (await admin.MetricsAsync(campaign)).Int("memberCount").ShouldBe(13);
    }

    [Fact]
    public async Task Metrics_OfAnEmptyCampaign_AreZero_WithAllFiveStatusKeys_AndNoCostPerLead()
    {
        var admin = (await factory.NewOrgAsync("Mkt Metrics Empty")).Admin;
        var campaign = await admin.NewCampaignIdAsync("Boş", "event", new { actualCost = 500 });

        var m = await admin.MetricsAsync(campaign);

        (m.Int("memberCount"), m.Int("leadCount"), m.Int("contactCount"), m.Int("contactedCount"), m.Int("responseCount"), m.Int("convertedCount")).ShouldBe((0, 0, 0, 0, 0, 0));
        (m.Dec("responseRate"), m.Dec("conversionRate")).ShouldBe((0m, 0m));
        var counts = m.GetProperty("statusCounts").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())).ToList();
        counts.ShouldBe([("added", 0), ("sent", 0), ("responded", 0), ("converted", 0), ("unsubscribed", 0)]);
        m.Has("costPerLead").ShouldBeFalse("lead yok → yazılmaz");
        m.Str("currency").ShouldBe("TRY");
    }

    [Fact]
    public async Task CostPerLead_IsOmitted_WithoutActualCost_OrWithoutLeads_AndRoundedOtherwise()
    {
        var org = await factory.NewOrgAsync("Mkt Metrics Cost");
        var admin = org.Admin;
        var noCost = await admin.NewCampaignIdAsync("Maliyetsiz");
        await AddLeadsAsync(org, noCost, "added", "sent");
        var contactsOnly = await admin.NewCampaignIdAsync("Yalnız kişi", extra: new { actualCost = 900 });
        await AddContactsAsync(org, contactsOnly, "sent");
        var zeroCost = await admin.NewCampaignIdAsync("Sıfır maliyet", extra: new { actualCost = 0 });
        await AddLeadsAsync(org, zeroCost, "added");
        var thirds = await admin.NewCampaignIdAsync("Üçte bir", extra: new { actualCost = 100 });
        await AddLeadsAsync(org, thirds, "added", "added", "added");

        (await admin.MetricsAsync(noCost)).Has("costPerLead").ShouldBeFalse();
        (await admin.MetricsAsync(contactsOnly)).Has("costPerLead").ShouldBeFalse();
        (await admin.MetricsAsync(zeroCost)).Dec("costPerLead").ShouldBe(0m);
        (await admin.MetricsAsync(thirds)).Dec("costPerLead").ShouldBe(33.33m);
    }

    [Fact]
    public async Task Report_MatchesHandComputedValues_ForAMultiCampaignDataSet()
    {
        var org = await factory.NewOrgAsync("Mkt Report");
        var admin = org.Admin;
        var c1 = await admin.NewCampaignIdAsync("C1 E-posta", "email", new { status = "active", startDate = "2024-03-10", budget = 1000, expectedRevenue = 5000, actualCost = 800 });
        var c2 = await admin.NewCampaignIdAsync("C2 E-posta", "email", new { startDate = "2024-06-01", budget = 2000, actualCost = 500 });
        var c3 = await admin.NewCampaignIdAsync("C3 Etkinlik", "event", new { status = "active", startDate = "2024-12-31", budget = 300 });
        var c4 = await admin.NewCampaignIdAsync("C4 Web semineri", "webinar", new { startDate = "2024-01-01" });
        var outsideBefore = await admin.NewCampaignIdAsync("Önce", "advertising", new { startDate = "2023-12-31", budget = 9999 });
        var outsideAfter = await admin.NewCampaignIdAsync("Sonra", "other", new { startDate = "2025-01-01", budget = 9999 });
        var deleted = await admin.NewCampaignIdAsync("Silinmiş", "email", new { startDate = "2024-05-05", budget = 9999 });
        await AddLeadsAsync(org, c1, "added", "sent", "responded", "converted");
        await AddContactsAsync(org, c1, "sent");
        await AddLeadsAsync(org, c2, "converted", "converted");
        await AddContactsAsync(org, c3, "sent", "sent", "unsubscribed");
        await AddLeadsAsync(org, deleted, "converted");
        await AddLeadsAsync(org, outsideBefore, "converted");
        await admin.PostJsonAsync($"{CampaignsPath}/{c3}/status", new { status = "completed" }, HttpStatusCode.NoContent);
        await admin.PostJsonAsync($"{CampaignsPath}/{c4}/status", new { status = "cancelled" }, HttpStatusCode.NoContent);
        await admin.DeleteJsonAsync($"{CampaignsPath}/{deleted}");

        var report = await admin.GetJsonAsync($"{Summary}?from=2024-01-01&to=2024-12-31");

        (report.Str("from"), report.Str("to"), report.Int("campaignCount")).ShouldBe(("2024-01-01", "2024-12-31", 4));
        report.GetProperty("byStatus").EnumerateArray().Select(r => (r.Str("status"), r.Int("count"))).ToList()
            .ShouldBe([("planned", 1), ("active", 1), ("completed", 1), ("cancelled", 1)]);

        var byType = report.GetProperty("byType").EnumerateArray().ToList();
        byType.Select(r => r.Str("type")).ShouldBe(["email", "event", "webinar", "advertising", "other"]);
        (byType[0].Int("count"), byType[0].Dec("budget"), byType[0].Dec("actualCost"), byType[0].Int("memberCount"), byType[0].Int("convertedCount")).ShouldBe((2, 3000m, 1300m, 7, 3));
        (byType[1].Int("count"), byType[1].Dec("budget"), byType[1].Dec("actualCost"), byType[1].Int("memberCount"), byType[1].Int("convertedCount")).ShouldBe((1, 300m, 0m, 3, 0));
        (byType[2].Int("count"), byType[2].Int("memberCount")).ShouldBe((1, 0));
        byType.Skip(3).ShouldAllBe(r => r.Int("count") == 0 && r.Dec("budget") == 0m && r.Int("memberCount") == 0);

        // Toplamlar: 10 üye, 6 lead, 9 ulaşılan, 4 yanıt, 3 dönüşen; oranlar toplam sayılar üzerinden.
        var totals = report.GetProperty("totals");
        (totals.Dec("budget"), totals.Dec("expectedRevenue"), totals.Dec("actualCost")).ShouldBe((3300m, 5000m, 1300m));
        (totals.Int("memberCount"), totals.Int("leadCount"), totals.Int("contactedCount"), totals.Int("responseCount"), totals.Int("convertedCount")).ShouldBe((10, 6, 9, 4, 3));
        (totals.Dec("responseRate"), totals.Dec("conversionRate"), totals.Dec("costPerLead")).ShouldBe((44.44m, 50m, 216.67m));

        // En iyi kampanyalar: dönüşen azalan, sonra üye azalan, sonra ad.
        var top = report.GetProperty("topCampaigns").EnumerateArray().ToList();
        top.Select(t => t.Id()).ShouldBe([c2, c1, c3, c4]);
        (top[0].Int("memberCount"), top[0].Int("convertedCount"), top[0].Dec("responseRate"), top[0].Str("status")).ShouldBe((2, 2, 100m, "planned"));
        (top[1].Str("name"), top[1].Str("type"), top[1].Int("memberCount"), top[1].Int("convertedCount"), top[1].Dec("responseRate")).ShouldBe(("C1 E-posta", "email", 5, 1, 50m));
        (top[1].Dec("budget"), top[1].Dec("actualCost")).ShouldBe((1000m, 800m));
        top[3].Has("budget").ShouldBeFalse();

        // Aralık uçları dahildir: 2024-12-31 (C3) ve 2024-01-01 (C4) sınırda.
        (await admin.GetJsonAsync($"{Summary}?from=2024-01-02&to=2024-12-31")).Int("campaignCount").ShouldBe(3);
        (await admin.GetJsonAsync($"{Summary}?from=2024-01-01&to=2024-12-30")).Int("campaignCount").ShouldBe(3);
        (await admin.GetJsonAsync($"{Summary}?from=2024-01-01&to=2024-01-01")).Int("campaignCount").ShouldBe(1);
        (await admin.GetJsonAsync($"{Summary}?from=2023-12-31&to=2023-12-31")).GetProperty("topCampaigns").EnumerateArray().Single().Id().ShouldBe(outsideBefore);
        (await admin.GetJsonAsync($"{Summary}?from=2025-01-01&to=2025-01-01")).GetProperty("topCampaigns").EnumerateArray().Single().Id().ShouldBe(outsideAfter);
    }

    [Fact]
    public async Task Report_TopCampaigns_AreCappedAtTen_AndOrderedDeterministically()
    {
        var org = await factory.NewOrgAsync("Mkt Report Top");
        var admin = org.Admin;
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(await admin.NewCampaignIdAsync($"Kampanya {i:D2}", "other", new { startDate = "2024-02-01" }));
        }

        // Ad artan sırası (üyesiz kampanyalar eşit): ilk 10 ad.
        var report = await admin.GetJsonAsync($"{Summary}?from=2024-01-01&to=2024-12-31");
        report.Int("campaignCount").ShouldBe(12);
        report.GetProperty("topCampaigns").EnumerateArray().Select(t => t.Str("name")).ShouldBe(Enumerable.Range(0, 10).Select(i => $"Kampanya {i:D2}"));

        // Bir kampanyaya dönüşen ve üye eklenince başa geçer.
        await AddLeadsAsync(org, ids[11], "converted");
        await AddLeadsAsync(org, ids[10], "added", "added");
        var top = (await admin.GetJsonAsync($"{Summary}?from=2024-01-01&to=2024-12-31")).GetProperty("topCampaigns").EnumerateArray().Select(t => t.Id()).ToList();
        top.Count.ShouldBe(10);
        top.Take(2).ShouldBe([ids[11], ids[10]]);
    }

    [Fact]
    public async Task Report_FallsBackToCreatedAt_ForCampaignsWithoutStartDate_InTheTenantCalendar()
    {
        var admin = (await factory.NewOrgAsync("Mkt Report CreatedAt")).Admin;
        var undated = await admin.NewCampaignIdAsync("Başlangıçsız", "email");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        string D(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd");

        (await admin.GetJsonAsync($"{Summary}?from={D(-1)}&to={D(1)}")).GetProperty("topCampaigns").EnumerateArray().Select(t => t.Id()).ShouldBe([undated]);
        (await admin.GetJsonAsync($"{Summary}?from={D(-30)}&to={D(-3)}")).Int("campaignCount").ShouldBe(0);

        // startDate varsa createdAt yok sayılır: bugün oluşturulmuş ama başlangıcı geçmişte olan kampanya bugünün aralığında değildir.
        await admin.NewCampaignIdAsync("Geçmişte başlayan", "email", new { startDate = D(-200) });
        (await admin.GetJsonAsync($"{Summary}?from={D(-1)}&to={D(1)}")).Int("campaignCount").ShouldBe(1);
        (await admin.GetJsonAsync($"{Summary}?from={D(-201)}&to={D(-199)}")).Int("campaignCount").ShouldBe(1);

        // Varsayılan aralık son 12 ay: bugünkü başlangıçsız + 200 gün önce başlayan içeride, 3 yıl önce başlayan dışarıda.
        await admin.NewCampaignIdAsync("Çok eski", "email", new { startDate = D(-1100) });
        var report = await admin.GetJsonAsync(Summary);
        report.Int("campaignCount").ShouldBe(2);
        report.Str("to").ShouldNotBeNullOrEmpty();
        DateOnly.Parse(report.Str("from")).ShouldBeLessThan(today.AddMonths(-10));
    }

    [Fact]
    public async Task Report_RejectsInvalidRanges_AndWorksForAnEmptyOrganization()
    {
        var admin = (await factory.NewOrgAsync("Mkt Report Validation")).Admin;

        await (await admin.GetAsync($"{Summary}?from=2024-06-01&to=2024-05-31", Ct)).ShouldBeValidationErrorAsync("to");
        await (await admin.GetAsync($"{Summary}?from=2010-01-01&to=2024-01-01", Ct)).ShouldBeValidationErrorAsync("to");
        (await admin.GetAsync($"{Summary}?from={DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1):yyyy-MM-dd}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"{Summary}?from=garbage", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var empty = await admin.GetJsonAsync(Summary);
        empty.Int("campaignCount").ShouldBe(0);
        empty.GetProperty("byStatus").GetArrayLength().ShouldBe(4);
        empty.GetProperty("byType").GetArrayLength().ShouldBe(5);
        empty.GetProperty("topCampaigns").GetArrayLength().ShouldBe(0);
        var totals = empty.GetProperty("totals");
        (totals.Dec("budget"), totals.Int("memberCount"), totals.Dec("responseRate"), totals.Dec("conversionRate")).ShouldBe((0m, 0, 0m, 0m));
        totals.Has("costPerLead").ShouldBeFalse();
    }
}
