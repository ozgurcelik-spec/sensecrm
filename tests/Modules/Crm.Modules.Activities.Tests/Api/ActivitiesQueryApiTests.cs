using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Shared.Kernel.Time;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Activities.Tests.Api.ActivitiesApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Activities.Tests.Api;

/// <summary>Liste filtreleri/sıralama/sayfalama, kişisel özet (kiracı saat dilimi sınırları) ve aktivite raporu.</summary>
[Collection(ApiCollection.Name)]
public sealed class ActivitiesQueryApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task List_FiltersSortsAndPages()
    {
        var org = await factory.NewOrgAsync("Act List");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.activities.read");
        var now = DateTime.UtcNow;

        var a = (await admin.CreateActivityAsync("task", "Alpha", new { dueAt = now.AddDays(1).Iso(), priority = "low" })).Id();
        var b = (await admin.CreateActivityAsync("task", "Bravo", new { dueAt = now.AddDays(-2).Iso(), priority = "high" })).Id();
        var c = (await admin.CreateActivityAsync("call", "Charlie", new { description = "yearly PLAN meeting" })).Id();
        var d = (await admin.CreateActivityAsync("task", "Delta", new { dueAt = now.AddDays(2).Iso(), status = "completed" })).Id();
        var e = (await admin.CreateActivityAsync("note", "Echo")).Id();
        var m = (await admin.CreateActivityAsync("task", "Üye görevi", new { assignedUserId = memberId, dueAt = now.AddDays(5).Iso() })).Id();

        // Varsayılan sıralama: dueAt artan, boş dueAt olanlar sonda (oluşturulma sırasıyla).
        (await admin.ListIdsAsync()).ShouldBe([b, a, d, m, c, e,], "varsayılan: dueAt artan, boşlar sonda");
        (await admin.ListIdsAsync("?sort=dueAt")).ShouldBe([b, a, d, m, c, e]);
        (await admin.ListIdsAsync("?sort=-dueAt")).ShouldBe([m, d, a, b, c, e], "azalan sıralamada da boşlar sonda");
        (await admin.ListIdsAsync("?sort=priority")).ShouldBe([a, c, d, e, m, b], "low < normal < high (alfabetik değil)");
        (await admin.ListIdsAsync("?sort=-priority")).ShouldBe([b, c, d, e, m, a]);
        (await admin.ListIdsAsync("?sort=subject")).ShouldBe([a, b, c, d, e, m]);
        (await admin.ListIdsAsync("?sort=-subject")).ShouldBe([m, e, d, c, b, a]);
        (await admin.ListIdsAsync("?sort=createdAt")).ShouldBe([a, b, c, d, e, m]);
        (await admin.ListIdsAsync("?sort=-createdAt")).ShouldBe([m, e, d, c, b, a]);
        (await admin.ListIdsAsync("?sort=bilinmeyen")).ShouldBe([b, a, d, m, c, e], "bilinmeyen alan yok sayılır → varsayılan");
        (await admin.ListIdsAsync("?sort=-priority,subject")).ShouldBe([b, c, d, e, m, a]);

        // Filtreler.
        (await admin.ListIdsAsync("?type=call")).ShouldBe([c]);
        (await admin.ListIdsAsync("?type=note")).ShouldBe([e]);
        (await admin.ListIdsAsync("?status=completed")).ShouldBe([d, e]);
        (await admin.ListIdsAsync("?status=open&type=task")).ShouldBe([b, a, m]);
        (await admin.ListIdsAsync($"?assignedUserId={memberId}")).ShouldBe([m]);
        (await admin.ListIdsAsync($"?assignedUserId={org.AdminUserId}&type=task")).ShouldBe([b, a, d]);
        (await admin.ListIdsAsync("?overdue=true")).ShouldBe([b]);
        (await admin.ListIdsAsync("?overdue=false")).ShouldBe([a, d, m, c, e]);
        (await admin.ListIdsAsync($"?dueFrom={Uri.EscapeDataString(now.Iso())}")).ShouldBe([a, d, m]);
        (await admin.ListIdsAsync($"?dueTo={Uri.EscapeDataString(now.Iso())}")).ShouldBe([b]);
        (await admin.ListIdsAsync($"?dueFrom={Uri.EscapeDataString(now.AddDays(1).AddHours(-1).Iso())}&dueTo={Uri.EscapeDataString(now.AddDays(2).AddHours(1).Iso())}")).ShouldBe([a, d]);

        // Arama: konu veya açıklama, büyük/küçük harf duyarsız; joker karakterler düz metindir.
        (await admin.ListIdsAsync("?q=alp")).ShouldBe([a]);
        (await admin.ListIdsAsync("?q=YEARLY%20plan")).ShouldBe([c]);
        (await admin.ListIdsAsync("?q=%25")).ShouldBeEmpty();
        (await admin.ListIdsAsync("?q=a_p")).ShouldBeEmpty();

        // Sayfalama.
        var page2 = await admin.GetJsonAsync($"{ActivitiesPath}?pageSize=4&page=2");
        page2.GetProperty("totalCount").GetInt32().ShouldBe(6);
        (page2.GetProperty("page").GetInt32(), page2.GetProperty("pageSize").GetInt32()).ShouldBe((2, 4));
        page2.GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([c, e]);
        (await admin.ListIdsAsync("?pageSize=2&page=1")).ShouldBe([b, a]);
        (await admin.GetJsonAsync($"{ActivitiesPath}?pageSize=1000")).GetProperty("pageSize").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task Summary_UsesTheTenantTimeZone_ForTodayAndTheMondayWeek()
    {
        var org = await factory.NewOrgAsync("Act Summary");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.activities.read");

        // UTC'den çok farklı bir saat dilimi (UTC+14): "bugün" UTC gününden ayrışır.
        await admin.PutJsonAsync($"{Base}/organization", new { name = "Act Summary", defaultLocale = "tr", timeZone = "Pacific/Kiritimati" });
        var calendar = TenantCalendar.For("Pacific/Kiritimati");
        var today = calendar.Today(DateTimeOffset.UtcNow);
        var todayStart = calendar.StartOfDayUtc(today);
        var tomorrowStart = calendar.StartOfDayUtc(today.AddDays(1));
        var weekStartDate = TenantCalendar.StartOfWeek(today);
        var weekStart = calendar.StartOfDayUtc(weekStartDate);
        var nextWeekStart = calendar.StartOfDayUtc(weekStartDate.AddDays(7));

        // Açık işler: bugün başında, bugün sonunda (vadesi bugün), yarın başında (yarın), dün sonunda (geciken), vadesiz.
        await admin.CreateActivityAsync("task", "Bugün başı", new { dueAt = todayStart.AddSeconds(1).Iso() });
        await admin.CreateActivityAsync("call", "Bugün sonu", new { dueAt = tomorrowStart.AddSeconds(-1).Iso() });
        await admin.CreateActivityAsync("task", "Yarın başı", new { dueAt = tomorrowStart.Iso() });
        await admin.CreateActivityAsync("meeting", "Dün sonu", new { dueAt = todayStart.AddSeconds(-1).Iso() });
        await admin.CreateActivityAsync("task", "Vadesiz");

        // Bu hafta tamamlananlar: şimdi tamamlanan + hafta başı tam sınırda (dahil); hafta başından 1 sn önce ve sonraki hafta başı (hariç).
        var doneNow = await admin.CreateActivityAsync("task", "Şimdi tamamlandı");
        await admin.PostJsonAsync($"{ActivitiesPath}/{doneNow.Id()}/complete", null, HttpStatusCode.NoContent);
        var atWeekStart = (await admin.CreateActivityAsync("task", "Hafta başında", new { status = "completed" })).Id();
        var beforeWeek = (await admin.CreateActivityAsync("task", "Hafta öncesi", new { status = "completed" })).Id();
        var atNextWeek = (await admin.CreateActivityAsync("task", "Gelecek hafta", new { status = "completed" })).Id();
        await factory.ExecuteAsync("UPDATE activities.activities SET completed_at = @at WHERE id = @id", ("at", weekStart), ("id", atWeekStart));
        await factory.ExecuteAsync("UPDATE activities.activities SET completed_at = @at WHERE id = @id", ("at", weekStart.AddSeconds(-1)), ("id", beforeWeek));
        await factory.ExecuteAsync("UPDATE activities.activities SET completed_at = @at WHERE id = @id", ("at", nextWeekStart), ("id", atNextWeek));

        // Notlar ve iptal edilenler sayılmaz.
        await admin.CreateActivityAsync("note", "Bugünkü not");
        await admin.CreateActivityAsync("task", "İptal", new { status = "cancelled", dueAt = todayStart.AddSeconds(2).Iso() });

        // Başkasının işi sayılmaz.
        await admin.CreateActivityAsync("task", "Üyenin işi", new { assignedUserId = memberId, dueAt = todayStart.AddSeconds(3).Iso() });

        var mine = await admin.GetJsonAsync($"{ActivitiesPath}/summary");
        (mine.GetProperty("openCount").GetInt32(), mine.GetProperty("overdueCount").GetInt32(), mine.GetProperty("dueTodayCount").GetInt32(), mine.GetProperty("completedThisWeek").GetInt32())
            .ShouldBe((5, 2, 2, 2));

        var members = await admin.GetJsonAsync($"{ActivitiesPath}/summary?assignedUserId={memberId}");
        (members.GetProperty("openCount").GetInt32(), members.GetProperty("overdueCount").GetInt32(), members.GetProperty("dueTodayCount").GetInt32(), members.GetProperty("completedThisWeek").GetInt32())
            .ShouldBe((1, 1, 1, 0));

        // Aynı veriyle UTC saat diliminde "bugün" farklı bir pencere olur: pencere gerçekten kiracı saat dilimine bağlı.
        await admin.PutJsonAsync($"{Base}/organization", new { name = "Act Summary", defaultLocale = "tr", timeZone = "UTC" });
        var utcSummary = await admin.GetJsonAsync($"{ActivitiesPath}/summary");
        var utcToday = TenantCalendar.Utc.Today(DateTimeOffset.UtcNow);
        var expectedUtcDueToday = new[] { todayStart.AddSeconds(1), tomorrowStart.AddSeconds(-1), tomorrowStart, todayStart.AddSeconds(-1) }
            .Count(due => TenantCalendar.Utc.LocalDate(due) == utcToday);
        utcSummary.GetProperty("dueTodayCount").GetInt32().ShouldBe(expectedUtcDueToday);
        utcSummary.GetProperty("openCount").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task Summary_OfAnEmptyUser_IsAllZeros()
    {
        var org = await factory.NewOrgAsync("Act Summary Empty");

        var summary = await org.Admin.GetJsonAsync($"{ActivitiesPath}/summary");

        (summary.GetProperty("openCount").GetInt32(), summary.GetProperty("overdueCount").GetInt32(), summary.GetProperty("dueTodayCount").GetInt32(), summary.GetProperty("completedThisWeek").GetInt32())
            .ShouldBe((0, 0, 0, 0));
    }

    [Fact]
    public async Task ByUserReport_CountsCompletedOpenAndOverdue_InTheTenantRange_ExcludingNotes()
    {
        var org = await factory.NewOrgAsync("Act Report");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye Mert", "crm.activities.read");

        Task SetCompletedAt(Guid id, DateTime at) => factory.ExecuteAsync("UPDATE activities.activities SET completed_at = @at WHERE id = @id", ("at", at), ("id", id));
        static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

        // Yönetici (Istanbul UTC+3): Mart 2026.
        var t1 = (await admin.CreateActivityAsync("task", "Tamamlanan 1", new { status = "completed" })).Id();
        var t2 = (await admin.CreateActivityAsync("call", "Nisan'a düşen", new { status = "completed" })).Id(); // 31 Mart 21:30Z = 1 Nisan yerel
        var t3 = (await admin.CreateActivityAsync("task", "Mart 1 yerel", new { status = "completed" })).Id(); // 28 Şubat 21:30Z = 1 Mart yerel
        await admin.CreateActivityAsync("task", "Açık geciken", new { dueAt = Utc(2026, 3, 20, 9).Iso() });
        await admin.CreateActivityAsync("meeting", "Uzak gelecek", new { dueAt = Utc(2029, 1, 1, 9).Iso() });
        await admin.CreateActivityAsync("task", "Vadesiz", new { }); // aralıkta yer almaz
        await admin.CreateActivityAsync("task", "İptal", new { status = "cancelled", dueAt = Utc(2026, 3, 21, 9).Iso() });
        var note = (await admin.CreateActivityAsync("note", "Not")).Id();
        await SetCompletedAt(t1, Utc(2026, 3, 10, 9));
        await SetCompletedAt(t2, Utc(2026, 3, 31, 21, 30));
        await SetCompletedAt(t3, Utc(2026, 2, 28, 21, 30));
        await SetCompletedAt(note, Utc(2026, 3, 10, 9));

        // Üye.
        var m1 = (await admin.CreateActivityAsync("task", "Üye 1", new { assignedUserId = memberId, status = "completed" })).Id();
        var m2 = (await admin.CreateActivityAsync("task", "Üye 2", new { assignedUserId = memberId, status = "completed" })).Id();
        await admin.CreateActivityAsync("task", "Üye açık geciken", new { assignedUserId = memberId, dueAt = Utc(2026, 3, 25, 9).Iso() });
        await SetCompletedAt(m1, Utc(2026, 3, 12, 9));
        await SetCompletedAt(m2, Utc(2026, 3, 15, 9));

        var march = await admin.GetJsonAsync($"{Base}/reports/activities/by-user?from=2026-03-01&to=2026-03-31");

        var rows = march.EnumerateArray().ToDictionary(r => r.GetProperty("userId").GetGuid());
        rows.Count.ShouldBe(2);
        (rows[org.AdminUserId].Str("userName"), rows[org.AdminUserId].GetProperty("completedCount").GetInt32(), rows[org.AdminUserId].GetProperty("openCount").GetInt32(), rows[org.AdminUserId].GetProperty("overdueCount").GetInt32())
            .ShouldBe((org.AdminName, 2, 1, 1)); // t1 + t3 (yerel 1 Mart); t2 Nisan'da, not ve iptal sayılmaz
        (rows[memberId].Str("userName"), rows[memberId].GetProperty("completedCount").GetInt32(), rows[memberId].GetProperty("openCount").GetInt32(), rows[memberId].GetProperty("overdueCount").GetInt32())
            .ShouldBe(("Üye Mert", 2, 1, 1));
        march.EnumerateArray().First().GetProperty("userId").GetGuid().ShouldBeOneOf(org.AdminUserId, memberId);

        // Aralık genişleyince: uzak gelecekteki açık iş gecikmez, sayılır; 1 Nisan'a düşen tamamlanan (t2) girer.
        var wide = await admin.GetJsonAsync($"{Base}/reports/activities/by-user?from=2026-03-01&to=2030-01-01");
        var admin2 = wide.EnumerateArray().Single(r => r.GetProperty("userId").GetGuid() == org.AdminUserId);
        (admin2.GetProperty("completedCount").GetInt32(), admin2.GetProperty("openCount").GetInt32(), admin2.GetProperty("overdueCount").GetInt32()).ShouldBe((3, 2, 1));

        // Aralık dışı: satır yok. Ters aralık ve 10 yıldan uzun aralık: validation.
        (await admin.GetJsonAsync($"{Base}/reports/activities/by-user?from=2020-01-01&to=2020-01-31")).GetArrayLength().ShouldBe(0);
        await (await admin.GetAsync($"{Base}/reports/activities/by-user?from=2026-04-01&to=2026-03-01", Ct)).ShouldBeValidationErrorAsync("to");
        await (await admin.GetAsync($"{Base}/reports/activities/by-user?from=2000-01-01&to=2026-03-01", Ct)).ShouldBeValidationErrorAsync("to");
    }

    [Fact]
    public async Task ByUserReport_WithoutRange_UsesTheLastTwelveMonths_AndIsTenantScoped()
    {
        var a = await factory.NewOrgAsync("Act Report Default A");
        var b = await factory.NewOrgAsync("Act Report Default B");
        await a.Admin.CreateActivityAsync("task", "A tamamlandı", new { status = "completed" });
        await b.Admin.CreateActivityAsync("task", "B tamamlandı", new { status = "completed" });
        await b.Admin.CreateActivityAsync("task", "B tamamlandı 2", new { status = "completed" });

        var reportA = await a.Admin.GetJsonAsync($"{Base}/reports/activities/by-user");
        var reportB = await b.Admin.GetJsonAsync($"{Base}/reports/activities/by-user");

        reportA.GetArrayLength().ShouldBe(1);
        (reportA[0].GetProperty("userId").GetGuid(), reportA[0].GetProperty("completedCount").GetInt32()).ShouldBe((a.AdminUserId, 1));
        (reportB[0].GetProperty("userId").GetGuid(), reportB[0].GetProperty("completedCount").GetInt32()).ShouldBe((b.AdminUserId, 2));
    }
}
