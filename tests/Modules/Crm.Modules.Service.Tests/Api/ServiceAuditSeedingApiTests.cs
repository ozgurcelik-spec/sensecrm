using System.Net;
using System.Net.Http.Json;
using Crm.Modules.Identity.Infrastructure.Persistence;
using Crm.Modules.Service.Application;
using Crm.Modules.Service.Domain.Cases;
using Crm.Modules.Service.Infrastructure.Persistence;
using Crm.Modules.Service.Infrastructure.Provisioning;
using Crm.Tests.Shared.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using static Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Service.Tests.Api;

/// <summary>Denetim kaydı (Case/CaseComment/SlaPolicy, maskeleme, kayıt bazlı okuma izinleri) ve SLA politikası tohumlama yolları.</summary>
[Collection(ApiCollection.Name)]
public sealed class ServiceAuditSeedingApiTests(CrmApiFactory factory)
{
    private async Task<List<System.Text.Json.JsonElement>> AuditAsync(HttpClient client, string entityType, Guid id)
    {
        var page = await client.GetJsonAsync($"{Base}/audit?entityType={entityType}&entityId={id}&pageSize=100");
        return page.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToList();
    }

    [Fact]
    public async Task CaseChanges_AreAudited_WithCamelCaseFields_AndEnumStrings()
    {
        var org = await factory.NewOrgAsync("Audit Case");
        var admin = org.Admin;
        var c = await admin.CreateCaseAsync("Denetimli talep", new { priority = "high", channel = "email" });
        var id = c.Id();
        await admin.PostJsonAsync($"{CasesPath}/{id}/priority", new { priority = "urgent" }, HttpStatusCode.NoContent);
        await admin.PostJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = org.AdminUserId }, HttpStatusCode.NoContent);
        await admin.SetStatusAsync(id, "resolved", "Çözüldü");
        await admin.DeleteJsonAsync($"{CasesPath}/{id}");

        var items = await AuditAsync(admin, "Case", id);

        items.Select(i => i.Str("action")).ToList().ShouldBe(["deleted", "updated", "updated", "updated", "created"], "en yeni önce");
        items.ShouldAllBe(i => i.Str("entityType") == "Case" && i.Str("entityId") == id.ToString());

        var created = items[4].GetProperty("changes");
        created.GetProperty("number").GetProperty("new").GetString().ShouldBe(c.Str("number"));
        created.GetProperty("subject").GetProperty("new").GetString().ShouldBe("Denetimli talep");
        created.GetProperty("status").GetProperty("new").GetString().ShouldBe("new");
        created.GetProperty("priority").GetProperty("new").GetString().ShouldBe("high");
        created.GetProperty("channel").GetProperty("new").GetString().ShouldBe("email");
        created.TryGetProperty("firstResponseDueAt", out _).ShouldBeTrue("türetilmiş SLA alanları denetim farkında görünür");

        var priority = items[3].GetProperty("changes");
        (priority.GetProperty("priority").GetProperty("old").GetString(), priority.GetProperty("priority").GetProperty("new").GetString()).ShouldBe(("high", "urgent"));
        priority.TryGetProperty("dueAt", out _).ShouldBeTrue();

        items[2].GetProperty("changes").GetProperty("assignedUserId").GetProperty("new").GetGuid().ShouldBe(org.AdminUserId);
        var resolved = items[1].GetProperty("changes");
        (resolved.GetProperty("status").GetProperty("new").GetString(), resolved.GetProperty("resolutionNote").GetProperty("new").GetString()).ShouldBe(("resolved", "Çözüldü"));
        items[0].GetProperty("changes").TryGetProperty("subject", out _).ShouldBeTrue();
        items.ShouldAllBe(i => i.Str("userDisplayName") == org.AdminName);
    }

    [Fact]
    public async Task CommentBodies_AreMasked_InTheAuditTrail_AndCaseEventsAreNotAudited()
    {
        var org = await factory.NewOrgAsync("Audit Comment");
        var admin = org.Admin;
        var id = (await admin.CreateCaseAsync("Yorum denetimi")).Id();
        var comment = await admin.CommentAsync(id, "public", "TC kimlik 12345678901 gizli");

        var items = await AuditAsync(admin, "CaseComment", comment.Id());

        var created = items.ShouldHaveSingleItem();
        created.Str("action").ShouldBe("created");
        var changes = created.GetProperty("changes");
        changes.GetProperty("body").GetProperty("new").GetString().ShouldBe("***", "yorum metni maskelenir");
        changes.GetProperty("visibility").GetProperty("new").GetString().ShouldBe("public");
        changes.GetProperty("caseId").GetProperty("new").GetGuid().ShouldBe(id);
        var orgAudit = await admin.GetAsync($"{Base}/organization/audit?pageSize=100", Ct);
        (await orgAudit.Content.ReadAsStringAsync(Ct)).ShouldNotContain("12345678901");

        // CaseEvent ve CaseCounter bilinçli olarak denetim dışıdır (Lead onaylı istisna).
        (await factory.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_type IN ('CaseEvent', 'CaseCounter')", ("t", org.TenantId))).ShouldBe(0);
    }

    [Fact]
    public async Task PerRecordAudit_RequiresCasesRead_ButSlaPolicyOnlyOrgAuditRead()
    {
        var org = await factory.NewOrgAsync("Audit Permissions");
        var id = (await org.Admin.CreateCaseAsync("İzinli denetim")).Id();
        var comment = await org.Admin.CommentAsync(id, "public", "yorum");
        var (reader, _) = await factory.AddMemberAsync(org, "Talep Okuyucu", "crm.cases.read");
        var (auditor, _) = await factory.AddMemberAsync(org, "Denetçi", "org.audit.read");
        var (none, _) = await factory.AddMemberAsync(org, "Yetkisiz", "crm.accounts.read");

        // Case ve CaseComment: crm.cases.read ile (org.audit.read olmadan) okunur.
        (await AuditAsync(reader, "Case", id)).ShouldNotBeEmpty();
        (await AuditAsync(reader, "CaseComment", comment.Id())).ShouldNotBeEmpty();
        (await AuditAsync(auditor, "Case", id)).ShouldNotBeEmpty("org.audit.read her türü açar");
        await (await none.GetAsync($"{Base}/audit?entityType=Case&entityId={id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // SlaPolicy: yalnız org.audit.read.
        await org.Admin.GetJsonAsync(SlaPath);
        var policyId = await factory.ScalarAsync<Guid>("SELECT id FROM service.sla_policies WHERE tenant_id = @t AND priority = 'Normal'", ("t", org.TenantId));
        await org.Admin.PutJsonAsync(SlaPath, new
        {
            policies = new object[]
            {
                new { priority = "low", firstResponseMinutes = 100, resolutionMinutes = 200 },
                new { priority = "normal", firstResponseMinutes = 50, resolutionMinutes = 100 },
                new { priority = "high", firstResponseMinutes = 20, resolutionMinutes = 40 },
                new { priority = "urgent", firstResponseMinutes = 5, resolutionMinutes = 10 },
            },
        });
        var policyAudit = await AuditAsync(auditor, "SlaPolicy", policyId);
        policyAudit.Select(i => i.Str("action")).ToList().ShouldBe(["updated", "created"]);
        var change = policyAudit[0].GetProperty("changes");
        (change.GetProperty("firstResponseMinutes").GetProperty("old").GetInt32(), change.GetProperty("firstResponseMinutes").GetProperty("new").GetInt32()).ShouldBe((480, 50));
        await (await reader.GetAsync($"{Base}/audit?entityType=SlaPolicy&entityId={policyId}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task OrganizationCreated_OutboxEvent_SeedsFourPolicies_ThroughTheWorkerPath_Idempotently()
    {
        var org = await factory.NewOrgAsync("Seed Worker");

        // Kayıt işlemi OrganizationCreated olayını Identity outbox'una yazdı; Worker'ın yaptığı iş: outbox'ı boşalt → Service tüketicisi tohumlar.
        (await factory.OutboxMessagesAsync<IdentityDbContext>("Identity.OrganizationCreated", org.TenantId.ToString())).ShouldHaveSingleItem();
        await factory.DrainOutboxAsync<IdentityDbContext>();

        var policies = await PoliciesOfAsync(org.TenantId);
        policies.Count.ShouldBe(4);
        policies.Select(p => (p.Priority, p.FirstResponseMinutes, p.ResolutionMinutes)).OrderBy(p => p.Priority).ToList().ShouldBe(
            [(CasePriority.Low, 1440, 10080), (CasePriority.Normal, 480, 4320), (CasePriority.High, 240, 1440), (CasePriority.Urgent, 60, 240)]);

        // Olay yeniden işlense/çağrı tekrarlansa da 4 satırda kalır.
        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<IDefaultSlaPolicySeeder>().EnsureAsync(org.TenantId, Ct)).ShouldBeFalse();
        }

        (await PoliciesOfAsync(org.TenantId)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task ApiStartup_SeedsExistingOrganizations_Idempotently()
    {
        var org = await factory.NewOrgAsync("Seed Startup");
        await factory.DrainOutboxAsync<IdentityDbContext>();
        (await PoliciesOfAsync(org.TenantId)).Count.ShouldBe(4);

        // M1–M6A'da açılmış organizasyonu taklit et: politikaları (fiziksel olarak) sil.
        await factory.ExecuteAsync("DELETE FROM service.sla_policies WHERE tenant_id = @t", ("t", org.TenantId));
        (await PoliciesOfAsync(org.TenantId)).ShouldBeEmpty();

        var startup = factory.Services.GetServices<IHostedService>().OfType<DefaultSlaPolicySyncHostedService>().Single();
        await startup.StartAsync(Ct);
        await startup.StartAsync(Ct);

        (await PoliciesOfAsync(org.TenantId)).Count.ShouldBe(4);
        (await org.Admin.GetJsonAsync(SlaPath)).GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public async Task FirstCase_WorksBeforeTheWorkerProcessedTheEvent_ThroughTheLazyPath()
    {
        // Kayıttan hemen sonra (outbox henüz boşaltılmadan) talep açmak çalışır: politika tembel tohumlanır.
        var org = await factory.NewOrgAsync("Seed Lazy");
        (await factory.OutboxMessagesAsync<IdentityDbContext>("Identity.OrganizationCreated", org.TenantId.ToString())).Single().ProcessedAt.ShouldBeNull();
        await factory.ExecuteAsync("DELETE FROM service.sla_policies WHERE tenant_id = @t", ("t", org.TenantId));

        var created = await org.Admin.CreateCaseAsync("İlk talep", new { priority = "urgent" });

        created.Utc("dueAt").ShouldBe(created.Utc("createdAt").AddMinutes(240), TimeSpan.FromSeconds(1));
        (await PoliciesOfAsync(org.TenantId)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task ConcurrentLazySeeding_StaysAtFourRows_AndAMissingSingleRowIsCompleted()
    {
        var org = await factory.NewOrgAsync("Seed Concurrent");
        await factory.ExecuteAsync("DELETE FROM service.sla_policies WHERE tenant_id = @t", ("t", org.TenantId));

        // Eşzamanlı listeleme çağrıları (tembel yol) advisory lock ile tek seferde 4 satır yazar.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => org.Admin.GetAsync(SlaPath, Ct)));
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await PoliciesOfAsync(org.TenantId)).Count.ShouldBe(4);

        // Eksik tek satır tamamlanır; mevcut satırlar değişmez.
        await factory.ExecuteAsync("DELETE FROM service.sla_policies WHERE tenant_id = @t AND priority = 'High'", ("t", org.TenantId));
        await factory.ExecuteAsync("UPDATE service.sla_policies SET first_response_minutes = 7 WHERE tenant_id = @t AND priority = 'Low'", ("t", org.TenantId));
        using (var scope = factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IDefaultSlaPolicySeeder>().EnsureAsync(org.TenantId, Ct)).ShouldBeTrue();
        }

        var policies = await PoliciesOfAsync(org.TenantId);
        policies.Count.ShouldBe(4);
        policies.Single(p => p.Priority == CasePriority.High).FirstResponseMinutes.ShouldBe(240);
        policies.Single(p => p.Priority == CasePriority.Low).FirstResponseMinutes.ShouldBe(7);
    }

    private async Task<List<Crm.Modules.Service.Domain.Sla.SlaPolicy>> PoliciesOfAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        return await db.SlaPolicies.IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == tenantId).ToListAsync(Ct);
    }
}
