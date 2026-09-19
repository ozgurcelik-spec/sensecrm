using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Infrastructure.Security;
using Sense.Crm.Modules.Service.Contracts;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Service.Tests.Api.ServiceApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Service.Tests.Api;

/// <summary>İzinler (okuma/yazma ayrı, doğrulama yetkiden önce, SLA ve rapor izinleri) ve kiracılar arası izolasyon.</summary>
[Collection(ApiCollection.Name)]
public sealed class ServicePermissionsIsolationApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task PermissionCatalog_ContainsTheCaseKeys_AndSystemRolesReceiveThem()
    {
        var org = await factory.NewOrgAsync("Perm Catalog");

        var permissions = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => (Key: p.Str("key"), Group: p.Str("group"))).ToList();
        permissions.ShouldContain(("crm.cases.read", "crm"));
        permissions.ShouldContain(("crm.cases.write", "crm"));

        var roles = (await org.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Where(r => r.GetProperty("isSystem").GetBoolean()).ToList();
        roles.Count.ShouldBe(2);
        foreach (var role in roles)
        {
            var keys = role.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
            keys.ShouldContain("crm.cases.read", $"{role.Str("name")} rolü");
            keys.ShouldContain("crm.cases.write", $"{role.Str("name")} rolü");
        }

        // Administrator = tüm katalog (sabit sayı değil, birebir eşitlik).
        var me = await org.Admin.GetJsonAsync($"{Base}/me");
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Order().ShouldBe(permissions.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task ExistingOrganizations_GetTheNewKeys_ThroughTheSystemRoleSync()
    {
        var org = await factory.NewOrgAsync("Perm Sync");
        await factory.ExecuteAsync(
            "UPDATE identity.roles SET permissions = array_remove(array_remove(permissions, 'crm.cases.read'), 'crm.cases.write') WHERE tenant_id = @t AND is_system",
            ("t", org.TenantId));

        using (var scope = factory.Services.CreateScope())
        {
            using var system = Sense.Crm.Shared.Infrastructure.Context.CurrentUserAccessor.UseSystem();
            (await scope.ServiceProvider.GetRequiredService<SystemRolePermissionSynchronizer>().SyncTenantAsync(org.TenantId, Ct)).ShouldBeGreaterThan(0);
        }

        foreach (var role in (await org.Admin.GetJsonAsync($"{Base}/organization/roles")).EnumerateArray().Where(r => r.GetProperty("isSystem").GetBoolean()))
        {
            role.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("crm.cases.write");
        }
    }

    [Fact]
    public async Task ReadOnlyMember_ReadsButCannotWrite_AndValidationRunsBeforeAuthorization()
    {
        var org = await factory.NewOrgAsync("Perm Reader");
        var (reader, _) = await factory.AddMemberAsync(org, "Okuyucu", ServicePermissions.CasesRead);
        var id = (await org.Admin.CreateCaseAsync("Okunacak")).Id();

        // Okuma uçları çalışır.
        (await reader.ListIdsAsync()).ShouldContain(id);
        (await reader.GetCaseAsync(id)).Str("subject").ShouldBe("Okunacak");
        (await reader.TimelineAsync(id)).ShouldNotBeEmpty();
        (await reader.GetJsonAsync($"{CasesPath}/summary")).Has("openCount").ShouldBeTrue();

        // Her yazma ucu 403 (geçerli gövdeyle).
        await (await reader.PostAsJsonAsync(CasesPath, new { subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PutAsJsonAsync($"{CasesPath}/{id}", new { subject = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.DeleteAsync($"{CasesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "open" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CasesPath}/{id}/priority", new { priority = "high" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CasesPath}/{id}/assign", new { assignedUserId = (Guid?)null }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await reader.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "public", body = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Geçersiz gövde yine 400 (doğrulama yetkiden önce).
        await (await reader.PostAsJsonAsync(CasesPath, new { subject = "" }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await reader.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { body = "x" }, Ct)).ShouldBeValidationErrorAsync("visibility");

        // Hiçbir şey değişmedi.
        var unchanged = await org.Admin.GetCaseAsync(id);
        (unchanged.Str("subject"), unchanged.Str("status")).ShouldBe(("Okunacak", "new"));
    }

    [Fact]
    public async Task WriteOnlyMember_CannotRead_AndWriteDoesNotImplyRead()
    {
        var org = await factory.NewOrgAsync("Perm Writer");
        var (writer, _) = await factory.AddMemberAsync(org, "Yazıcı", ServicePermissions.CasesWrite);
        var id = (await org.Admin.CreateCaseAsync("Yazılacak")).Id();

        await (await writer.GetAsync(CasesPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await writer.GetAsync($"{CasesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await writer.GetAsync($"{CasesPath}/{id}/timeline", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await writer.GetAsync($"{CasesPath}/summary", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await writer.GetAsync($"{Base}/audit?entityType=Case&entityId={id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Yazma uçları çalışır (yorum yazma izni ister).
        (await writer.PostAsJsonAsync($"{CasesPath}/{id}/comments", new { visibility = "internal", body = "yazıcı yorumu" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await writer.PostAsJsonAsync($"{CasesPath}/{id}/status", new { status = "open" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await writer.PostAsJsonAsync($"{CasesPath}/{id}/priority", new { priority = "high" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await writer.PutAsJsonAsync($"{CasesPath}/{id}", new { subject = "Yazıcı düzenledi" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await org.Admin.GetCaseAsync(id)).Str("subject").ShouldBe("Yazıcı düzenledi");
    }

    [Fact]
    public async Task SlaEndpoints_RequireSettingsManage_ReportsRequireReportsRead()
    {
        var org = await factory.NewOrgAsync("Perm Sla Reports");
        var (caseManager, _) = await factory.AddMemberAsync(org, "Talep Yöneticisi", ServicePermissions.CasesRead, ServicePermissions.CasesWrite);
        var (settings, _) = await factory.AddMemberAsync(org, "Ayar Yöneticisi", "org.settings.manage");
        var (reporter, _) = await factory.AddMemberAsync(org, "Raporcu", "crm.reports.read");
        var body = new
        {
            policies = new object[]
            {
                new { priority = "low", firstResponseMinutes = 10, resolutionMinutes = 20 },
                new { priority = "normal", firstResponseMinutes = 10, resolutionMinutes = 20 },
                new { priority = "high", firstResponseMinutes = 10, resolutionMinutes = 20 },
                new { priority = "urgent", firstResponseMinutes = 10, resolutionMinutes = 20 },
            },
        };

        await (await caseManager.GetAsync(SlaPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await caseManager.PutAsJsonAsync(SlaPath, body, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await settings.GetJsonAsync(SlaPath)).GetArrayLength().ShouldBe(4);
        (await settings.PutAsJsonAsync(SlaPath, body, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await settings.PutAsJsonAsync(SlaPath, new { policies = Array.Empty<object>() }, Ct)).ShouldBeValidationErrorAsync("policies");

        await (await caseManager.GetAsync($"{ReportsPath}/summary", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await caseManager.GetAsync($"{ReportsPath}/by-assignee", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await reporter.GetAsync($"{ReportsPath}/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reporter.GetAsync($"{ReportsPath}/by-assignee", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await reporter.GetAsync($"{CasesPath}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Unauthenticated_RequestsAreRejected()
    {
        var anonymous = factory.CreateClient();

        await (await anonymous.GetAsync(CasesPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
        await (await anonymous.GetAsync(SlaPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
        await (await anonymous.GetAsync($"{ReportsPath}/summary", Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }

    [Fact]
    public async Task CrossTenant_CasesAreInvisibleAndImmutable_AcrossEveryEndpoint()
    {
        var a = await factory.NewOrgAsync("Iso A");
        var b = await factory.NewOrgAsync("Iso B");
        var (_, memberB) = await factory.AddMemberAsync(b, "B Üyesi", "crm.cases.read");
        var accountA = await a.Admin.CreateAccountAsync("A Firması");
        var contactA = await a.Admin.CreateContactAsync("A Kişisi");
        var accountB = await b.Admin.CreateAccountAsync("B Firması");
        var contactB = await b.Admin.CreateContactAsync("B Kişisi");
        var caseA = (await a.Admin.CreateCaseAsync("A'nın talebi", new { accountId = accountA.Id(), contactId = contactA.Id() })).Id();
        await a.Admin.CommentAsync(caseA, "public", "A yorumu");
        var caseB = (await b.Admin.CreateCaseAsync("B'nin talebi", new { accountId = accountB.Id() })).Id();

        // A'nın talebi B'de: liste/detay/zaman çizelgesi/eylemler 404.
        (await b.Admin.ListIdsAsync("?pageSize=100")).ShouldBe([caseB]);
        await (await b.Admin.GetAsync($"{CasesPath}/{caseA}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.GetAsync($"{CasesPath}/{caseA}/timeline", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PutAsJsonAsync($"{CasesPath}/{caseA}", new { subject = "Ele geçirildi" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PostAsJsonAsync($"{CasesPath}/{caseA}/status", new { status = "resolved", resolutionNote = "x" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PostAsJsonAsync($"{CasesPath}/{caseA}/priority", new { priority = "urgent" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PostAsJsonAsync($"{CasesPath}/{caseA}/assign", new { assignedUserId = b.AdminUserId }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.PostAsJsonAsync($"{CasesPath}/{caseA}/comments", new { visibility = "public", body = "sızma" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await b.Admin.DeleteAsync($"{CasesPath}/{caseA}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // B'nin firma/kişi/üyesini A'nın talebine bağlamak reddedilir.
        await (await a.Admin.PostAsJsonAsync(CasesPath, new { subject = "Çapraz", accountId = accountB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.account_not_found");
        await (await a.Admin.PostAsJsonAsync(CasesPath, new { subject = "Çapraz", contactId = contactB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.contact_not_found");
        await (await a.Admin.PostAsJsonAsync(CasesPath, new { subject = "Çapraz", assignedUserId = memberB }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await a.Admin.PutAsJsonAsync($"{CasesPath}/{caseA}", new { subject = "Çapraz", accountId = accountB.Id() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "case.account_not_found");
        await (await a.Admin.PostAsJsonAsync($"{CasesPath}/{caseA}/assign", new { assignedUserId = memberB }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");

        // Denetim ve özet/rapor yalnız kendi verisini görür.
        var foreignAudit = await b.Admin.GetJsonAsync($"{Base}/audit?entityType=Case&entityId={caseA}");
        foreignAudit.GetProperty("total").GetInt64().ShouldBe(0);
        (await b.Admin.GetJsonAsync($"{CasesPath}/summary")).GetProperty("openCount").GetInt32().ShouldBe(1);
        (await b.Admin.GetJsonAsync($"{ReportsPath}/summary")).GetProperty("totalCount").GetInt32().ShouldBe(1);

        // A tarafında hiçbir şey değişmedi.
        var unchanged = await a.Admin.GetCaseAsync(caseA);
        (unchanged.Str("subject"), unchanged.Str("status"), unchanged.Str("priority")).ShouldBe(("A'nın talebi", "open", "normal"));
        (await a.Admin.TimelineAsync(caseA)).Count(i => i.Str("type") == "comment").ShouldBe(1);
    }

    [Fact]
    public async Task CrossTenant_SlaPoliciesAndNumberCounters_AreIsolated()
    {
        var a = await factory.NewOrgAsync("Iso Sla A");
        var b = await factory.NewOrgAsync("Iso Sla B");

        (await a.Admin.GetJsonAsync(SlaPath)).GetArrayLength().ShouldBe(4);
        (await b.Admin.GetJsonAsync(SlaPath)).GetArrayLength().ShouldBe(4);
        await b.Admin.PutJsonAsync(SlaPath, new
        {
            policies = new object[]
            {
                new { priority = "low", firstResponseMinutes = 1, resolutionMinutes = 2 },
                new { priority = "normal", firstResponseMinutes = 1, resolutionMinutes = 2 },
                new { priority = "high", firstResponseMinutes = 1, resolutionMinutes = 2 },
                new { priority = "urgent", firstResponseMinutes = 1, resolutionMinutes = 2 },
            },
        });

        (await a.Admin.GetJsonAsync(SlaPath))[1].GetProperty("firstResponseMinutes").GetInt32().ShouldBe(480, "B'nin PUT'u A'yı etkilemez");
        (await b.Admin.GetJsonAsync(SlaPath))[1].GetProperty("firstResponseMinutes").GetInt32().ShouldBe(1);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.sla_policies WHERE tenant_id = @t", ("t", a.TenantId))).ShouldBe(4);
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.sla_policies WHERE tenant_id = @t", ("t", b.TenantId))).ShouldBe(4);

        (await a.Admin.CreateCaseAsync("A1")).Str("number").ShouldEndWith("-0001");
        (await b.Admin.CreateCaseAsync("B1")).Str("number").ShouldEndWith("-0001");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM service.case_counters WHERE tenant_id IN (@a, @b)", ("a", a.TenantId), ("b", b.TenantId))).ShouldBe(2);
    }
}
