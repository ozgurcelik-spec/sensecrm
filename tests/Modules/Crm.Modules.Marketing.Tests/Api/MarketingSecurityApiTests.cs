using System.Net;
using System.Net.Http.Json;
using Crm.Modules.Identity.Infrastructure.Security;
using Crm.Shared.Infrastructure.Context;
using Crm.Tests.Shared.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Crm.Modules.Marketing.Tests.Api.MarketingApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Marketing.Tests.Api;

/// <summary>İzinler (403), kiracı izolasyonu (zorunlu), sistem rolleri ve denetim kaydı.</summary>
[Collection(ApiCollection.Name)]
public sealed class MarketingSecurityApiTests(CrmApiFactory factory)
{
    private const string Summary = $"{Base}/reports/marketing/summary";

    [Fact]
    public async Task CrossTenantIsolation_AdminOfA_CannotSeeOrModify_CampaignsOfB()
    {
        var a = await factory.NewOrgAsync("Mkt Iso A");
        var b = await factory.NewOrgAsync("Mkt Iso B");
        var leadsB = await b.Admin.NewLeadIdsAsync(2, "B");
        var leadA = (await a.Admin.CreateLeadAsync("A lead")).Id();
        var campaignB = await b.Admin.NewCampaignIdAsync("B kampanyası", "email", new { budget = 100, actualCost = 50, startDate = "2024-04-04" });
        var campaignA = await a.Admin.NewCampaignIdAsync("A kampanyası", "event", new { startDate = "2024-04-04" });
        await b.Admin.AddMembersAsync(campaignB, "lead", leadsB);
        await a.Admin.AddMembersAsync(campaignA, "lead", [leadA]);
        var rowB = (await b.Admin.MembersAsync(campaignB)).First().Id();

        // Okuma: bulunamaz, listelerde yok.
        await (await a.Admin.GetAsync($"{CampaignsPath}/{campaignB}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.GetAsync($"{CampaignsPath}/{campaignB}/members", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.GetAsync($"{CampaignsPath}/{campaignB}/metrics", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await a.Admin.ListIdsAsync()).ShouldBe([campaignA]);
        (await a.Admin.ListIdsAsync($"?ownerUserId={b.AdminUserId}")).ShouldBeEmpty();
        (await a.Admin.ListIdsAsync("?q=B%20kampanyas%C4%B1")).ShouldBeEmpty();

        // Değiştirme: bulunamaz; B tarafında hiçbir şey değişmez.
        await (await a.Admin.PutAsJsonAsync($"{CampaignsPath}/{campaignB}", new { name = "Ele geçirildi", type = "email" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.PostAsJsonAsync($"{CampaignsPath}/{campaignB}/status", new { status = "active" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.DeleteAsync($"{CampaignsPath}/{campaignB}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.PostAsJsonAsync($"{CampaignsPath}/{campaignB}/members", new { memberType = "lead", memberIds = new[] { leadA } }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.PostAsJsonAsync($"{CampaignsPath}/{campaignB}/members/status", new { memberIds = new[] { rowB }, status = "sent" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await a.Admin.PostAsJsonAsync($"{CampaignsPath}/{campaignB}/members/remove", new { memberIds = new[] { rowB } }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        var afterB = await b.Admin.GetJsonAsync($"{CampaignsPath}/{campaignB}");
        (afterB.Str("name"), afterB.Str("status"), afterB.Int("memberCount")).ShouldBe(("B kampanyası", "planned", 2));
        (await b.Admin.MembersAsync(campaignB)).ShouldAllBe(m => m.Str("status") == "added");

        // Çapraz referanslar: B'nin lead'i A'nın kampanyasına eklenemez; kayıt bazlı uç da bulamaz.
        var cross = await a.Admin.AddMembersAsync(campaignA, "lead", [leadsB[0]]);
        (cross.Int("addedCount"), cross.GetProperty("skipped")[0].Str("reason")).ShouldBe((0, "not_found"));
        await (await a.Admin.GetAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={leadsB[0]}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await b.Admin.GetJsonAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={leadsB[0]}")).EnumerateArray().Single().Str("campaignId").ShouldBe(campaignB.ToString());

        // Rapor ve denetim de kiracıya bağlıdır.
        var reportA = await a.Admin.GetJsonAsync($"{Summary}?from=2024-01-01&to=2024-12-31");
        (reportA.Int("campaignCount"), reportA.GetProperty("totals").Dec("budget"), reportA.GetProperty("totals").Int("memberCount")).ShouldBe((1, 0m, 1));
        reportA.GetProperty("topCampaigns").EnumerateArray().Single().Id().ShouldBe(campaignA);
        (await a.Admin.GetJsonAsync($"{Base}/audit?entityType=Campaign&entityId={campaignB}")).GetProperty("total").GetInt64().ShouldBe(0);
        (await a.Admin.GetJsonAsync($"{Base}/audit?entityType=CampaignMember&entityId={rowB}")).GetProperty("total").GetInt64().ShouldBe(0);
        var orgAudit = await a.Admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100");
        orgAudit.GetProperty("items").EnumerateArray().Select(i => i.Str("entityId")).ShouldNotContain(campaignB.ToString());
    }

    [Fact]
    public async Task Permissions_ReadOnlyUserReads_ButEveryWriteIs403_AndValidationRunsFirst()
    {
        var org = await factory.NewOrgAsync("Mkt Permissions");
        var (reader, _) = await factory.AddMemberAsync(org, "Okuyucu", "crm.campaigns.read");
        var (writer, _) = await factory.AddMemberAsync(org, "Yazıcı", "crm.campaigns.read", "crm.campaigns.write");
        var (nobody, _) = await factory.AddMemberAsync(org, "Hiçbiri", "crm.deals.read");
        var lead = (await org.Admin.CreateLeadAsync("Kaya")).Id();
        var campaign = await org.Admin.NewCampaignIdAsync();
        await org.Admin.AddMembersAsync(campaign, "lead", [lead]);
        var row = (await org.Admin.MembersAsync(campaign)).Single().Id();

        // Okuma uçları: crm.campaigns.read yeter.
        foreach (var url in new[]
        {
            CampaignsPath,
            $"{CampaignsPath}/{campaign}",
            $"{CampaignsPath}/{campaign}/members",
            $"{CampaignsPath}/{campaign}/metrics",
            $"{CampaignsPath}/by-member?memberType=lead&memberId={lead}",
        })
        {
            (await reader.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, url);
        }

        // Yazma uçları: 403.
        await (await reader.PostAsJsonAsync(CampaignsPath, new { name = "x", type = "email" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PutAsJsonAsync($"{CampaignsPath}/{campaign}", new { name = "x", type = "email" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.DeleteAsync($"{CampaignsPath}/{campaign}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CampaignsPath}/{campaign}/status", new { status = "active" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CampaignsPath}/{campaign}/members", new { memberType = "lead", memberIds = new[] { lead } }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CampaignsPath}/{campaign}/members/status", new { memberIds = new[] { row }, status = "sent" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CampaignsPath}/{campaign}/members/remove", new { memberIds = new[] { row } }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Doğrulama yetkiden önce çalışır: geçersiz gövdeli yetkisiz istek 400 alır.
        await (await reader.PostAsJsonAsync(CampaignsPath, new { type = "email" }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await reader.PostAsJsonAsync($"{CampaignsPath}/{campaign}/members", new { memberType = "lead", memberIds = Array.Empty<Guid>() }, Ct)).ShouldBeValidationErrorAsync("memberIds");
        await (await reader.PostAsJsonAsync($"{CampaignsPath}/{campaign}/status", new { }, Ct)).ShouldBeValidationErrorAsync("status");

        // Hiçbir şey değişmedi; yazma izni olan çalışır.
        (await org.Admin.MembersAsync(campaign)).Single().Str("status").ShouldBe("added");
        (await writer.PostAsJsonAsync($"{CampaignsPath}/{campaign}/status", new { status = "active" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await writer.PostAsJsonAsync($"{CampaignsPath}/{campaign}/members/status", new { memberIds = new[] { row }, status = "sent" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await writer.PostAsJsonAsync(CampaignsPath, new { name = "Yazıcının", type = "other" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Okuma izni olmayan okuyamaz (yazma izni okumayı kapsamaz).
        await (await nobody.GetAsync(CampaignsPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await nobody.GetAsync($"{CampaignsPath}/{campaign}/metrics", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await nobody.GetAsync($"{CampaignsPath}/by-member?memberType=lead&memberId={lead}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var (writeOnly, _) = await factory.AddMemberAsync(org, "Yalnız yazan", "crm.campaigns.write");
        await (await writeOnly.GetAsync(CampaignsPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Rapor yalnız crm.reports.read ile (kampanya izni yetmez).
        await (await reader.GetAsync(Summary, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var (reports, _) = await factory.AddMemberAsync(org, "Raporcu", "crm.reports.read");
        (await reports.GetAsync(Summary, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await reports.GetAsync(CampaignsPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.GetAsync($"{Summary}?from=2024-06-01&to=2024-05-01", Ct)).ShouldBeValidationErrorAsync("to");
    }

    [Fact]
    public async Task PermissionCatalog_ContainsTheCampaignKeys_AndUnauthenticatedCallsAreRejected()
    {
        var org = await factory.NewOrgAsync("Mkt Catalog");

        var keys = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => (Key: p.Str("key"), Group: p.Str("group"))).ToList();
        keys.ShouldContain(("crm.campaigns.read", "crm"));
        keys.ShouldContain(("crm.campaigns.write", "crm"));
        keys.Select(k => k.Key).Distinct().Count().ShouldBe(keys.Count);

        // Administrator kataloğun tamamını alır (yeni iki anahtar dahil).
        var me = await org.Admin.GetJsonAsync($"{Base}/me");
        var granted = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        granted.ShouldContain("crm.campaigns.read");
        granted.ShouldContain("crm.campaigns.write");
        granted.Count.ShouldBe(keys.Count);

        var anonymous = factory.CreateClient();
        await (await anonymous.GetAsync(CampaignsPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
        await (await anonymous.GetAsync(Summary, Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }

    [Fact]
    public async Task StandardRole_CarriesTheCampaignPermissions_ForNewAndSynchronizedExistingOrganizations()
    {
        var org = await factory.NewOrgAsync("Mkt Standard New");

        // Yeni organizasyon: Standard kuralı ("tüm crm.*") iki yeni anahtarı otomatik alır.
        async Task<List<string>> StandardPermissionsAsync(Org o)
        {
            var roles = await o.Admin.GetJsonAsync($"{Base}/organization/roles");
            return roles.EnumerateArray().Single(r => r.Str("name") == "Standard").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        }

        var fresh = await StandardPermissionsAsync(org);
        fresh.ShouldContain("crm.campaigns.read");
        fresh.ShouldContain("crm.campaigns.write");
        fresh.ShouldNotContain("crm.approvals.decide");

        // Eski organizasyon: anahtarlar yokmuş gibi (M6C öncesi) → API açılışındaki senkronizatör yeniden yayar.
        await factory.ExecuteAsync(
            "UPDATE identity.roles SET permissions = array_remove(array_remove(permissions, 'crm.campaigns.read'), 'crm.campaigns.write') WHERE tenant_id = @t AND is_system",
            ("t", org.TenantId));
        using (var system = CurrentUserAccessor.UseSystem())
        {
            using var scope = factory.Services.CreateScope();
            var changed = await scope.ServiceProvider.GetRequiredService<SystemRolePermissionSynchronizer>().SyncTenantAsync(org.TenantId, Ct);
            changed.ShouldBeGreaterThanOrEqualTo(1);
        }

        var synced = await StandardPermissionsAsync(org);
        synced.ShouldContain("crm.campaigns.read");
        synced.ShouldContain("crm.campaigns.write");
        var adminRole = (await org.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Single(r => r.Str("name") == "Administrator");
        adminRole.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("crm.campaigns.write");
    }

    [Fact]
    public async Task Audit_RecordsCampaignAndMembershipChanges_AndIsReadableWithCampaignsReadOnly()
    {
        var org = await factory.NewOrgAsync("Mkt Audit");
        var (reader, _) = await factory.AddMemberAsync(org, "Denetçi", "crm.campaigns.read");
        var (outsider, _) = await factory.AddMemberAsync(org, "Yabancı", "crm.deals.read");
        var lead = (await org.Admin.CreateLeadAsync("İzlenen")).Id();

        var created = await org.Admin.CreateCampaignAsync("İzlenen kampanya", "email", new { budget = 100, description = "ilk" });
        var id = created.Id();
        await org.Admin.PutJsonAsync($"{CampaignsPath}/{id}", new { name = "İzlenen kampanya", type = "email", budget = 250, description = "ikinci", startDate = "2026-10-01" });
        await org.Admin.PostJsonAsync($"{CampaignsPath}/{id}/status", new { status = "active" }, HttpStatusCode.NoContent);
        await org.Admin.DeleteJsonAsync($"{CampaignsPath}/{id}");

        // crm.campaigns.read yeter (org.audit.read gerekmez); tür-izin eşlemesi Marketing modülünden gelir.
        var audit = await reader.GetJsonAsync($"{Base}/audit?entityType=Campaign&entityId={id}");
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        audit.GetProperty("total").GetInt64().ShouldBe(4);
        items.Select(i => i.Str("action")).Reverse().ShouldBe(["created", "updated", "updated", "deleted"]);
        items.ShouldAllBe(i => i.Str("entityType") == "Campaign");

        // Alan adları camelCase, enum değerleri camelCase string; kişisel veri alanı yok → maskeleme yok.
        var createdChanges = items[^1].GetProperty("changes");
        createdChanges.GetProperty("name").GetProperty("new").GetString().ShouldBe("İzlenen kampanya");
        createdChanges.GetProperty("status").GetProperty("new").GetString().ShouldBe("planned");
        createdChanges.GetProperty("type").GetProperty("new").GetString().ShouldBe("email");
        createdChanges.GetProperty("currency").GetProperty("new").GetString().ShouldBe("TRY");
        var updatedChanges = items[^2].GetProperty("changes");
        (updatedChanges.GetProperty("description").GetProperty("old").GetString(), updatedChanges.GetProperty("description").GetProperty("new").GetString()).ShouldBe(("ilk", "ikinci"));
        updatedChanges.GetProperty("startDate").GetProperty("new").GetString().ShouldBe("2026-10-01");
        var statusChange = items[^3].GetProperty("changes").GetProperty("status");
        (statusChange.Str("old"), statusChange.Str("new")).ShouldBe(("planned", "active"));
        items.SelectMany(i => i.GetProperty("changes").EnumerateObject()).ShouldAllBe(c => !c.Value.ToString().Contains("***", StringComparison.Ordinal));

        // Üyelik: ekleme (created), durum (updated), çıkarma (deleted; fiziksel silme).
        var campaign = await org.Admin.NewCampaignIdAsync("Üyelik denetimi");
        await org.Admin.AddMembersAsync(campaign, "lead", [lead]);
        var row = (await org.Admin.MembersAsync(campaign)).Single().Id();
        await org.Admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/status", new { memberIds = new[] { row }, status = "sent" }, HttpStatusCode.OK);
        await org.Admin.PostJsonAsync($"{CampaignsPath}/{campaign}/members/remove", new { memberIds = new[] { row } }, HttpStatusCode.OK);
        var memberAudit = (await reader.GetJsonAsync($"{Base}/audit?entityType=CampaignMember&entityId={row}")).GetProperty("items").EnumerateArray().ToList();
        memberAudit.Select(i => i.Str("action")).ShouldBe(["deleted", "updated", "created"]);
        memberAudit[^1].GetProperty("changes").GetProperty("memberType").GetProperty("new").GetString().ShouldBe("lead");
        memberAudit[^1].GetProperty("changes").GetProperty("memberId").GetProperty("new").GetString().ShouldBe(lead.ToString());
        var sent = memberAudit[1].GetProperty("changes").GetProperty("status");
        (sent.Str("old"), sent.Str("new")).ShouldBe(("added", "sent"));
        memberAudit[0].GetProperty("changes").GetProperty("status").GetProperty("old").GetString().ShouldBe("sent");

        // İzni olmayan okuyamaz; org.audit.read'e sahip yönetici okur.
        await (await outsider.GetAsync($"{Base}/audit?entityType=Campaign&entityId={id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await outsider.GetAsync($"{Base}/audit?entityType=CampaignMember&entityId={row}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await org.Admin.GetJsonAsync($"{Base}/audit?entityType=Campaign&entityId={id}")).GetProperty("total").GetInt64().ShouldBe(4);
    }
}
