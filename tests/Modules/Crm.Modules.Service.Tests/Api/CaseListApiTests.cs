using System.Net;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Service.Tests.Api;

/// <summary>Liste: filtreler, sıralama, sayfalama, arama kaçışı, özet sayaçları.</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseListApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task List_FiltersByStatusAndPriority_WithCommaSeparatedValues_AndChannelAccountContactAssignee()
    {
        var org = await factory.NewOrgAsync("List Filters");
        var other = await factory.NewOrgAsync("List Filters Other");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Mert Kaya", "crm.cases.read");
        var acme = await admin.CreateAccountAsync("Acme");
        var ayse = await admin.CreateContactAsync("Yılmaz");

        var a = (await admin.CreateCaseAsync("A yeni düşük", new { priority = "low", channel = "email", accountId = acme.Id() })).Id();
        var b = (await admin.CreateCaseAsync("B açık yüksek", new { priority = "high", channel = "phone", assignedUserId = memberId, contactId = ayse.Id() })).Id();
        var c = (await admin.CreateCaseAsync("C bekleyen acil", new { priority = "urgent", channel = "web", assignedUserId = org.AdminUserId })).Id();
        var d = (await admin.CreateCaseAsync("D çözülmüş", new { priority = "normal" })).Id();
        var e = (await admin.CreateCaseAsync("E kapalı", new { priority = "high" })).Id();
        await admin.SetStatusAsync(b, "open");
        await admin.SetStatusAsync(c, "pending");
        await admin.SetStatusAsync(d, "resolved", "ok");
        await admin.SetStatusAsync(e, "closed", "ok");
        await other.Admin.CreateCaseAsync("Başkasının");

        (await admin.ListIdsAsync("?pageSize=100")).ShouldBe([e, d, c, b, a], "varsayılan sıra -createdAt");
        (await admin.ListIdsAsync("?status=new,open,pending")).Order().ShouldBe(new[] { a, b, c }.Order());
        (await admin.ListIdsAsync("?status=resolved")).ShouldBe([d]);
        (await admin.ListIdsAsync("?status=Resolved,CLOSED")).Order().ShouldBe(new[] { d, e }.Order(), "büyük/küçük harf duyarsız");
        (await admin.ListIdsAsync("?priority=high,urgent")).Order().ShouldBe(new[] { b, c, e }.Order());
        (await admin.ListIdsAsync("?status=open,pending&priority=urgent")).ShouldBe([c]);
        (await admin.ListIdsAsync("?channel=phone")).ShouldBe([b]);
        (await admin.ListIdsAsync($"?assignedUserId={memberId}")).ShouldBe([b]);
        (await admin.ListIdsAsync($"?accountId={acme.Id()}")).ShouldBe([a]);
        (await admin.ListIdsAsync($"?contactId={ayse.Id()}")).ShouldBe([b]);
        (await admin.ListIdsAsync("?unassigned=true")).Order().ShouldBe(new[] { a, d, e }.Order());
        (await admin.ListIdsAsync("?unassigned=true&status=new,open,pending")).ShouldBe([a]);

        // Bilinmeyen değerler validation; unassigned + assignedUserId birlikte olamaz.
        foreach (var bad in new[] { "status=bogus", "status=open,bogus", "priority=extreme", "slaState=maybe", "status=1" })
        {
            await (await admin.GetAsync($"{CasesPath}?{bad}", Ct)).ShouldBeValidationErrorAsync(bad.Split('=')[0]);
        }

        (await admin.GetAsync($"{CasesPath}?channel=smoke", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await (await admin.GetAsync($"{CasesPath}?unassigned=true&assignedUserId={memberId}", Ct)).ShouldBeValidationErrorAsync("unassigned");
    }

    [Fact]
    public async Task List_Search_MatchesNumberAndSubject_WithEscapedWildcards()
    {
        var admin = (await factory.NewOrgAsync("List Search")).Admin;
        var invoice = await admin.CreateCaseAsync("Fatura hatalı");
        var literal = await admin.CreateCaseAsync("Yüzde %100 iade_talebi");
        await admin.CreateCaseAsync("Başka konu");

        (await admin.ListIdsAsync("?q=fatura")).ShouldBe([invoice.Id()]);
        (await admin.ListIdsAsync("?q=FATURA")).ShouldBe([invoice.Id()], "büyük/küçük harf duyarsız");
        (await admin.ListIdsAsync($"?q={invoice.Str("number")}")).ShouldBe([invoice.Id()], "numarayla arama");
        (await admin.ListIdsAsync("?q=%25100")).ShouldBe([literal.Id()], "% joker değil düz metin");
        (await admin.ListIdsAsync("?q=iade_talebi")).ShouldBe([literal.Id()]);
        (await admin.ListIdsAsync("?q=%25")).ShouldBe([literal.Id()], "yalnız % içeren konu");
        (await admin.ListIdsAsync("?q=_")).ShouldBe([literal.Id()], "alt çizgi joker değil");
        (await admin.ListIdsAsync("?q=yokboyle")).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_Sorting_UsesTheWhitelist_EnumOrderForStatusAndPriority_AndIsStable()
    {
        using var host = new ClockedHost(factory);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var org = await host.NewOrgAsync("List Sort", t0);
        var admin = org.Admin;

        var ids = new Dictionary<string, Guid>();
        foreach (var (name, priority) in new[] { ("b-normal", "normal"), ("d-urgent", "urgent"), ("a-low", "low"), ("c-high", "high") })
        {
            host.Clock.Advance(TimeSpan.FromMinutes(1));
            ids[name] = (await admin.CreateCaseAsync(name, new { priority })).Id();
        }

        await admin.SetStatusAsync(ids["c-high"], "open");
        await admin.SetStatusAsync(ids["a-low"], "resolved", "ok");
        await admin.SetStatusAsync(ids["d-urgent"], "closed", "ok");
        // Durumlar: b-normal=new, c-high=open, a-low=resolved, d-urgent=closed

        async Task<List<string>> Subjects(string sort) =>
            (await admin.GetJsonAsync($"{CasesPath}?sort={sort}")).GetProperty("items").EnumerateArray().Select(i => i.Str("subject")).ToList();

        (await Subjects("priority")).ShouldBe(["a-low", "b-normal", "c-high", "d-urgent"], "low < normal < high < urgent");
        (await Subjects("-priority")).ShouldBe(["d-urgent", "c-high", "b-normal", "a-low"]);
        (await Subjects("status")).ShouldBe(["b-normal", "c-high", "a-low", "d-urgent"], "new < open < pending < resolved < closed (metin sırası değil)");
        (await Subjects("-status")).ShouldBe(["d-urgent", "a-low", "c-high", "b-normal"]);
        (await Subjects("subject")).ShouldBe(["a-low", "b-normal", "c-high", "d-urgent"]);
        (await Subjects("-subject")).ShouldBe(["d-urgent", "c-high", "b-normal", "a-low"]);
        (await Subjects("createdAt")).ShouldBe(["b-normal", "d-urgent", "a-low", "c-high"]);
        (await Subjects("-createdAt")).ShouldBe(["c-high", "a-low", "d-urgent", "b-normal"]);
        (await Subjects("")).ShouldBe(["c-high", "a-low", "d-urgent", "b-normal"], "varsayılan -createdAt");
        (await Subjects("bogus")).ShouldBe(["c-high", "a-low", "d-urgent", "b-normal"], "bilinmeyen alan yok sayılır");
        (await Subjects("dueAt")).ShouldBe(["d-urgent", "c-high", "b-normal", "a-low"], "urgent < high < normal < low çözüm süreleri (aynı başlangıç değil ama sırayla)");
        (await Subjects("number")).ShouldBe(["b-normal", "d-urgent", "a-low", "c-high"]);
        (await Subjects("updatedAt")).Count.ShouldBe(4);

        // Çok anahtarlı sıralama: önce öncelik azalan sonra konu.
        (await Subjects("-priority,subject")).ShouldBe(["d-urgent", "c-high", "b-normal", "a-low"]);
    }

    [Fact]
    public async Task List_Paging_ReturnsTotals_ClampsThePageSize_AndIsStable()
    {
        var admin = (await factory.NewOrgAsync("List Paging")).Admin;
        for (var i = 1; i <= 7; i++)
        {
            await admin.CreateCaseAsync($"Sayfa {i}");
        }

        var page2 = await admin.GetJsonAsync($"{CasesPath}?page=2&pageSize=3&sort=number");
        (page2.GetProperty("page").GetInt32(), page2.GetProperty("pageSize").GetInt32(), page2.GetProperty("totalCount").GetInt32()).ShouldBe((2, 3, 7));
        page2.GetProperty("items").EnumerateArray().Select(i => i.Str("subject")).ToList().ShouldBe(["Sayfa 4", "Sayfa 5", "Sayfa 6"]);

        var last = await admin.GetJsonAsync($"{CasesPath}?page=3&pageSize=3&sort=number");
        last.GetProperty("items").GetArrayLength().ShouldBe(1);

        (await admin.GetJsonAsync($"{CasesPath}?pageSize=1000")).GetProperty("pageSize").GetInt32().ShouldBe(100, "üst sınır 100");
        (await admin.GetJsonAsync(CasesPath)).GetProperty("pageSize").GetInt32().ShouldBe(25, "varsayılan 25");

        // Aynı anahtarlı (tüm createdAt eşit olabilir) sıralama sayfalar arasında tekrar/atlama yapmaz.
        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            seen.AddRange(await admin.ListIdsAsync($"?page={page}&pageSize=2&sort=priority"));
        }

        seen.Distinct().Count().ShouldBe(7);
    }

    [Fact]
    public async Task ListItem_HasNoDescriptionOrResolutionNote_WhileTheDetailHasBoth()
    {
        var org = await factory.NewOrgAsync("List Shape");
        var admin = org.Admin;
        var acme = await admin.CreateAccountAsync("Acme A.Ş.");
        var contact = await admin.CreateContactAsync("Yılmaz", acme.Id());
        var id = (await admin.CreateCaseAsync("Şekil", new { description = "detay", contactId = contact.Id() })).Id();
        await admin.SetStatusAsync(id, "resolved", "Çözüldü");

        var item = (await admin.GetJsonAsync(CasesPath)).GetProperty("items")[0];
        item.Has("description").ShouldBeFalse();
        item.Has("resolutionNote").ShouldBeFalse();
        item.Str("accountName").ShouldBe("Acme A.Ş.");
        item.Str("contactName").ShouldBe("Test Yılmaz");
        item.Str("createdByName").ShouldBe(org.AdminName);

        var detail = await admin.GetCaseAsync(id);
        (detail.Str("description"), detail.Str("resolutionNote")).ShouldBe(("detay", "Çözüldü"));
    }

    [Fact]
    public async Task Summary_CountsActiveCases_OpenOverdueMineAndUnassigned()
    {
        using var host = new ClockedHost(factory);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var org = await host.NewOrgAsync("List Summary", t0);
        var admin = org.Admin;
        var (member, memberId) = await host.AddMemberAsync(org, "Mert Kaya", "crm.cases.read");

        (await admin.GetJsonAsync($"{CasesPath}/summary")).EnumerateObject().Select(p => p.Value.GetInt32()).ShouldBe([0, 0, 0, 0]);

        var mineOnTime = (await admin.CreateCaseAsync("Benim", new { assignedUserId = org.AdminUserId, priority = "low" })).Id();
        var mineOverdue = (await admin.CreateCaseAsync("Benim geciken", new { assignedUserId = org.AdminUserId, priority = "urgent" })).Id();
        var unassigned = (await admin.CreateCaseAsync("Sahipsiz", new { priority = "low" })).Id();
        var theirs = (await admin.CreateCaseAsync("Onun", new { assignedUserId = memberId, priority = "low" })).Id();
        var resolved = (await admin.CreateCaseAsync("Çözülmüş", new { assignedUserId = org.AdminUserId, priority = "urgent" })).Id();
        var closed = (await admin.CreateCaseAsync("Kapalı", new { priority = "urgent" })).Id();
        await admin.SetStatusAsync(resolved, "resolved", "ok");
        await admin.SetStatusAsync(closed, "closed", "ok");
        await admin.SetStatusAsync(theirs, "pending");

        host.Clock.SetUtcNow(t0.AddMinutes(61)); // urgent ilk yanıt hedefi (60 dk) aşıldı; low'lar sağlam.

        var summary = await admin.GetJsonAsync($"{CasesPath}/summary");
        summary.GetProperty("openCount").GetInt32().ShouldBe(4, "aktif: new/open/pending");
        summary.GetProperty("overdueCount").GetInt32().ShouldBe(1, "yalnız aktif ve ihlalli (çözülmüş/kapalı ihlalsiz)");
        summary.GetProperty("mineCount").GetInt32().ShouldBe(2);
        summary.GetProperty("unassignedCount").GetInt32().ShouldBe(1);
        (await admin.ListIdsAsync("?status=new,open,pending&slaState=breached")).ShouldBe([mineOverdue]);

        // Sayaçlar çağıran kullanıcıya göredir.
        var theirSummary = await member.GetJsonAsync($"{CasesPath}/summary");
        theirSummary.GetProperty("mineCount").GetInt32().ShouldBe(1);
        theirSummary.GetProperty("openCount").GetInt32().ShouldBe(4);
        _ = (mineOnTime, unassigned);
    }
}
