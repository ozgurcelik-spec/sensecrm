using System.Net;
using System.Net.Http.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Marketing.Tests.Api.MarketingApiKit;

namespace Crm.Modules.Marketing.Tests.Api;

/// <summary>Kampanya listesi: filtreler, çoklu değer, joker kaçışı, tarih uçları, sıralama (boşlar sonda), sayfalama.</summary>
[Collection(ApiCollection.Name)]
public sealed class CampaignQueryApiTests(CrmApiFactory factory)
{
    /// <summary>
    /// A "Sonbahar E-posta" email planned 2026-10-01 bütçe 1000 (açıklama "%50 indirim"); B "Fuar 2026" event active 2026-11-01 bütçe 5000;
    /// C "Webinar_A" webinar planned başlangıçsız bütçesiz; D "Reklam" advertising cancelled 2026-09-01 bütçe 300. Oluşturma sırası A, B, C, D.
    /// </summary>
    private async Task<(Org Org, Guid A, Guid B, Guid C, Guid D)> SeedAsync(string orgName)
    {
        var org = await factory.NewOrgAsync(orgName);
        var admin = org.Admin;
        var a = await admin.NewCampaignIdAsync("Sonbahar E-posta", "email", new { startDate = "2026-10-01", budget = 1000, description = "%50 indirim" });
        var b = await admin.NewCampaignIdAsync("Fuar 2026", "event", new { status = "active", startDate = "2026-11-01", budget = 5000 });
        var c = await admin.NewCampaignIdAsync("Webinar_A", "webinar");
        var d = await admin.NewCampaignIdAsync("Reklam", "advertising", new { startDate = "2026-09-01", budget = 300 });
        await admin.PostJsonAsync($"{CampaignsPath}/{d}/status", new { status = "cancelled" }, HttpStatusCode.NoContent);
        return (org, a, b, c, d);
    }

    [Fact]
    public async Task List_DefaultSortIsNewestFirst_AndPagesAreStable()
    {
        var (org, a, b, c, d) = await SeedAsync("Mkt List Default");
        var admin = org.Admin;

        (await admin.ListIdsAsync()).ShouldBe([d, c, b, a]);

        var first = await admin.GetJsonAsync($"{CampaignsPath}?pageSize=3&page=1");
        var second = await admin.GetJsonAsync($"{CampaignsPath}?pageSize=3&page=2");
        (first.Int("page"), first.Int("pageSize"), first.GetProperty("totalCount").GetInt64()).ShouldBe((1, 3, 4));
        first.GetProperty("items").GetArrayLength().ShouldBe(3);
        second.GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([a]);

        (await admin.GetJsonAsync($"{CampaignsPath}?pageSize=1000")).Int("pageSize").ShouldBe(100);
        (await admin.ListIdsAsync("?sort=bogus,-nope")).ShouldBe([d, c, b, a]);
    }

    [Fact]
    public async Task List_FiltersByTypeStatusOwner_WithMultipleValues_AndRejectsUnknownOnes()
    {
        var (org, a, b, c, d) = await SeedAsync("Mkt List Filters");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Başka Sahip", "crm.campaigns.read");
        var owned = await admin.NewCampaignIdAsync("Başkasının", "other", new { ownerUserId = memberId });

        (await admin.ListIdsAsync("?status=planned,active&sort=createdAt")).ShouldBe([a, b, c, owned]);
        (await admin.ListIdsAsync("?status=cancelled")).ShouldBe([d]);
        (await admin.ListIdsAsync("?type=email,event&sort=createdAt")).ShouldBe([a, b]);
        (await admin.ListIdsAsync("?type=EMAIL&status=Planned")).ShouldBe([a]);
        (await admin.ListIdsAsync($"?ownerUserId={memberId}")).ShouldBe([owned]);
        (await admin.ListIdsAsync($"?ownerUserId={org.AdminUserId}&type=other")).ShouldBeEmpty();

        await (await admin.GetAsync($"{CampaignsPath}?status=bogus", Ct)).ShouldBeValidationErrorAsync("status");
        await (await admin.GetAsync($"{CampaignsPath}?status=planned,bogus", Ct)).ShouldBeValidationErrorAsync("status");
        await (await admin.GetAsync($"{CampaignsPath}?type=nope", Ct)).ShouldBeValidationErrorAsync("type");
    }

