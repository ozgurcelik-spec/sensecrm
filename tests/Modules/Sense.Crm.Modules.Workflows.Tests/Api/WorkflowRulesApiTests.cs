using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Workflows.Tests.Api.WorkflowsApiKit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Workflows.Tests.Api;

/// <summary>Kural CRUD, tür başına parametre doğrulaması, enable/disable, izinler, denetim, kiracı izolasyonu.</summary>
[Collection(ApiCollection.Name)]
public sealed class WorkflowRulesApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task Crud_DefaultsToEnabled_ResponseShape_FullReplaceAndSoftDelete()
    {
        var org = await factory.NewOrgAsync("Rules Crud");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");

        var created = await org.CreateRuleAsync(LeadRule(role, "  Web leadleri  ", ["web", "referral"], followUpHours: 48));
        var id = created.Id();

        created.Str("name").ShouldBe("Web leadleri");
        created.Str("kind").ShouldBe("leadAssignment");
        created.GetProperty("isEnabled").GetBoolean().ShouldBeTrue("isEnabled verilmezse etkin başlar");
        var lead = created.GetProperty("params");
        (lead.GetProperty("assigneeRoleId").GetGuid(), lead.GetProperty("followUpHours").GetInt32()).ShouldBe((role, 48));
        lead.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ShouldBe(["web", "referral"]);
        created.TryGetProperty("createdAt", out _).ShouldBeTrue();
        created.TryGetProperty("updatedAt", out _).ShouldBeFalse();

        (await org.Admin.GetJsonAsync($"{RulesPath}/{id}")).Id().ShouldBe(id);
        (await org.Admin.GetJsonAsync(RulesPath)).EnumerateArray().Select(r => r.Id()).ShouldBe([id]);

        // PUT tam değiştirmedir (tür dahil); etkin durumu değişmez.
        var approver = await org.CreateRoleAsync("Onaycilar", "crm.approvals.decide");
        await org.Admin.PutJsonAsync($"{RulesPath}/{id}", new { name = "Buyuk firsat", kind = "dealApproval", @params = new { minAmount = 250000, approverRoleId = approver } });
        var updated = await org.Admin.GetJsonAsync($"{RulesPath}/{id}");
        (updated.Str("name"), updated.Str("kind"), updated.GetProperty("isEnabled").GetBoolean()).ShouldBe(("Buyuk firsat", "dealApproval", true));
        (updated.GetProperty("params").GetProperty("minAmount").GetDecimal(), updated.GetProperty("params").GetProperty("approverRoleId").GetGuid()).ShouldBe((250000m, approver));
        updated.GetProperty("params").TryGetProperty("sources", out _).ShouldBeFalse("eski türün alanları kalmaz");
        updated.TryGetProperty("updatedAt", out _).ShouldBeTrue();

        await org.Admin.PostJsonAsync($"{RulesPath}/{id}/disable", null, HttpStatusCode.NoContent);
        (await org.Admin.GetJsonAsync($"{RulesPath}/{id}")).GetProperty("isEnabled").GetBoolean().ShouldBeFalse();
        await org.Admin.PostJsonAsync($"{RulesPath}/{id}/disable", null, HttpStatusCode.NoContent);
        await org.Admin.PostJsonAsync($"{RulesPath}/{id}/enable", null, HttpStatusCode.NoContent);
        (await org.Admin.GetJsonAsync($"{RulesPath}/{id}")).GetProperty("isEnabled").GetBoolean().ShouldBeTrue();

        await org.Admin.DeleteJsonAsync($"{RulesPath}/{id}");
        await (await org.Admin.GetAsync($"{RulesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await org.Admin.DeleteAsync($"{RulesPath}/{id}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await org.Admin.PostRawAsync($"{RulesPath}/{id}/enable")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        (await org.Admin.GetJsonAsync(RulesPath)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Create_CanStartDisabled_AndSeveralRulesOfTheSameKindCoexist()
    {
        var org = await factory.NewOrgAsync("Rules Multi");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");

        var off = await org.CreateRuleAsync(LeadRule(role, "Kapali", isEnabled: false));
        var on = await org.CreateRuleAsync(LeadRule(role, "Acik"));

        off.GetProperty("isEnabled").GetBoolean().ShouldBeFalse();
        on.GetProperty("isEnabled").GetBoolean().ShouldBeTrue();
        (await org.Admin.GetJsonAsync(RulesPath)).EnumerateArray().Select(r => r.Str("name")).ShouldBe(["Kapali", "Acik"]);
    }

    [Fact]
    public async Task Validation_ReportsFieldErrors_UnderParamsPrefix()
    {
        var org = await factory.NewOrgAsync("Rules Validation");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        var client = org.Admin;

        await (await client.PostAsJsonAsync(RulesPath, new { kind = "leadAssignment", @params = new { assigneeRoleId = role } }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await client.PostAsJsonAsync(RulesPath, new { name = new string('x', 201), kind = "leadAssignment", @params = new { assigneeRoleId = role } }, Ct)).ShouldBeValidationErrorAsync("name");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", @params = new { assigneeRoleId = role } }, Ct)).ShouldBeValidationErrorAsync("kind");
        (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "sendEmail", @params = new { } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "leadAssignment", @params = new { } }, Ct)).ShouldBeValidationErrorAsync("params.assigneeRoleId");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "leadAssignment" }, Ct)).ShouldBeValidationErrorAsync("params.assigneeRoleId");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "leadAssignment", @params = new { assigneeRoleId = role, followUpHours = 0 } }, Ct)).ShouldBeValidationErrorAsync("params.followUpHours");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "leadAssignment", @params = new { assigneeRoleId = role, followUpHours = 721 } }, Ct)).ShouldBeValidationErrorAsync("params.followUpHours");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "leadAssignment", @params = new { assigneeRoleId = role, sources = new[] { "fax" } } }, Ct)).ShouldBeValidationErrorAsync("params.sources");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "dealApproval", @params = new { approverRoleId = role } }, Ct)).ShouldBeValidationErrorAsync("params.minAmount");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "dealApproval", @params = new { minAmount = 0, approverRoleId = role } }, Ct)).ShouldBeValidationErrorAsync("params.minAmount");
        await (await client.PostAsJsonAsync(RulesPath, new { name = "x", kind = "dealApproval", @params = new { minAmount = 10 } }, Ct)).ShouldBeValidationErrorAsync("params.approverRoleId");

        // Aynı doğrulama güncellemede de geçerli.
        var rule = await org.CreateRuleAsync(LeadRule(role));
        await (await client.PutAsJsonAsync($"{RulesPath}/{rule.Id()}", new { name = "y", kind = "dealApproval", @params = new { minAmount = -1, approverRoleId = role } }, Ct)).ShouldBeValidationErrorAsync("params.minAmount");
        (await client.GetJsonAsync($"{RulesPath}/{rule.Id()}")).Str("kind").ShouldBe("leadAssignment", "geçersiz güncelleme hiçbir şeyi değiştirmez");
    }

    [Fact]
    public async Task UnknownRole_IsRejected_IncludingARoleOfAnotherOrganization()
    {
        var orgA = await factory.NewOrgAsync("Rules Role A");
        var orgB = await factory.NewOrgAsync("Rules Role B");
        var roleOfB = await orgB.CreateRoleAsync("B rolu", "crm.leads.read");

        await (await orgA.Admin.PostAsJsonAsync(RulesPath, LeadRule(Guid.NewGuid()), Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "workflow.role_not_found");
        await (await orgA.Admin.PostAsJsonAsync(RulesPath, LeadRule(roleOfB), Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "workflow.role_not_found");
        await (await orgA.Admin.PostAsJsonAsync(RulesPath, DealRule(roleOfB, 100), Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "workflow.role_not_found");

        var own = await orgA.CreateRoleAsync("A rolu", "crm.leads.read");
        var rule = await orgA.CreateRuleAsync(LeadRule(own));
        await (await orgA.Admin.PutAsJsonAsync($"{RulesPath}/{rule.Id()}", new { name = "x", kind = "leadAssignment", @params = new { assigneeRoleId = roleOfB } }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "workflow.role_not_found");
        (await orgA.Admin.GetJsonAsync(RulesPath)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task RulesAndExecutions_RequireManagePermission()
    {
        var org = await factory.NewOrgAsync("Rules Permissions");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        var rule = await org.CreateRuleAsync(LeadRule(role));
        var member = await factory.AddMemberWithPermissionsAsync(org, "Sadece Okur", "crm.leads.read", "crm.approvals.decide");
        var client = member.Client;

        await (await client.GetAsync(RulesPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.GetAsync($"{RulesPath}/{rule.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.PostAsJsonAsync(RulesPath, LeadRule(role), Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.PutAsJsonAsync($"{RulesPath}/{rule.Id()}", new { name = "x", kind = "leadAssignment", @params = new { assigneeRoleId = role } }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.DeleteAsync($"{RulesPath}/{rule.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.PostRawAsync($"{RulesPath}/{rule.Id()}/enable")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.PostRawAsync($"{RulesPath}/{rule.Id()}/disable")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.GetAsync(ExecutionsPath, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.GetAsync($"{ExecutionsPath}/{Guid.NewGuid()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.PostRawAsync($"{ExecutionsPath}/{Guid.NewGuid()}/terminate")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await client.PostRawAsync($"{ExecutionsPath}/{Guid.NewGuid()}/retry")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Kimliksiz istek 401.
        (await factory.CreateClient().GetAsync(RulesPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.CreateClient().GetAsync($"{ApprovalsPath}/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SystemRoles_AdministratorGetsBothNewPermissions_StandardGetsNeither()
    {
        var org = await factory.NewOrgAsync("Rules SystemRoles");
        var roles = await org.Admin.GetJsonAsync($"{Base}/organization/roles");
        var system = roles.EnumerateArray().Where(r => r.GetProperty("isSystem").GetBoolean()).ToList();
        var admin = system.Single(r => r.GetProperty("permissions").EnumerateArray().Any(p => p.GetString() == "org.roles.manage"));
        var standard = system.Single(r => r.Id() != admin.Id());

        string[] added = ["org.workflows.manage", "crm.approvals.decide"];
        var adminPermissions = admin.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        var standardPermissions = standard.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        adminPermissions.ShouldContain(added[0]);
        adminPermissions.ShouldContain(added[1]);
        standardPermissions.ShouldNotContain(added[0]);
        standardPermissions.ShouldNotContain(added[1]);
        standardPermissions.ShouldContain("crm.leads.write", "Standard diğer crm.* izinlerini korur");

        var catalog = (await org.Admin.GetJsonAsync($"{Base}/permissions")).EnumerateArray().Select(p => (p.Str("key"), p.Str("group"))).ToList();
        catalog.ShouldContain(("org.workflows.manage", "org"));
        catalog.ShouldContain(("crm.approvals.decide", "crm"));
    }

    [Fact]
    public async Task RuleChanges_AreAuditLogged()
    {
        var org = await factory.NewOrgAsync("Rules Audit");
        var role = await org.CreateRoleAsync("Satis", "crm.leads.read");
        var rule = await org.CreateRuleAsync(LeadRule(role, "Denetlenen"));
        await org.Admin.PostJsonAsync($"{RulesPath}/{rule.Id()}/disable", null, HttpStatusCode.NoContent);
        await org.Admin.DeleteJsonAsync($"{RulesPath}/{rule.Id()}");

        var audit = await org.Admin.GetJsonAsync($"{Base}/audit?entityType=WorkflowRule&entityId={rule.Id()}");
        audit.GetProperty("items").EnumerateArray().Select(i => i.Str("action")).ShouldBe(["deleted", "updated", "created"]);
        audit.GetRawText().ShouldContain("Denetlenen");
    }

    [Fact]
    public async Task CrossTenant_RulesAreInvisibleAndImmutableFromAnotherOrganization()
    {
        var orgA = await factory.NewOrgAsync("Rules Isolation A");
        var orgB = await factory.NewOrgAsync("Rules Isolation B");
        var roleA = await orgA.CreateRoleAsync("A", "crm.leads.read");
        var ruleA = await orgA.CreateRuleAsync(LeadRule(roleA, "Sadece A"));
        var roleB = await orgB.CreateRoleAsync("B", "crm.leads.read");

        (await orgB.Admin.GetJsonAsync(RulesPath)).GetArrayLength().ShouldBe(0);
        await (await orgB.Admin.GetAsync($"{RulesPath}/{ruleA.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await orgB.Admin.PutAsJsonAsync($"{RulesPath}/{ruleA.Id()}", new { name = "hack", kind = "leadAssignment", @params = new { assigneeRoleId = roleB } }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await orgB.Admin.DeleteAsync($"{RulesPath}/{ruleA.Id()}", Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await orgB.Admin.PostRawAsync($"{RulesPath}/{ruleA.Id()}/disable")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        var still = await orgA.Admin.GetJsonAsync($"{RulesPath}/{ruleA.Id()}");
        (still.Str("name"), still.GetProperty("isEnabled").GetBoolean()).ShouldBe(("Sadece A", true));
        (await orgB.Admin.GetJsonAsync($"{Base}/audit?entityType=WorkflowRule&entityId={ruleA.Id()}")).GetProperty("total").GetInt64().ShouldBe(0);
    }
}
