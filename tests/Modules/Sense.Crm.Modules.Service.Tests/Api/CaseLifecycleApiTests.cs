using System.Net;
using System.Net.Http.Json;
using Sense.Crm.Modules.Service.Contracts;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Service.Tests.Api;

/// <summary>Durum makinesi (5×5 tablo), çözüm notu, yeniden açma, yorumlar ve ilk yanıt kuralı — HTTP üzerinden.</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseLifecycleApiTests(CrmApiFactory factory)
{
    private static readonly string[] Statuses = ["new", "open", "pending", "resolved", "closed"];

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["new"] = ["open", "pending", "resolved", "closed"],
        ["open"] = ["pending", "resolved", "closed"],
        ["pending"] = ["open", "resolved", "closed"],
        ["resolved"] = ["open", "closed"],
        ["closed"] = ["open"],
    };

    public static IEnumerable<object[]> Cells() =>
        Statuses.SelectMany(from => Statuses.Select(to => new object[] { from, to }));

    /// <summary>Talebi API üzerinden istenen duruma getirir (new → hedef; closed önce resolved'dan geçer).</summary>
    private static async Task DriveToAsync(HttpClient client, Guid id, string status)
    {
        switch (status)
        {
            case "new":
                return;
            case "closed":
                await client.SetStatusAsync(id, "resolved", "Çözüldü");
                await client.SetStatusAsync(id, "closed");
                return;
            case "resolved":
                await client.SetStatusAsync(id, "resolved", "Çözüldü");
                return;
            default:
                await client.SetStatusAsync(id, status);
                return;
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Transition_Table_Cell(string from, string to)
    {
        var org = await factory.NewOrgAsync($"Table {from} {to}");
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Tablo")).Id();
        await DriveToAsync(admin, id, from);
        var eventsBefore = (await admin.TimelineAsync(id)).Count;
        var resolvedBefore = (await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", id.ToString())).Count;

        var response = await admin.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = to, resolutionNote = "Not" }, Ct);

        var eventsAfter = (await admin.TimelineAsync(id)).Count;
        var resolvedAfter = (await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", id.ToString())).Count;
        if (from == to)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "aynı duruma geçiş idempotent 204");
            eventsAfter.ShouldBe(eventsBefore, "olay yok");
            resolvedAfter.ShouldBe(resolvedBefore, "CaseResolved yok");
            (await admin.GetCaseAsync(id)).Str("status").ShouldBe(from);
        }
        else if (Allowed[from].Contains(to))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
            (await admin.GetCaseAsync(id)).Str("status").ShouldBe(to);
            eventsAfter.ShouldBe(eventsBefore + 1);
            var status = (await admin.TimelineAsync(id)).First(i => i.Str("type") == "statusChanged");
            (status.Str("from"), status.Str("to")).ShouldBe((from, to));
        }
        else
        {
            await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.invalid_transition");
            (await admin.GetCaseAsync(id)).Str("status").ShouldBe(from);
            eventsAfter.ShouldBe(eventsBefore);
            resolvedAfter.ShouldBe(resolvedBefore);
        }
    }

    [Fact]
    public async Task InvalidTransition_ReportsFromAndToAsArguments_InTheLocalizedMessage()
    {
        var admin = (await factory.NewOrgAsync("Transition Args")).Admin;
        var id = (await admin.CreateCaseAsync("Argümanlar")).Id();
        await admin.SetStatusAsync(id, "open");

        var response = await admin.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "new" }, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.invalid_transition");
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var detail = json.RootElement.GetProperty("detail").GetString()!;
        detail.ShouldContain("open");
        detail.ShouldContain("new");
        json.RootElement.GetProperty("args").GetProperty("from").GetString().ShouldBe("open");
        json.RootElement.GetProperty("args").GetProperty("to").GetString().ShouldBe("new");
    }

    [Theory]
    [InlineData("resolved", null)]
    [InlineData("resolved", "")]
    [InlineData("resolved", "   ")]
    [InlineData("closed", "  ")]
    public async Task Resolving_OrClosingUnresolved_RequiresANote(string target, string? note)
    {
        var admin = (await factory.NewOrgAsync($"Note {target}")).Admin;
        var id = (await admin.CreateCaseAsync("Not zorunlu")).Id();
        await admin.SetStatusAsync(id, "open");

        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = target, resolutionNote = note }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "case.resolution_required");

        (await admin.GetCaseAsync(id)).Str("status").ShouldBe("open");
        (await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", id.ToString())).ShouldBeEmpty();
    }

    [Fact]
    public async Task StatusRequest_Validation_RunsBeforeAuthorization()
    {
        var org = await factory.NewOrgAsync("Status Validation");
        var (reader, _) = await factory.AddMemberAsync(org, "Salt Okuyucu", ServicePermissions.CasesRead);
        var id = (await org.Admin.CreateCaseAsync("Doğrulama")).Id();

        await (await org.Admin.PostAsJsonAsync($"{CasesPath}/{id}/status", new { }, Ct)).ShouldBeValidationErrorAsync("status");
        await (await org.Admin.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "resolved", resolutionNote = new string('x', 4001) }, Ct)).ShouldBeValidationErrorAsync("resolutionNote");
        (await org.Admin.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "bogus" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Geçersiz gövde yetkisiz kullanıcıda da 400; geçerli gövde 403.
        await (await reader.PostAsJsonAsync($"{CasesPath}/{id}/status", new { }, Ct)).ShouldBeValidationErrorAsync("status");
        await (await reader.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "open" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Resolving_WritesResolvedAtNoteAndCountsAsFirstResponse_WhenThereWasNoComment()
    {
        using var host = new ClockedHost(factory);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var org = await host.NewOrgAsync("Resolve Fields", t0);
        var admin = org.Admin;
        var c = await admin.CreateCaseAsync("Yorumsuz çözüm");
        var id = c.Id();
        host.Clock.SetUtcNow(t0.AddMinutes(90));

        await admin.SetStatusAsync(id, "resolved", "  Fatura düzeltildi.  ");

        var resolved = await admin.GetCaseAsync(id);
        resolved.Str("resolutionNote").ShouldBe("Fatura düzeltildi.");
        resolved.Utc("resolvedAt").ShouldBe(t0.UtcDateTime.AddMinutes(90));
        resolved.Utc("firstResponseAt").ShouldBe(t0.UtcDateTime.AddMinutes(90), "çözüm anı ilk yanıt sayılır");
        resolved.Has("closedAt").ShouldBeFalse();
        resolved.Str("slaState").ShouldBe("ok");
        resolved.GetProperty("firstResponseBreached").GetBoolean().ShouldBeFalse();

        var note = (await admin.TimelineAsync(id)).First(i => i.Str("type") == "statusChanged");
        note.Str("note").ShouldBe("Fatura düzeltildi.");

        // resolved → closed: resolvedAt ve not korunur, closedAt yazılır; çözüm notu gerekmez, gönderilen not yok sayılır.
        host.Clock.SetUtcNow(t0.AddMinutes(120));
        await admin.SetStatusAsync(id, "closed", "yok sayılır");
        var closed = await admin.GetCaseAsync(id);
        closed.Utc("resolvedAt").ShouldBe(t0.UtcDateTime.AddMinutes(90));
        closed.Utc("closedAt").ShouldBe(t0.UtcDateTime.AddMinutes(120));
        closed.Str("resolutionNote").ShouldBe("Fatura düzeltildi.");
        (await admin.TimelineAsync(id)).First(i => i.Str("type") == "statusChanged").Has("note").ShouldBeFalse();
    }

    [Fact]
    public async Task ClosingWithoutResolving_WritesBothTimestamps_AndOnePendingResolvedEvent()
    {
        var admin = (await factory.NewOrgAsync("Close Unresolved")).Admin;
        var c = await admin.CreateCaseAsync("Vazgeçildi", new { priority = "high" });
        var id = c.Id();
        await admin.SetStatusAsync(id, "pending");

        await admin.SetStatusAsync(id, "closed", "Müşteri vazgeçti");

        var closed = await admin.GetCaseAsync(id);
        closed.Str("resolutionNote").ShouldBe("Müşteri vazgeçti");
        closed.Has("resolvedAt").ShouldBeTrue();
        closed.Has("closedAt").ShouldBeTrue();
        closed.Utc("resolvedAt").ShouldBe(closed.Utc("closedAt"), TimeSpan.FromSeconds(1));
        (await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", id.ToString())).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Reopen_ResolvedIsUnlimited_ClosedHasAFourteenDayWindow_AndFieldsReset()
    {
        using var host = new ClockedHost(factory);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var org = await host.NewOrgAsync("Reopen Window", t0);
        var admin = org.Admin;

        // resolved → open: süre sınırı yok.
        var resolvedCase = await admin.CreateCaseAsync("Çözülmüş");
        await admin.SetStatusAsync(resolvedCase.Id(), "resolved", "Çözüldü");
        host.Clock.SetUtcNow(t0.AddDays(400));
        await admin.SetStatusAsync(resolvedCase.Id(), "open");
        var reopened = await admin.GetCaseAsync(resolvedCase.Id());
        (reopened.Str("status"), reopened.GetProperty("reopenCount").GetInt32()).ShouldBe(("open", 1));
        reopened.Has("resolvedAt").ShouldBeFalse();
        reopened.Has("closedAt").ShouldBeFalse();
        reopened.Has("resolutionNote").ShouldBeFalse("eski not zaman çizelgesindeki olayda kalır");
        reopened.Utc("dueAt").ShouldBe(t0.UtcDateTime.AddDays(400).AddMinutes(4320), "çözüm hedefi açılış anından yeniden hesaplanır");
        reopened.Utc("firstResponseDueAt").ShouldBe(t0.UtcDateTime.AddMinutes(480), "ilk yanıt hedefi değişmez");
        (await admin.TimelineAsync(resolvedCase.Id())).Any(i => i.Has("note") && i.Str("note") == "Çözüldü").ShouldBeTrue();

        // closed → open: kapanıştan itibaren 14. gün dahil.
        host.Clock.SetUtcNow(t0);
        var onDay14 = (await admin.CreateCaseAsync("Gün 14")).Id();
        await admin.SetStatusAsync(onDay14, "resolved", "ok");
        await admin.SetStatusAsync(onDay14, "closed");
        host.Clock.SetUtcNow(t0.AddDays(14));
        await admin.SetStatusAsync(onDay14, "open");
        (await admin.GetCaseAsync(onDay14)).Str("status").ShouldBe("open");

        // 14 gün + 1 sn → reddedilir; durum ve sayaç değişmez.
        host.Clock.SetUtcNow(t0);
        var late = (await admin.CreateCaseAsync("Gün 14 + 1 sn")).Id();
        await admin.SetStatusAsync(late, "resolved", "ok");
        await admin.SetStatusAsync(late, "closed");
        host.Clock.SetUtcNow(t0.AddDays(14).AddSeconds(1));
        await (await admin.PostAsJsonAsync($"{CasesPath}/{late}/status", new { status = "open" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.reopen_window_expired");
        var stillClosed = await admin.GetCaseAsync(late);
        (stillClosed.Str("status"), stillClosed.GetProperty("reopenCount").GetInt32()).ShouldBe(("closed", 0));

        // Çözülmeden kapatılan talep de aynı pencereyle yeniden açılır ve tekrar çözülünce yeni bir CaseResolved üretir.
        host.Clock.SetUtcNow(t0);
        var again = (await admin.CreateCaseAsync("Tekrar çözülen")).Id();
        await admin.SetStatusAsync(again, "closed", "vazgeçti");
        host.Clock.SetUtcNow(t0.AddDays(1));
        await admin.SetStatusAsync(again, "open");
        await admin.SetStatusAsync(again, "resolved", "yine çözüldü");
        (await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", again.ToString())).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ReopenWindow_IsConfigurable_ThroughSettings()
    {
        using var custom = factory.WithWebHostBuilder(builder => builder.UseSetting("Service:ReopenWindowDays", "2"));
        var client = custom.CreateClient();
        var auth = await client.SignUpAsync("Reopen Config", UniqueEmail("admin"));
        client.WithToken(auth.AccessToken);
        var id = (await client.CreateCaseAsync("Kısa pencere")).Id();
        await client.SetStatusAsync(id, "resolved", "ok");
        await client.SetStatusAsync(id, "closed");

        await factory.ExecuteAsync("UPDATE service.cases SET closed_at = now() - interval '3 days' WHERE id = @id", ("id", id));

        await (await client.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "open" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.reopen_window_expired");
    }

    [Fact]
    public async Task ResolvingPublishesExactlyOneCaseResolved_WithTheRightPayload_AndFailuresPublishNothing()
    {
        using var host = new ClockedHost(factory);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var org = await host.NewOrgAsync("Resolved Event", t0);
        var admin = org.Admin;
        var acme = await admin.CreateAccountAsync("Acme");
        var contact = await admin.CreateContactAsync("Yılmaz", acme.Id());
        var c = await admin.CreateCaseAsync("Olay", new { contactId = contact.Id(), priority = "urgent", assignedUserId = org.AdminUserId });
        var id = c.Id();

        // Hatalı ve aynı-durum geçişleri olay üretmez.
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "resolved" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "case.resolution_required");
        await admin.SetStatusAsync(id, "open");
        await admin.SetStatusAsync(id, "open");
        (await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", id.ToString())).ShouldBeEmpty();

        // Urgent: çözüm hedefi 240 dk; 5 saat 30 sn sonra çözülür → ihlalli.
        host.Clock.SetUtcNow(t0.AddMinutes(300).AddSeconds(30));
        await admin.SetStatusAsync(id, "resolved", "Çözüldü");

        var message = (await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", id.ToString())).ShouldHaveSingleItem();
        message.TenantId.ShouldBe(org.TenantId);
        message.ActorUserId.ShouldBe(org.AdminUserId);
        using var payload = System.Text.Json.JsonDocument.Parse(message.Payload);
        var root = payload.RootElement;
        root.GetProperty("caseId").GetGuid().ShouldBe(id);
        root.GetProperty("caseNumber").GetString().ShouldBe(c.Str("number"));
        root.GetProperty("accountId").GetGuid().ShouldBe(acme.Id());
        root.GetProperty("contactId").GetGuid().ShouldBe(contact.Id());
        root.GetProperty("priority").GetString().ShouldBe("urgent");
        root.GetProperty("assignedUserId").GetGuid().ShouldBe(org.AdminUserId);
        root.GetProperty("resolutionMinutes").GetInt32().ShouldBe(300, "tam dakika, aşağı yuvarlanır");
        root.GetProperty("slaBreached").GetBoolean().ShouldBeTrue();
        root.GetProperty("resolvedAt").GetDateTime().ToUniversalTime().ShouldBe(t0.UtcDateTime.AddMinutes(300).AddSeconds(30));

        // Yeniden aç + tekrar çöz → ikinci bir olay; SLA'ya uygun çözümde slaBreached=false.
        host.Clock.SetUtcNow(t0.AddMinutes(400));
        await admin.SetStatusAsync(id, "open");
        host.Clock.SetUtcNow(t0.AddMinutes(410));
        await admin.SetStatusAsync(id, "resolved", "Tekrar çözüldü");
        var messages = await factory.OutboxMessagesAsync<ServiceDbContext>("Service.CaseResolved", id.ToString());
        messages.Count.ShouldBe(2);
    }
}