    [Fact]
    public async Task List_SearchEscapesWildcards_AndMatchesNameAndDescription()
    {
        var (org, a, _, c, _) = await SeedAsync("Mkt List Search");
        var admin = org.Admin;

        (await admin.ListIdsAsync("?q=sonbahar")).ShouldBe([a]);
        (await admin.ListIdsAsync("?q=indirim")).ShouldBe([a]);
        (await admin.ListIdsAsync("?q=%25")).ShouldBe([a], "% düz metindir, joker değil");
        (await admin.ListIdsAsync("?q=_")).ShouldBe([c], "_ düz metindir, tek karakter jokeri değil");
        (await admin.ListIdsAsync("?q=%25%25%25")).ShouldBeEmpty();
        (await admin.ListIdsAsync("?q=yok-boyle-bir-sey")).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_StartDateRange_IsInclusive_AndExcludesCampaignsWithoutStartDate()
    {
        var (org, a, b, c, d) = await SeedAsync("Mkt List Dates");
        var admin = org.Admin;

        (await admin.ListIdsAsync("?startFrom=2026-10-01&sort=startDate")).ShouldBe([a, b]);
        (await admin.ListIdsAsync("?startTo=2026-10-01&sort=startDate")).ShouldBe([d, a]);
        (await admin.ListIdsAsync("?startFrom=2026-10-01&startTo=2026-10-01")).ShouldBe([a]);
        (await admin.ListIdsAsync("?startFrom=2026-09-01&startTo=2026-11-01&sort=startDate")).ShouldBe([d, a, b]);
        (await admin.ListIdsAsync("?startFrom=2026-12-01")).ShouldBeEmpty();
        (await admin.ListIdsAsync("?startFrom=2026-01-01&startTo=2026-12-31")).ShouldNotContain(c);
    }

    [Fact]
    public async Task List_Sorts_ByWhitelistedFields_WithEmptyValuesAlwaysLast()
    {
        var (org, a, b, c, d) = await SeedAsync("Mkt List Sort");
        var admin = org.Admin;

        (await admin.ListIdsAsync("?sort=startDate")).ShouldBe([d, a, b, c]);
        (await admin.ListIdsAsync("?sort=-startDate")).ShouldBe([b, a, d, c]);
        (await admin.ListIdsAsync("?sort=budget")).ShouldBe([d, a, b, c]);
        (await admin.ListIdsAsync("?sort=-budget")).ShouldBe([b, a, d, c]);
        (await admin.ListIdsAsync("?sort=name")).ShouldBe([b, d, a, c]);
        (await admin.ListIdsAsync("?sort=-name")).ShouldBe([c, a, d, b]);
        (await admin.ListIdsAsync("?sort=createdAt")).ShouldBe([a, b, c, d]);
        (await admin.ListIdsAsync("?sort=type")).ShouldBe([a, b, c, d], "email < event < webinar < advertising");
        (await admin.ListIdsAsync("?sort=-status,name")).ShouldBe([d, b, a, c], "iptal, aktif, planlı (ad artan)");
        (await admin.ListIdsAsync("?sort=actualCost")).Count.ShouldBe(4);
        (await admin.ListIdsAsync("?sort=endDate")).Count.ShouldBe(4);
    }

    [Fact]
    public async Task List_ReportsMemberCounts_InTheSameShapeAsDetail()
    {
        var org = await factory.NewOrgAsync("Mkt List Counts");
        var admin = org.Admin;
        var withMembers = await admin.NewCampaignIdAsync("Üyeli");
        var empty = await admin.NewCampaignIdAsync("Boş");
        await admin.AddMembersAsync(withMembers, "lead", await admin.NewLeadIdsAsync(3));
        await admin.AddMembersAsync(withMembers, "contact", [(await admin.CreateContactAsync("Kişi")).Id()]);

        var page = await admin.GetJsonAsync($"{CampaignsPath}?sort=name");
        var counts = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.Id(), i => i.Int("memberCount"));
        counts[withMembers].ShouldBe(4);
        counts[empty].ShouldBe(0);
        (await admin.GetJsonAsync($"{CampaignsPath}/{withMembers}")).Int("memberCount").ShouldBe(4);
        (await admin.PostAsJsonAsync($"{CampaignsPath}/{empty}/members/remove", new { memberIds = new[] { Guid.NewGuid() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
