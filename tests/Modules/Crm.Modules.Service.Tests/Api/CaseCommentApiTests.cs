using System.Net;
using System.Net.Http.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Service.Tests.Api;

/// <summary>Yorumlar ve ilk yanıt kuralı (herkese açık / dahili, new → open, eşzamanlı ilk yorumlar).</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseCommentApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task AddComment_ReturnsTheDocumentedShape_AndRequiresAnExplicitVisibility()
    {
        var org = await factory.NewOrgAsync("Comment Shape");
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Yorum")).Id();

        var comment = await admin.CommentAsync(id, "internal", "  Dahili not  ");

        comment.Id().ShouldNotBe(Guid.Empty);
        comment.GetProperty("caseId").GetGuid().ShouldBe(id);
        comment.Str("visibility").ShouldBe("internal");
        comment.Str("body").ShouldBe("Dahili not");
        comment.GetProperty("authorUserId").GetGuid().ShouldBe(org.AdminUserId);
        comment.Str("authorName").ShouldBe(org.AdminName);
        comment.Has("createdAt").ShouldBeTrue();

        // Görünürlük varsayılansızdır: eksikse validation.
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { body = "gövde" }, Ct)).ShouldBeValidationErrorAsync("visibility");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public", body = "" }, Ct)).ShouldBeValidationErrorAsync("body");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public", body = "   " }, Ct)).ShouldBeValidationErrorAsync("body");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public", body = new string('x', 8001) }, Ct)).ShouldBeValidationErrorAsync("body");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public" }, Ct)).ShouldBeValidationErrorAsync("body");
        (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "everyone", body = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await (await admin.PostAsJsonAsync($"{CasesPath}/{Guid.NewGuid()}/comments", new { visibility = "public", body = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        (await admin.TimelineAsync(id)).Count(i => i.Str("type") == "comment").ShouldBe(1);
    }

    [Fact]
    public async Task TheFirstPublicComment_WritesFirstResponseAt_OpensANewCase_AndLaterCommentsDoNotChangeIt()
    {
        using var host = new ClockedHost(factory);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var org = await host.NewOrgAsync("Comment First Response", t0);
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("İlk yanıt")).Id();

        // Dahili yorum ilk yanıt sayılmaz ve durumu değiştirmez.
        host.Clock.SetUtcNow(t0.AddMinutes(5));
        await admin.CommentAsync(id, "internal", "Ekip içi not");
        var afterInternal = await admin.GetCaseAsync(id);
        afterInternal.Has("firstResponseAt").ShouldBeFalse();
        afterInternal.Str("status").ShouldBe("new");

        // İlk herkese açık yorum ilk yanıttır; new → open, olayın aktörü yorumu yazan.
        host.Clock.SetUtcNow(t0.AddMinutes(20));
        await admin.CommentAsync(id, "public", "Merhaba, bakıyoruz.");
        var opened = await admin.GetCaseAsync(id);
        opened.Utc("firstResponseAt").ShouldBe(t0.UtcDateTime.AddMinutes(20));
        opened.Str("status").ShouldBe("open");
        var openEvent = (await admin.TimelineAsync(id)).Single(i => i.Str("type") == "statusChanged");
        (openEvent.Str("from"), openEvent.Str("to")).ShouldBe(("new", "open"));
        openEvent.GetProperty("actorUserId").GetGuid().ShouldBe(org.AdminUserId);

        // İkinci herkese açık yorum değiştirmez.
        host.Clock.SetUtcNow(t0.AddMinutes(60));
        await admin.CommentAsync(id, "public", "Güncelleme.");
        (await admin.GetCaseAsync(id)).Utc("firstResponseAt").ShouldBe(t0.UtcDateTime.AddMinutes(20));
        (await admin.TimelineAsync(id)).Count(i => i.Str("type") == "statusChanged").ShouldBe(1);
    }

    [Fact]
    public async Task ResolvedCases_AcceptComments_ClosedCasesDoNot()
    {
        var org = await factory.NewOrgAsync("Comment Resolved");
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Kapanış yorumu")).Id();
        await admin.CommentAsync(id, "public", "Yanıt");
        var firstResponse = (await admin.GetCaseAsync(id)).Str("firstResponseAt");
        await admin.SetStatusAsync(id, "resolved", "Çözüldü");

        await admin.CommentAsync(id, "public", "Çözüm sonrası ek bilgi");
        var resolved = await admin.GetCaseAsync(id);
        (resolved.Str("status"), resolved.Str("firstResponseAt")).ShouldBe(("resolved", firstResponse));

        await admin.SetStatusAsync(id, "closed");
        await (await admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "internal", body = "kapalı" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "case.closed");

        await admin.SetStatusAsync(id, "open");
        await admin.CommentAsync(id, "internal", "yeniden açıldı");
        (await admin.TimelineAsync(id)).Count(i => i.Str("type") == "comment").ShouldBe(3);
    }

    [Fact]
    public async Task PublicComment_OnAPendingCase_KeepsPending()
    {
        var admin = (await factory.NewOrgAsync("Comment Pending")).Admin;
        var id = (await admin.CreateCaseAsync("Beklemede")).Id();
        await admin.SetStatusAsync(id, "pending");

        await admin.CommentAsync(id, "public", "Bilgi bekliyoruz");

        var c = await admin.GetCaseAsync(id);
        (c.Str("status"), c.Has("firstResponseAt")).ShouldBe(("pending", true));
    }

    [Fact]
    public async Task ConcurrentFirstComments_BothSucceed_ThroughTheHandlerRetry_AndFirstResponseIsWrittenOnce()
    {
        var org = await factory.NewOrgAsync("Comment Race");
        var admin = org.Admin;
        var (member, _) = await factory.AddMemberAsync(org, "Yarışçı", "crm.cases.read", "crm.cases.write");

        for (var round = 0; round < 5; round++)
        {
            var id = (await admin.CreateCaseAsync($"Yarış {round}")).Id();

            var responses = await Task.WhenAll(
                admin.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public", body = "A" }, Ct),
                member.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public", body = "B" }, Ct));

            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
            }

            var timeline = await admin.TimelineAsync(id);
            timeline.Count(i => i.Str("type") == "comment").ShouldBe(2, "her yorum tam bir kez kaydolur (yeniden denemede çift yazım yok)");
            timeline.Count(i => i.Str("type") == "statusChanged").ShouldBe(1, "new → open bir kez");
            (await admin.GetCaseAsync(id)).Str("status").ShouldBe("open");
            (await factory.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log_entries WHERE entity_type = 'CaseComment' AND tenant_id = @t", ("t", org.TenantId)))
                .ShouldBe((round + 1) * 2L, "denetim satırları da çoğalmaz");
        }
    }
}
