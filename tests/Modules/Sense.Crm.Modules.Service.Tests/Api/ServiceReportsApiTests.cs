using System.Net;
using System.Net.Http.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Service.Tests.Api;

/// <summary>Servis raporları: bilinen veri kümesiyle özet + temsilci bazlı, aralık uçları kiracı saat diliminde, doğrulama, izolasyon.</summary>
[Collection(ApiCollection.Name)]
public sealed class ServiceReportsApiTests(CrmApiFactory factory)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Summary_And_ByAssignee_MatchAKnownDataset()
    {
        using var host = new ClockedHost(factory);
        var org = await host.NewOrgAsync("Report Known", T0);
        var admin = org.Admin;
        var (_, memberId) = await host.AddMemberAsync(org, "Mert Kaya", "crm.cases.read");
        await admin.SendJsonAsync(HttpMethod.Patch, $"{Base}/organization/members/{memberId}", new { isActive = false }, HttpStatusCode.NoContent); // pasif üye adı da çözülür

        void At(int minutes) => host.Clock.SetUtcNow(T0.AddMinutes(minutes));

        // c1: normal, admin — ilk yanıt 60 dk, çözüm 600 dk → ihlalsiz
        At(0);
        var c1 = (await admin.CreateCaseAsync("c1", new { priority = "normal", assignedUserId = org.AdminUserId })).Id();
        // c2: urgent, Mert — yorumsuz çözülür (ilk yanıt = çözüm = 300 dk) → ihlalli
        At(10);
        var c2 = (await admin.CreateCaseAsync("c2", new { priority = "urgent" })).Id();
        // c3: high, admin — açık, hiç yanıt yok → rapor anında ihlalli
        At(20);
        var c3 = (await admin.CreateCaseAsync("c3", new { priority = "high", assignedUserId = org.AdminUserId })).Id();
        // c4: low, atanmamış — ilk yanıt 120 dk, açık → ihlalsiz
        At(30);
        var c4 = (await admin.CreateCaseAsync("c4", new { priority = "low" })).Id();
        // c5: normal, atanmamış — çözülmeden kapatılır (40 dk) → ihlalsiz
        At(40);
        var c5 = (await admin.CreateCaseAsync("c5", new { priority = "normal" })).Id();
        // c6: silinecek (kohort dışı)
        At(45);
        var c6 = (await admin.CreateCaseAsync("c6", new { priority = "urgent" })).Id();

        At(60);
        await admin.CommentAsync(c1, "public", "yanıt");
        At(80);
        await admin.SetStatusAsync(c5, "closed", "vazgeçti");
        await admin.DeleteJsonAsync($"{CasesPath}/{c6}");
        At(150);
        await admin.CommentAsync(c4, "public", "yanıt");
        await admin.SetStatusAsync(c3, "open");
        // c2 Mert'e (pasifleşmeden önce değil, şimdi Administrator ile) atanamaz: pasif üye → atama yalnız oluşturmadaki durumla; SQL ile atanır.
        await factory.ExecuteAsync("UPDATE service.cases SET assigned_user_id = @u WHERE id = @id", ("u", memberId), ("id", c2));
        At(310);
        await admin.SetStatusAsync(c2, "resolved", "çözüldü");
        At(600);
        await admin.SetStatusAsync(c1, "resolved", "çözüldü");

        // Rapor anı: T0 + 2 gün. Aralık: 2026-10-01 (Europe/Istanbul günü).
        host.Clock.SetUtcNow(T0.AddDays(2));
        var summary = await admin.GetJsonAsync($"{ReportsPath}/summary?from=2026-10-01&to=2026-10-01");

        summary.Str("from").ShouldBe("2026-10-01");
        summary.Str("to").ShouldBe("2026-10-01");
        summary.GetProperty("totalCount").GetInt32().ShouldBe(5, "silinmiş talep hariç");
        summary.GetProperty("resolvedCount").GetInt32().ShouldBe(3, "c1, c2 ve çözülmeden kapatılan c5");
        summary.GetProperty("byStatus").EnumerateArray().Select(s => (s.Str("status"), s.GetProperty("count").GetInt32())).ToList()
            .ShouldBe([("new", 0), ("open", 2), ("pending", 0), ("resolved", 2), ("closed", 1)]);
        summary.GetProperty("byPriority").EnumerateArray().Select(s => (s.Str("priority"), s.GetProperty("count").GetInt32())).ToList()
            .ShouldBe([("low", 1), ("normal", 2), ("high", 1), ("urgent", 1)]);
        summary.GetProperty("avgFirstResponseMinutes").GetDouble().ShouldBe(130.0, "(60 + 300 + 120 + 40) / 4");
        summary.GetProperty("avgResolutionMinutes").GetDouble().ShouldBe(313.3, "(600 + 300 + 40) / 3, 1 ondalık");
        summary.GetProperty("slaBreachedCount").GetInt32().ShouldBe(2, "c2 (geç çözüm/yanıt) ve c3 (yanıtsız)");
        summary.GetProperty("slaBreachRate").GetDouble().ShouldBe(0.4);

        var rows = (await admin.GetJsonAsync($"{ReportsPath}/by-assignee?from=2026-10-01&to=2026-10-01")).EnumerateArray().ToList();
        rows.Count.ShouldBe(3);

        // Sıra: totalCount azalan (atanmamış 2, admin 2, Mert 1), eşitlikte ad; atanmamış tek satır ve kimliksiz.
        var unassigned = rows.Single(r => !r.Has("assignedUserId"));
        unassigned.Has("assignedUserName").ShouldBeFalse();
        (unassigned.GetProperty("totalCount").GetInt32(), unassigned.GetProperty("openCount").GetInt32(), unassigned.GetProperty("resolvedCount").GetInt32(), unassigned.GetProperty("slaBreachedCount").GetInt32())
            .ShouldBe((2, 1, 1, 0));
        unassigned.GetProperty("avgFirstResponseMinutes").GetDouble().ShouldBe(80.0, "(120 + 40) / 2");
        unassigned.GetProperty("avgResolutionMinutes").GetDouble().ShouldBe(40.0);

        var adminRow = rows.Single(r => r.Has("assignedUserId") && r.GetProperty("assignedUserId").GetGuid() == org.AdminUserId);
        adminRow.Str("assignedUserName").ShouldBe(org.AdminName);
        (adminRow.GetProperty("totalCount").GetInt32(), adminRow.GetProperty("openCount").GetInt32(), adminRow.GetProperty("resolvedCount").GetInt32(), adminRow.GetProperty("slaBreachedCount").GetInt32())
            .ShouldBe((2, 1, 1, 1));
        adminRow.GetProperty("avgFirstResponseMinutes").GetDouble().ShouldBe(60.0, "yalnız c1'in yanıtı var");
        adminRow.GetProperty("avgResolutionMinutes").GetDouble().ShouldBe(600.0);

        var memberRow = rows.Single(r => r.Has("assignedUserId") && r.GetProperty("assignedUserId").GetGuid() == memberId);
        memberRow.Str("assignedUserName").ShouldBe("Mert Kaya", "pasif üye adı da çözülür");
        (memberRow.GetProperty("totalCount").GetInt32(), memberRow.GetProperty("openCount").GetInt32(), memberRow.GetProperty("resolvedCount").GetInt32(), memberRow.GetProperty("slaBreachedCount").GetInt32())
            .ShouldBe((1, 0, 1, 1));
        memberRow.GetProperty("avgFirstResponseMinutes").GetDouble().ShouldBe(300.0);
        rows.Select(r => r.GetProperty("totalCount").GetInt32()).ToList().ShouldBe([2, 2, 1]);
        _ = (c3, c4, c5);
    }

    [Fact]
    public async Task EmptyRange_ReturnsZerosAndOmitsAverages_WithFullZeroFilledBreakdowns()
    {
        var admin = (await factory.NewOrgAsync("Report Empty")).Admin;

        var summary = await admin.GetJsonAsync($"{ReportsPath}/summary?from=2000-01-01&to=2000-01-31");

        (summary.GetProperty("totalCount").GetInt32(), summary.GetProperty("resolvedCount").GetInt32(), summary.GetProperty("slaBreachedCount").GetInt32()).ShouldBe((0, 0, 0));
        summary.GetProperty("slaBreachRate").GetDouble().ShouldBe(0);
        summary.Has("avgFirstResponseMinutes").ShouldBeFalse("örneklem yoksa yazılmaz");
        summary.Has("avgResolutionMinutes").ShouldBeFalse();
        summary.GetProperty("byStatus").EnumerateArray().Select(s => s.Str("status")).ToList().ShouldBe(["new", "open", "pending", "resolved", "closed"]);
        summary.GetProperty("byStatus").EnumerateArray().ShouldAllBe(s => s.GetProperty("count").GetInt32() == 0);
        summary.GetProperty("byPriority").EnumerateArray().Select(s => s.Str("priority")).ToList().ShouldBe(["low", "normal", "high", "urgent"]);
        (await admin.GetJsonAsync($"{ReportsPath}/by-assignee?from=2000-01-01&to=2000-01-31")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Averages_AreOmitted_WhenOnlyOneMeasureHasSamples()
    {
        var admin = (await factory.NewOrgAsync("Report Partial")).Admin;
        var c = (await admin.CreateCaseAsync("Yalnız yanıt")).Id();
        await admin.CommentAsync(c, "public", "yanıt");

        var summary = await admin.GetJsonAsync($"{ReportsPath}/summary");

        summary.GetProperty("totalCount").GetInt32().ShouldBe(1);
        summary.Has("avgFirstResponseMinutes").ShouldBeTrue();
        summary.Has("avgResolutionMinutes").ShouldBeFalse("çözülen yok");
        summary.GetProperty("resolvedCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task RangeEdges_FollowTheTenantTimeZone_IncludingAPlusFourteenTenant()
    {
        using var host = new ClockedHost(factory);
        var org = await host.NewOrgAsync("Report Zone", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var admin = org.Admin;
        await admin.PutJsonAsync($"{Base}/organization", new { name = "Report Zone", defaultLocale = "tr", timeZone = "Pacific/Kiritimati" }); // UTC+14

        // UTC 09:00'da yerel saat 23:00 ve gün 1 Ekim; UTC 10:30'da yerel 00:30 ve gün 2 Ekim.
        var beforeMidnight = (await admin.CreateCaseAsync("Gece öncesi")).Id();
        host.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 1, 10, 30, 0, TimeSpan.Zero));
        var afterMidnight = (await admin.CreateCaseAsync("Gece sonrası")).Id();
        host.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

        (await admin.GetJsonAsync($"{ReportsPath}/summary?from=2026-10-01&to=2026-10-01")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync($"{ReportsPath}/summary?from=2026-10-02&to=2026-10-02")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await admin.GetJsonAsync($"{ReportsPath}/summary?from=2026-10-01&to=2026-10-02")).GetProperty("totalCount").GetInt32().ShouldBe(2);
        (await admin.GetJsonAsync($"{ReportsPath}/summary?from=2026-10-03&to=2026-10-03")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        _ = (beforeMidnight, afterMidnight);
    }

    [Fact]
    public async Task DefaultRange_IsTheLastTwelveMonths_AndInvalidRangesAreRejected()
    {
        using var host = new ClockedHost(factory);
        var org = await host.NewOrgAsync("Report Default", new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var admin = org.Admin;
        await admin.CreateCaseAsync("Bu ay");
        host.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

        var summary = await admin.GetJsonAsync($"{ReportsPath}/summary");
        summary.Str("to").ShouldBe("2026-10-20");
        summary.Str("from").ShouldBe("2025-11-01", "11 ay önceki ayın ilk günü");
        summary.GetProperty("totalCount").GetInt32().ShouldBe(1);

        await (await admin.GetAsync($"{ReportsPath}/summary?from=2026-10-10&to=2026-10-01", Ct)).ShouldBeValidationErrorAsync("to");
        await (await admin.GetAsync($"{ReportsPath}/summary?from=2010-01-01&to=2026-10-01", Ct)).ShouldBeValidationErrorAsync("to");
        await (await admin.GetAsync($"{ReportsPath}/by-assignee?from=2026-10-10&to=2026-10-01", Ct)).ShouldBeValidationErrorAsync("to");
        (await admin.GetAsync($"{ReportsPath}/summary?from=bozuk", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reports_CountOnlyTheirOwnTenant()
    {
        var a = await factory.NewOrgAsync("Report Iso A");
        var b = await factory.NewOrgAsync("Report Iso B");
        await a.Admin.CreateCaseAsync("A1");
        await a.Admin.CreateCaseAsync("A2");
        await b.Admin.CreateCaseAsync("B1", new { assignedUserId = b.AdminUserId });

        (await a.Admin.GetJsonAsync($"{ReportsPath}/summary")).GetProperty("totalCount").GetInt32().ShouldBe(2);
        (await b.Admin.GetJsonAsync($"{ReportsPath}/summary")).GetProperty("totalCount").GetInt32().ShouldBe(1);
        var rowsA = (await a.Admin.GetJsonAsync($"{ReportsPath}/by-assignee")).EnumerateArray().ToList();
        rowsA.ShouldHaveSingleItem().Has("assignedUserId").ShouldBeFalse();
        (await b.Admin.GetJsonAsync($"{ReportsPath}/by-assignee")).EnumerateArray().Single().GetProperty("assignedUserId").GetGuid().ShouldBe(b.AdminUserId);
    }
}
