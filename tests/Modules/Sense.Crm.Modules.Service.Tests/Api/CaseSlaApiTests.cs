using System.Net;
using System.Net.Http.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Service.Tests.Api;

/// <summary>SLA hedefleri, uyarı/ihlal sınırları (tam eşik ve bir tik), politikalar ve süzgeç/özet tutarlılığı (sahte saatle).</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseSlaApiTests(CrmApiFactory factory)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(ClockedHost Host, Org Org)> NewAsync(CrmApiFactory factory, string name)
    {
        var host = new ClockedHost(factory);
        var org = await host.NewOrgAsync(name, T0);
        return (host, org);
    }

    private static async Task<string> StateAsync(HttpClient client, Guid id) => (await client.GetCaseAsync(id)).Str("slaState");

    [Theory]
    [InlineData("urgent", 60, 240)]
    [InlineData("high", 240, 1440)]
    [InlineData("normal", 480, 4320)]
    [InlineData("low", 1440, 10080)]
    public async Task Targets_UseTheDefaultPolicy_ForEachPriority(string priority, int firstResponse, int resolution)
    {
        var (host, org) = await NewAsync(factory, $"Sla Default {priority}");
        using var _ = host;

        var c = await org.Admin.CreateCaseAsync("Hedef", new { priority });

        c.Utc("firstResponseDueAt").ShouldBe(T0.UtcDateTime.AddMinutes(firstResponse));
        c.Utc("dueAt").ShouldBe(T0.UtcDateTime.AddMinutes(resolution));
    }

    [Fact]
    public async Task FirstResponse_WarnThreshold_AndDueBoundary_AreExact()
    {
        var (host, org) = await NewAsync(factory, "Sla First Boundaries");
        using var _ = host;
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("İlk yanıt sınırları")).Id(); // normal: ilk yanıt 480 dk, uyarı +384 dk

        host.Clock.SetUtcNow(T0.AddMinutes(384).AddMilliseconds(-1));
        (await StateAsync(admin, id)).ShouldBe("ok", "uyarı eşiğinden bir tik önce");

        host.Clock.SetUtcNow(T0.AddMinutes(384));
        (await StateAsync(admin, id)).ShouldBe("atRisk", "tam eşik");

        host.Clock.SetUtcNow(T0.AddMinutes(480));
        var onDue = await admin.GetCaseAsync(id);
        (onDue.Str("slaState"), onDue.GetProperty("isSlaBreached").GetBoolean()).ShouldBe(("atRisk", false), "hedef anın kendisi ihlal değil");

        host.Clock.SetUtcNow(T0.AddMinutes(480).AddMilliseconds(1));
        var late = await admin.GetCaseAsync(id);
        late.Str("slaState").ShouldBe("breached");
        late.GetProperty("isSlaBreached").GetBoolean().ShouldBeTrue();
        late.GetProperty("firstResponseBreached").GetBoolean().ShouldBeTrue();
        late.GetProperty("resolutionBreached").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Resolution_WarnThreshold_AndDueBoundary_AreExact_OnceTheFirstResponseStopsItsClock()
    {
        var (host, org) = await NewAsync(factory, "Sla Resolution Boundaries");
        using var _ = host;
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Çözüm sınırları")).Id(); // normal: çözüm 4320 dk, uyarı +3456 dk
        host.Clock.SetUtcNow(T0.AddMinutes(1));
        await admin.CommentAsync(id, "public", "İlk yanıt");

        // İlk yanıt gelince ilk yanıt riski durur; çözüm riski sürer.
        host.Clock.SetUtcNow(T0.AddMinutes(400));
        (await StateAsync(admin, id)).ShouldBe("ok", "ilk yanıt uyarı eşiği geçse de yanıt verildi");

        host.Clock.SetUtcNow(T0.AddMinutes(3456).AddMilliseconds(-1));
        (await StateAsync(admin, id)).ShouldBe("ok");
        host.Clock.SetUtcNow(T0.AddMinutes(3456));
        (await StateAsync(admin, id)).ShouldBe("atRisk");

        host.Clock.SetUtcNow(T0.AddMinutes(4320));
        var onDue = await admin.GetCaseAsync(id);
        (onDue.Str("slaState"), onDue.GetProperty("isSlaBreached").GetBoolean()).ShouldBe(("atRisk", false));

        host.Clock.SetUtcNow(T0.AddMinutes(4320).AddMilliseconds(1));
        var late = await admin.GetCaseAsync(id);
        (late.Str("slaState"), late.GetProperty("resolutionBreached").GetBoolean(), late.GetProperty("firstResponseBreached").GetBoolean()).ShouldBe(("breached", true, false));
    }

    [Fact]
    public async Task AFirstResponseGivenAfterTheTarget_StaysABreach_EvenAfterResolving()
    {
        var (host, org) = await NewAsync(factory, "Sla Late First Response");
        using var _ = host;
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Geç yanıt")).Id();
        host.Clock.SetUtcNow(T0.AddMinutes(481));

        await admin.CommentAsync(id, "public", "Geç yanıt");
        var late = await admin.GetCaseAsync(id);
        (late.Str("slaState"), late.GetProperty("firstResponseBreached").GetBoolean()).ShouldBe(("breached", true));

        await admin.SetStatusAsync(id, "resolved", "Çözüldü");
        var resolved = await admin.GetCaseAsync(id);
        (resolved.Str("slaState"), resolved.GetProperty("isSlaBreached").GetBoolean()).ShouldBe(("breached", true), "ihlal kalıcıdır");

        host.Clock.SetUtcNow(T0.AddDays(90));
        (await StateAsync(admin, id)).ShouldBe("breached");
    }

    [Fact]
    public async Task ResolvedInTime_IsOkForever_WhileLateResolvedIsBreached()
    {
        var (host, org) = await NewAsync(factory, "Sla Resolved");
        using var _ = host;
        var admin = org.Admin;
        var inTime = (await admin.CreateCaseAsync("Zamanında")).Id();
        var late = (await admin.CreateCaseAsync("Geç çözüm")).Id();

        host.Clock.SetUtcNow(T0.AddMinutes(60));
        await admin.SetStatusAsync(inTime, "resolved", "Hızlı");
        host.Clock.SetUtcNow(T0.AddMinutes(4321));
        await admin.SetStatusAsync(late, "resolved", "Yavaş");

        host.Clock.SetUtcNow(T0.AddDays(365));
        var okCase = await admin.GetCaseAsync(inTime);
        (okCase.Str("slaState"), okCase.GetProperty("isSlaBreached").GetBoolean()).ShouldBe(("ok", false));
        var lateCase = await admin.GetCaseAsync(late);
        (lateCase.Str("slaState"), lateCase.GetProperty("resolutionBreached").GetBoolean()).ShouldBe(("breached", true));

        // Kapatma da aynısını korur.
        await admin.SetStatusAsync(inTime, "closed");
        (await StateAsync(admin, inTime)).ShouldBe("ok");
    }

    [Fact]
    public async Task ChangingThePriority_RecalculatesFromTheOriginalStart_UpMayBreach_DownExtends()
    {
        var (host, org) = await NewAsync(factory, "Sla Priority");
        using var _ = host;
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Öncelik")).Id();
        host.Clock.SetUtcNow(T0.AddMinutes(100));

        // Yükseltme: hedef geçmişe düşebilir → ihlal (urgent ilk yanıt 60 dk, T0'dan hesaplanır).
        await admin.PostJsonAsync($"{CasesPath}/{id}/priority", new { priority = "urgent" }, HttpStatusCode.NoContent);
        var up = await admin.GetCaseAsync(id);
        up.Utc("firstResponseDueAt").ShouldBe(T0.UtcDateTime.AddMinutes(60));
        up.Utc("dueAt").ShouldBe(T0.UtcDateTime.AddMinutes(240));
        up.Str("slaState").ShouldBe("breached");

        // Düşürme uzatır (low ilk yanıt 1440, çözüm 10080).
        await admin.PostJsonAsync($"{CasesPath}/{id}/priority", new { priority = "low" }, HttpStatusCode.NoContent);
        var down = await admin.GetCaseAsync(id);
        down.Utc("firstResponseDueAt").ShouldBe(T0.UtcDateTime.AddMinutes(1440));
        down.Utc("dueAt").ShouldBe(T0.UtcDateTime.AddMinutes(10080));
        down.Str("slaState").ShouldBe("ok");

        // İlk yanıt geldikten sonra ilk yanıt hedefi değişmez; çözüm hedefi değişir.
        await admin.CommentAsync(id, "public", "yanıt");
        await admin.PostJsonAsync($"{CasesPath}/{id}/priority", new { priority = "high" }, HttpStatusCode.NoContent);
        var after = await admin.GetCaseAsync(id);
        after.Utc("firstResponseDueAt").ShouldBe(T0.UtcDateTime.AddMinutes(1440));
        after.Utc("dueAt").ShouldBe(T0.UtcDateTime.AddMinutes(1440));

        var events = (await admin.TimelineAsync(id)).Where(i => i.Str("type") == "priorityChanged").Select(i => (i.Str("from"), i.Str("to"))).ToList();
        events.ShouldBe([("low", "high"), ("urgent", "low"), ("normal", "urgent")]);
    }

    [Fact]
    public async Task ReopeningRestartsTheResolutionClock_FromTheReopenMoment()
    {
        var (host, org) = await NewAsync(factory, "Sla Reopen");
        using var _ = host;
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Yeniden açma", new { priority = "high" })).Id();
        host.Clock.SetUtcNow(T0.AddMinutes(30));
        await admin.SetStatusAsync(id, "resolved", "Çözüldü");
        host.Clock.SetUtcNow(T0.AddDays(2));

        await admin.SetStatusAsync(id, "open");

        var reopened = await admin.GetCaseAsync(id);
        reopened.Utc("dueAt").ShouldBe(T0.UtcDateTime.AddDays(2).AddMinutes(1440), "yüksek öncelik çözüm süresi, açılış anından");
        reopened.Utc("firstResponseDueAt").ShouldBe(T0.UtcDateTime.AddMinutes(240));
        reopened.Str("slaState").ShouldBe("ok", "ilk yanıt (çözüm anı) hedef içinde verilmişti; yeni çözüm süresi yeni başladı");

        // Warn eşiği açılış anından ölçülür: high 1440 dk → 1152 dk.
        host.Clock.SetUtcNow(T0.AddDays(2).AddMinutes(1152));
        (await StateAsync(admin, id)).ShouldBe("atRisk");
        host.Clock.SetUtcNow(T0.AddDays(2).AddMinutes(1440).AddMilliseconds(1));
        (await StateAsync(admin, id)).ShouldBe("breached");
    }

    [Fact]
    public async Task PolicyChanges_DoNotChangeExistingCases_OnlyNewOnes()
    {
        var (host, org) = await NewAsync(factory, "Sla Policy Snapshot");
        using var _ = host;
        var admin = org.Admin;
        var existing = await admin.CreateCaseAsync("Eski");

        await admin.PutJsonAsync(SlaPath, new
        {
            policies = new object[]
            {
                new { priority = "low", firstResponseMinutes = 100, resolutionMinutes = 200 },
                new { priority = "normal", firstResponseMinutes = 10, resolutionMinutes = 20 },
                new { priority = "high", firstResponseMinutes = 30, resolutionMinutes = 60 },
                new { priority = "urgent", firstResponseMinutes = 5, resolutionMinutes = 15 },
            },
        });

        var after = await admin.GetCaseAsync(existing.Id());
        after.Utc("firstResponseDueAt").ShouldBe(existing.Utc("firstResponseDueAt"), "anlık görüntü");
        after.Utc("dueAt").ShouldBe(existing.Utc("dueAt"));

        var fresh = await admin.CreateCaseAsync("Yeni");
        fresh.Utc("firstResponseDueAt").ShouldBe(T0.UtcDateTime.AddMinutes(10));
        fresh.Utc("dueAt").ShouldBe(T0.UtcDateTime.AddMinutes(20));

        // Yeniden hesaplama (öncelik değişimi) güncel politikayı kullanır.
        await admin.PostJsonAsync($"{CasesPath}/{existing.Id()}/priority", new { priority = "urgent" }, HttpStatusCode.NoContent);
        (await admin.GetCaseAsync(existing.Id())).Utc("dueAt").ShouldBe(T0.UtcDateTime.AddMinutes(15));
    }

    [Fact]
    public async Task SlaStateFilter_SummaryAndResponses_AgreeOnTheSameSets()
    {
        var (host, org) = await NewAsync(factory, "Sla Parity");
        using var _ = host;
        var admin = org.Admin;

        // Farklı yaşta/öncelikte/durumda talepler oluştur.
        var ids = new List<Guid>();
        foreach (var (priority, offset) in new[] { ("urgent", 0), ("high", 10), ("normal", 20), ("low", 30), ("normal", 40), ("urgent", 50) })
        {
            host.Clock.SetUtcNow(T0.AddMinutes(offset));
            ids.Add((await admin.CreateCaseAsync($"Talep {priority} {offset}", new { priority })).Id());
        }

        host.Clock.SetUtcNow(T0.AddMinutes(60));
        await admin.CommentAsync(ids[1], "public", "yanıt");
        await admin.SetStatusAsync(ids[2], "resolved", "çözüldü");
        await admin.SetStatusAsync(ids[3], "closed", "vazgeçildi");

        // Birkaç anda karşılaştır: her an için liste süzgeci, yanıttaki slaState ile aynı kümeyi vermeli.
        foreach (var minutes in new[] { 61, 100, 300, 500, 1500, 5000, 20000 })
        {
            host.Clock.SetUtcNow(T0.AddMinutes(minutes));
            var all = (await admin.GetJsonAsync($"{CasesPath}?pageSize=100")).GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToList();

            foreach (var state in new[] { "ok", "atRisk", "breached" })
            {
                var expected = all.Where(i => i.Str("slaState") == state).Select(i => i.Id()).Order().ToList();
                var filtered = (await admin.ListIdsAsync($"?slaState={state}&pageSize=100")).Order().ToList();
                filtered.ShouldBe(expected, $"t+{minutes} dk, slaState={state}");
            }

            all.ShouldAllBe(i => i.GetProperty("isSlaBreached").GetBoolean() == (i.Str("slaState") == "breached"));

            var summary = await admin.GetJsonAsync($"{CasesPath}/summary");
            var overdueExpected = all.Count(i => i.Str("slaState") == "breached" && i.Str("status") is "new" or "open" or "pending");
            summary.GetProperty("overdueCount").GetInt32().ShouldBe(overdueExpected, $"t+{minutes}");
            (await admin.ListIdsAsync("?status=new,open,pending&slaState=breached&pageSize=100")).Count.ShouldBe(overdueExpected);
        }
    }

    [Fact]
    public async Task SlaPolicyEndpoints_AlwaysReturnFourRowsInOrder_AndPutIsValidated()
    {
        var org = await factory.NewOrgAsync("Sla Policies");
        var admin = org.Admin;

        var initial = await admin.GetJsonAsync(SlaPath);
        initial.EnumerateArray().Select(p => (p.Str("priority"), p.GetProperty("firstResponseMinutes").GetInt32(), p.GetProperty("resolutionMinutes").GetInt32()))
            .ToList().ShouldBe([("low", 1440, 10080), ("normal", 480, 4320), ("high", 240, 1440), ("urgent", 60, 240)]);

        await admin.PutJsonAsync(SlaPath, new
        {
            policies = new object[]
            {
                new { priority = "urgent", firstResponseMinutes = 30, resolutionMinutes = 120 },
                new { priority = "high", firstResponseMinutes = 120, resolutionMinutes = 720 },
                new { priority = "normal", firstResponseMinutes = 240, resolutionMinutes = 2880 },
                new { priority = "low", firstResponseMinutes = 720, resolutionMinutes = 5040 },
            },
        });
        (await admin.GetJsonAsync(SlaPath)).EnumerateArray().Select(p => p.Str("priority")).ToList().ShouldBe(["low", "normal", "high", "urgent"]);
        (await admin.GetJsonAsync(SlaPath))[3].GetProperty("firstResponseMinutes").GetInt32().ShouldBe(30);

        object Row(string priority, int first, int resolution) => new { priority, firstResponseMinutes = first, resolutionMinutes = resolution };
        object[] Valid() => [Row("low", 10, 20), Row("normal", 10, 20), Row("high", 10, 20), Row("urgent", 10, 20)];

        // Eksik / yinelenen öncelik, boş dizi, null.
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = new object[] { Row("low", 1, 2), Row("normal", 1, 2), Row("high", 1, 2) } }, Ct)).ShouldBeValidationErrorAsync("policies");
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = new object[] { Row("low", 1, 2), Row("low", 1, 2), Row("high", 1, 2), Row("urgent", 1, 2) } }, Ct)).ShouldBeValidationErrorAsync("policies");
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = Array.Empty<object>() }, Ct)).ShouldBeValidationErrorAsync("policies");
        await (await admin.PutAsJsonAsync(SlaPath, new { }, Ct)).ShouldBeValidationErrorAsync("policies");
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = new object[] { Row("low", 1, 2), Row("normal", 1, 2), Row("high", 1, 2), Row("urgent", 1, 2), Row("urgent", 1, 2) } }, Ct)).ShouldBeValidationErrorAsync("policies");

        // Sayı kuralları alan yollarıyla.
        var zero = Valid(); zero[2] = Row("high", 0, 20);
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = zero }, Ct)).ShouldBeValidationErrorAsync("policies[2].firstResponseMinutes");
        var inverted = Valid(); inverted[1] = Row("normal", 30, 20);
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = inverted }, Ct)).ShouldBeValidationErrorAsync("policies[1].firstResponseMinutes");
        var tooBig = Valid(); tooBig[3] = Row("urgent", 10, 525_601);
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = tooBig }, Ct)).ShouldBeValidationErrorAsync("policies[3].resolutionMinutes");
        var zeroResolution = Valid(); zeroResolution[0] = Row("low", 0, 0);
        await (await admin.PutAsJsonAsync(SlaPath, new { policies = zeroResolution }, Ct)).ShouldBeValidationErrorAsync("policies[0].resolutionMinutes");

        // Sınırlar geçerli: 1 ≤ ilk ≤ çözüm ≤ 525600.
        var edges = new object[] { Row("low", 1, 1), Row("normal", 525_600, 525_600), Row("high", 5, 10), Row("urgent", 10, 10) };
        await admin.PutJsonAsync(SlaPath, new { policies = edges });
        (await admin.GetJsonAsync(SlaPath))[1].GetProperty("resolutionMinutes").GetInt32().ShouldBe(525_600);
    }
}
