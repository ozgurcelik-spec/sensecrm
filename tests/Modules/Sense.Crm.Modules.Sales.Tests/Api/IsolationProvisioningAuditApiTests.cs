using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Modules.Sales.Infrastructure.Provisioning;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Sales.Tests.Api.SalesApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Sales.Tests.Api;

/// <summary>Kiracılar arası izolasyon, varsayılan huni tohumlama (yeni + mevcut organizasyon) ve denetim kaydı.</summary>
[Collection(ApiCollection.Name)]
public sealed class IsolationProvisioningAuditApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task CrossTenantIsolation_AdminOfA_CannotSeeOrModify_SalesRecordsOfB()
    {
        var a = await factory.NewOrgAsync("Isolation A");
        var b = await factory.NewOrgAsync("Isolation B");
        var adminA = a.Admin;
        var adminB = b.Admin;

        // B'nin kayıtları.
        var accountB = await adminB.CreateAccountAsync("B Firması");
        var contactB = await adminB.PostJsonAsync($"{Base}/contacts", new { lastName = "B Kişisi", accountId = accountB.Id() });
        var leadB = await adminB.CreateLeadAsync("B Lead", "B Şirketi");
        var leadB2 = await adminB.CreateLeadAsync("B Lead2", "B Şirketi 2");
        var pipelineB = await adminB.DefaultPipelineAsync();
        var dealB = await adminB.PostJsonAsync($"{Base}/deals", new { name = "B Fırsatı", accountId = accountB.Id(), amount = 999 });
        var extraPipelineB = await adminB.PostJsonAsync($"{Base}/pipelines", new { name = "B Özel Huni" });
        var stageB = pipelineB.StageId("Teklif");

        // A'nın kendi kaydı (çapraz referans denemeleri için).
        var accountA = await adminA.CreateAccountAsync("A Firması");
        var dealA = await adminA.PostJsonAsync($"{Base}/deals", new { name = "A Fırsatı", accountId = accountA.Id() });
        var leadA = await adminA.CreateLeadAsync("A Lead", "A Şirketi");

        // Okuma: bulunamaz (varlık sızdırılmaz), listelerde yok.
        foreach (var url in new[]
        {
            $"accounts/{accountB.Id()}", $"contacts/{contactB.Id()}", $"leads/{leadB.Id()}", $"deals/{dealB.Id()}", $"pipelines/{pipelineB.Id()}",
            $"accounts/{accountB.Id()}/contacts", $"accounts/{accountB.Id()}/deals",
        })
        {
            await (await adminA.GetAsync($"{Base}/{url}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        (await adminA.GetJsonAsync($"{Base}/accounts")).GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([accountA.Id()]);
        (await adminA.GetJsonAsync($"{Base}/contacts")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await adminA.GetJsonAsync($"{Base}/leads")).GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([leadA.Id()]);
        (await adminA.GetJsonAsync($"{Base}/deals")).GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([dealA.Id()]);
        var pipelinesA = (await adminA.GetJsonAsync($"{Base}/pipelines")).EnumerateArray().Select(p => p.Id()).ToList();
        pipelinesA.ShouldNotContain(pipelineB.Id());
        pipelinesA.ShouldNotContain(extraPipelineB.Id());
        var boardA = await adminA.GetJsonAsync($"{Base}/deals/board");
        boardA.GetProperty("stages").EnumerateArray().Sum(s => s.GetProperty("count").GetInt32()).ShouldBe(1);
        await (await adminA.GetAsync($"{Base}/deals/board?pipelineId={pipelineB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await adminA.GetJsonAsync($"{Base}/accounts?ownerUserId={b.AdminUserId}")).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // Değiştirme / silme: bulunamaz.
        await (await adminA.PutAsJsonAsync($"{Base}/accounts/{accountB.Id()}", new { name = "Ele geçirildi" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.DeleteAsync($"{Base}/accounts/{accountB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PutAsJsonAsync($"{Base}/contacts/{contactB.Id()}", new { lastName = "Ele geçirildi" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.DeleteAsync($"{Base}/contacts/{contactB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PutAsJsonAsync($"{Base}/leads/{leadB.Id()}", new { lastName = "Ele geçirildi", company = "X" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.DeleteAsync($"{Base}/leads/{leadB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/leads/{leadB.Id()}/convert", new { createDeal = false }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PutAsJsonAsync($"{Base}/deals/{dealB.Id()}", new { name = "Ele geçirildi", accountId = accountA.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.DeleteAsync($"{Base}/deals/{dealB.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/deals/{dealB.Id()}/stage", new { stageId = stageB }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PutAsJsonAsync($"{Base}/pipelines/{pipelineB.Id()}", new { name = "Ele geçirildi", isDefault = true }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        var stagesOfB = pipelineB.GetProperty("stages").EnumerateArray().Select(s => new { id = s.Id(), name = "Ele geçirildi", probability = s.GetProperty("probability").GetInt32(), kind = s.GetProperty("kind").GetString() }).ToList();
        await (await adminA.PutAsJsonAsync($"{Base}/pipelines/{pipelineB.Id()}/stages", new { stages = stagesOfB }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Çapraz referanslar: A, B'nin kimliklerini kendi kayıtlarına bağlayamaz.
        await (await adminA.PostAsJsonAsync($"{Base}/contacts", new { lastName = "X", accountId = accountB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = accountB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = accountA.Id(), contactId = contactB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = accountA.Id(), pipelineId = pipelineB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/deals", new { name = "X", accountId = accountA.Id(), stageId = stageB }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "pipeline.stage_not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/deals/{dealA.Id()}/stage", new { stageId = stageB }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "pipeline.stage_not_found");
        await (await adminA.PutAsJsonAsync($"{Base}/deals/{dealA.Id()}", new { name = "X", accountId = accountB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/leads/{leadA.Id()}/convert", new { createDeal = false, accountId = accountB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PostAsJsonAsync($"{Base}/leads/{leadA.Id()}/convert", new { createDeal = true, dealName = "X", pipelineId = pipelineB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Kayıt bazlı denetim de kiracıya bağlıdır: A, B'nin kayıt kimliğiyle boş sonuç alır.
        var foreignAudit = await adminA.GetJsonAsync($"{Base}/audit?entityType=Account&entityId={accountB.Id()}");
        foreignAudit.GetProperty("total").GetInt64().ShouldBe(0);
        var orgAudit = await adminA.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100");
        var auditedIds = orgAudit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()).ToList();
        new[] { accountB.Id(), contactB.Id(), leadB.Id(), dealB.Id(), pipelineB.Id(), extraPipelineB.Id(), stageB }.ShouldAllBe(id => !auditedIds.Contains(id.ToString()));

        // B tarafında hiçbir şey değişmedi.
        (await adminB.GetJsonAsync($"{Base}/accounts/{accountB.Id()}")).GetProperty("name").GetString().ShouldBe("B Firması");
        (await adminB.GetJsonAsync($"{Base}/accounts/{accountB.Id()}")).GetProperty("contactCount").GetInt32().ShouldBe(1);
        (await adminB.GetJsonAsync($"{Base}/leads/{leadB.Id()}")).GetProperty("status").GetString().ShouldBe("new");
        (await adminB.GetJsonAsync($"{Base}/leads/{leadB2.Id()}")).GetProperty("status").GetString().ShouldBe("new");
        var dealBAfter = await adminB.GetJsonAsync($"{Base}/deals/{dealB.Id()}");
        (dealBAfter.GetProperty("name").GetString(), dealBAfter.GetProperty("stageName").GetString()).ShouldBe(("B Fırsatı", "Nitelendirme"));
        (await adminB.GetJsonAsync($"{Base}/pipelines/{pipelineB.Id()}")).GetProperty("stages").GetArrayLength().ShouldBe(6);
        (await adminB.DefaultPipelineAsync()).Id().ShouldBe(pipelineB.Id());
        (await adminA.GetJsonAsync($"{Base}/leads/{leadA.Id()}")).GetProperty("status").GetString().ShouldBe("new");
    }

    [Theory]
    [InlineData("tr", "Nitelendirme", "Kazanıldı", "Kaybedildi")]
    [InlineData("en", "Qualification", "Closed Won", "Closed Lost")]
    public async Task SignUp_SeedsTheDefaultPipeline_ThroughTheOutboxEvent_InTheOrganizationLanguage(string locale, string first, string won, string lost)
    {
        var org = await factory.NewOrgAsync($"Seed {locale} Org", locale);

        // Kayıt işlemi OrganizationCreated olayını aynı transaction'da Identity outbox'una yazdı.
        (await factory.OutboxMessagesAsync<IdentityDbContext>("Identity.OrganizationCreated", org.TenantId.ToString())).ShouldHaveSingleItem();

        // Worker'ın yaptığı iş: outbox boşaltılır → Sales tüketicisi huniyi tohumlar (API isteği/tembel yol devrede değil).
        await factory.DrainOutboxAsync<IdentityDbContext>();
        (await factory.OutboxMessagesAsync<IdentityDbContext>("Identity.OrganizationCreated", org.TenantId.ToString())).Single().ProcessedAt.ShouldNotBeNull();

        var seeded = await PipelinesOfAsync(org.TenantId);
        var pipeline = seeded.ShouldHaveSingleItem();
        pipeline.IsDefault.ShouldBeTrue();
        pipeline.Stages.OrderBy(s => s.Order).Select(s => s.Name).ShouldBe(locale == "tr"
            ? ["Nitelendirme", "İhtiyaç Analizi", "Teklif", "Pazarlık", "Kazanıldı", "Kaybedildi"]
            : ["Qualification", "Needs Analysis", "Proposal", "Negotiation", "Closed Won", "Closed Lost"]);
        pipeline.Stages.OrderBy(s => s.Order).Select(s => s.Probability).ShouldBe([10, 20, 50, 75, 100, 0]);
        pipeline.Stages.Single(s => s.Kind == Sense.Crm.Modules.Sales.Domain.Pipelines.StageKind.Won).Name.ShouldBe(won);
        pipeline.Stages.Single(s => s.Kind == Sense.Crm.Modules.Sales.Domain.Pipelines.StageKind.Lost).Name.ShouldBe(lost);
        pipeline.Stages.First(s => s.Order == 0).Name.ShouldBe(first);

        // API de aynı (tek) huniyi görür; olay yeniden işlense de çoğalmaz (idempotent).
        (await org.Admin.GetJsonAsync($"{Base}/pipelines")).GetArrayLength().ShouldBe(1);
        using (var scope = factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<Sense.Crm.Modules.Sales.Application.IDefaultPipelineSeeder>().EnsureAsync(org.TenantId, locale, Ct)).ShouldBeFalse();
        }

        (await PipelinesOfAsync(org.TenantId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ApiStartup_SeedsTheDefaultPipeline_ForExistingOrganizations_Idempotently()
    {
        var org = await factory.NewOrgAsync("Existing Org", "en");
        await factory.DrainOutboxAsync<IdentityDbContext>();
        (await PipelinesOfAsync(org.TenantId)).Count.ShouldBe(1);

        // M1'de açılmış organizasyonu taklit et: huniyi (fiziksel olarak) sil.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
            await db.PipelineStages.IgnoreQueryFilters().Where(s => s.TenantId == org.TenantId).ExecuteDeleteAsync(Ct);
            await db.Pipelines.IgnoreQueryFilters().Where(p => p.TenantId == org.TenantId).ExecuteDeleteAsync(Ct);
        }

        (await PipelinesOfAsync(org.TenantId)).ShouldBeEmpty();

        var startup = factory.Services.GetServices<IHostedService>().OfType<DefaultPipelineSyncHostedService>().Single();
        await startup.StartAsync(Ct);
        await startup.StartAsync(Ct);

        var pipeline = (await PipelinesOfAsync(org.TenantId)).ShouldHaveSingleItem();
        pipeline.Name.ShouldBe("Sales Pipeline");
        pipeline.Stages.Count.ShouldBe(6);
        (await org.Admin.GetJsonAsync($"{Base}/pipelines")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Audit_RecordsSalesChanges_WithMaskedPersonalData_AndPerRecordEndpoint()
    {
        var org = await factory.NewOrgAsync("Audit Sales Org");
        var admin = org.Admin;

        var account = await admin.CreateAccountAsync("Denetimli Firma", new { Email = "gizli@firma.com", Phone = "0212 999 99 99", Industry = "Bankacılık" });
        var id = account.Id();
        await admin.PutJsonAsync($"{Base}/accounts/{id}", new { name = "Denetimli Firma A.Ş.", industry = "Bankacılık", email = "yeni@firma.com" });
        await admin.DeleteJsonAsync($"{Base}/accounts/{id}");

        var audit = await admin.GetJsonAsync($"{Base}/audit?entityType=Account&entityId={id}");
        audit.GetProperty("total").GetInt64().ShouldBe(3);
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("action").GetString()).ShouldBe(["deleted", "updated", "created"], "en yeni önce");
        items.ShouldAllBe(i => i.GetProperty("entityId").GetString() == id.ToString() && i.GetProperty("entityType").GetString() == "Account");
        items.ShouldAllBe(i => i.GetProperty("userDisplayName").GetString() == org.AdminName);

        var created = items[2].GetProperty("changes");
        created.GetProperty("name").GetProperty("new").GetString().ShouldBe("Denetimli Firma");
        created.GetProperty("email").GetProperty("new").GetString().ShouldBe("***", "kişisel veri maskelenir");
        created.GetProperty("phone").GetProperty("new").GetString().ShouldBe("***");
        created.GetProperty("industry").GetProperty("new").GetString().ShouldBe("Bankacılık");
        created.GetProperty("ownerUserId").GetProperty("new").GetGuid().ShouldBe(org.AdminUserId);

        var updated = items[1].GetProperty("changes");
        updated.GetProperty("name").GetProperty("old").GetString().ShouldBe("Denetimli Firma");
        updated.GetProperty("name").GetProperty("new").GetString().ShouldBe("Denetimli Firma A.Ş.");
        updated.GetProperty("email").GetProperty("new").GetString().ShouldBe("***");
        updated.TryGetProperty("industry", out _).ShouldBeFalse("değişmeyen alan yazılmaz");
        var raw = audit.GetRawText();
        raw.ShouldNotContain("gizli@firma.com");
        raw.ShouldNotContain("yeni@firma.com");
        raw.ShouldNotContain("0212 999 99 99");

        // Sayfalama ve zorunlu parametreler.
        (await admin.GetJsonAsync($"{Base}/audit?entityType=Account&entityId={id}&page=2&pageSize=2")).GetProperty("items").GetArrayLength().ShouldBe(1);
        await (await admin.GetAsync($"{Base}/audit", Ct)).ShouldBeValidationErrorAsync("entityType");
        await (await admin.GetAsync($"{Base}/audit?entityType=Account", Ct)).ShouldBeValidationErrorAsync("entityId");

        // Kurumsal denetim akışı (org.audit.read) firma değişikliklerini de içerir.
        var orgAudit = await admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100");
        orgAudit.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("entityId").GetString() == id.ToString()).ShouldBe(3);
    }

    [Fact]
    public async Task Audit_CoversContactsLeadsDealsAndPipelines_AndConversionTrail()
    {
        var admin = (await factory.NewOrgAsync("Audit Everything")).Admin;
        var pipeline = await admin.DefaultPipelineAsync();
        var account = await admin.CreateAccountAsync("Firma");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kişi", email = "k@x.com", mobile = "0555", phone = "0212" });
        var lead = await admin.CreateLeadAsync("Aday", "Aday A.Ş.", new { Email = "aday@x.com" });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Fırsat", accountId = account.Id() });
        await admin.PostJsonAsync($"{Base}/deals/{deal.Id()}/stage", new { stageId = pipeline.StageId("Teklif") }, HttpStatusCode.NoContent);
        await admin.PutJsonAsync($"{Base}/pipelines/{pipeline.Id()}", new { name = "Yeniden adlandırıldı", isDefault = true });
        await admin.PostJsonAsync($"{Base}/leads/{lead.Id()}/convert", new { createDeal = false }, HttpStatusCode.OK);

        var contactAudit = await admin.GetJsonAsync($"{Base}/audit?entityType=Contact&entityId={contact.Id()}");
        var contactChanges = contactAudit.GetProperty("items").EnumerateArray().Single().GetProperty("changes");
        contactChanges.GetProperty("email").GetProperty("new").GetString().ShouldBe("***");
        contactChanges.GetProperty("mobile").GetProperty("new").GetString().ShouldBe("***");
        contactChanges.GetProperty("phone").GetProperty("new").GetString().ShouldBe("***");
        contactChanges.GetProperty("lastName").GetProperty("new").GetString().ShouldBe("Kişi");

        var leadAudit = (await admin.GetJsonAsync($"{Base}/audit?entityType=Lead&entityId={lead.Id()}")).GetProperty("items").EnumerateArray().ToList();
        leadAudit.Select(i => i.GetProperty("action").GetString()).ShouldBe(["updated", "created"]);
        var statusChange = leadAudit[0].GetProperty("changes").GetProperty("status");
        (statusChange.GetProperty("old").GetString(), statusChange.GetProperty("new").GetString()).ShouldBe(("new", "converted"));
        leadAudit[1].GetProperty("changes").GetProperty("email").GetProperty("new").GetString().ShouldBe("***");

        var dealAudit = (await admin.GetJsonAsync($"{Base}/audit?entityType=Deal&entityId={deal.Id()}")).GetProperty("items").EnumerateArray().ToList();
        dealAudit.Select(i => i.GetProperty("action").GetString()).ShouldBe(["updated", "created"]);
        dealAudit[0].GetProperty("changes").GetProperty("stageId").GetProperty("new").GetGuid().ShouldBe(pipeline.StageId("Teklif"));

        var pipelineAudit = (await admin.GetJsonAsync($"{Base}/audit?entityType=Pipeline&entityId={pipeline.Id()}")).GetProperty("items").EnumerateArray().ToList();
        pipelineAudit[0].GetProperty("changes").GetProperty("name").GetProperty("new").GetString().ShouldBe("Yeniden adlandırıldı");
    }

    [Fact]
    public async Task Audit_PerRecordEndpoint_NeedsTheMatchingReadPermission()
    {
        var org = await factory.NewOrgAsync("Audit Perms");
        var account = await org.Admin.CreateAccountAsync("İzinli");
        var pipeline = await org.Admin.DefaultPipelineAsync();
        var deal = await org.Admin.PostJsonAsync($"{Base}/deals", new { name = "Gizli fırsat", accountId = account.Id() });
        var (accountsReader, _) = await factory.AddMemberAsync(org, "Firma Okuyucu", "crm.accounts.read");
        var (nobody, _) = await factory.AddMemberAsync(org, "Hiçbiri", "crm.leads.read");

        // crm.accounts.read → Account denetimi (org.audit.read olmadan).
        (await accountsReader.GetJsonAsync($"{Base}/audit?entityType=Account&entityId={account.Id()}")).GetProperty("total").GetInt64().ShouldBe(1);
        await (await accountsReader.GetAsync($"{Base}/organization/audit", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Başka kaynakların denetimi için o kaynağın okuma izni gerekir; bilinmeyen tür yalnız org.audit.read ile okunur.
        await (await accountsReader.GetAsync($"{Base}/audit?entityType=Deal&entityId={deal.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await accountsReader.GetAsync($"{Base}/audit?entityType=Pipeline&entityId={pipeline.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await accountsReader.GetAsync($"{Base}/audit?entityType=Role&entityId={Guid.NewGuid()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await nobody.GetAsync($"{Base}/audit?entityType=Account&entityId={account.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await factory.CreateClient().GetAsync($"{Base}/audit?entityType=Account&entityId={account.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");

        // Yönetici (org.audit.read) her türü okuyabilir.
        (await org.Admin.GetJsonAsync($"{Base}/audit?entityType=Deal&entityId={deal.Id()}")).GetProperty("total").GetInt64().ShouldBe(1);
    }

    private async Task<List<Sense.Crm.Modules.Sales.Domain.Pipelines.Pipeline>> PipelinesOfAsync(Guid tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
        return await db.Pipelines.AsNoTracking().IgnoreQueryFilters().Include(p => p.Stages).Where(p => p.TenantId == tenantId).ToListAsync(Ct);
    }
}
