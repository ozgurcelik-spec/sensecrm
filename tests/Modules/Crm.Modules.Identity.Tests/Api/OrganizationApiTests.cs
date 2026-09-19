using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Identity.Tests.Api;

/// <summary>Organizasyon ayarları, üyeler, roller (RBAC), denetim kaydı ve kiracılar arası izolasyon.</summary>
[Collection(ApiCollection.Name)]
public sealed class OrganizationApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task StandardMember_GetsForbidden_OnRoleManagement()
    {
        var admin = await NewOrganizationAsync("Rbac Org");
        var standardRoleId = await AuthApiTests.RoleIdAsync(admin, "Standard");
        var memberEmail = UniqueEmail("standard");
        (await admin.PostAsJsonAsync($"{Base}/organization/members", new { email = memberEmail, displayName = "Std User", password = DefaultPassword, roleId = standardRoleId }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var member = factory.CreateClient().WithToken((await factory.CreateClient().LoginAsync(memberEmail)).AccessToken);

        // org.users.read var: listeleyebilir.
        (await member.GetAsync($"{Base}/organization/roles", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await member.GetAsync($"{Base}/organization/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // org.roles.manage / org.users.manage / org.settings.manage / org.audit.read yok.
        await (await member.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Hacker", permissions = new[] { "org.roles.manage" } }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await member.PutAsJsonAsync($"{Base}/organization/roles/{standardRoleId}", new { name = "X", permissions = Array.Empty<string>() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await member.DeleteAsync($"{Base}/organization/roles/{standardRoleId}", Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await member.PutAsJsonAsync($"{Base}/organization", new { name = "X", defaultLocale = "tr", timeZone = "Europe/Istanbul" }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await member.GetAsync($"{Base}/organization/audit", Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Roles_CreateUpdateDelete_WithBusinessRules()
    {
        var admin = await NewOrganizationAsync("Roles Org");

        var created = await admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Sales Rep", permissions = new[] { "crm.leads.read", "crm.leads.write" } }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var role = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var roleId = role.GetProperty("id").GetGuid();
        role.GetProperty("isSystem").GetBoolean().ShouldBeFalse();
        role.GetProperty("memberCount").GetInt32().ShouldBe(0);

        (await admin.PutAsJsonAsync($"{Base}/organization/roles/{roleId}", new { name = "Sales", permissions = new[] { "crm.leads.read" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await (await admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Bad", permissions = new[] { "crm.unknown.read" } }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        var administratorId = await AuthApiTests.RoleIdAsync(admin, "Administrator");
        await (await admin.DeleteAsync($"{Base}/organization/roles/{administratorId}", Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "role.system_readonly");
        await (await admin.PutAsJsonAsync($"{Base}/organization/roles/{administratorId}", new { name = "Admin2", permissions = Array.Empty<string>() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "role.system_readonly");

        (await admin.PostAsJsonAsync($"{Base}/organization/members", new { email = UniqueEmail("sales"), displayName = "Sales", password = DefaultPassword, roleId }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await (await admin.DeleteAsync($"{Base}/organization/roles/{roleId}", Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "role.in_use");

        var unused = await (await admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Temp", permissions = Array.Empty<string>() }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        (await admin.DeleteAsync($"{Base}/organization/roles/{unused.GetProperty("id").GetGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Members_Add_Update_AndLastAdministratorGuard()
    {
        var admin = await NewOrganizationAsync("Members Org");
        var me = await admin.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        var adminUserId = me.GetProperty("user").GetProperty("id").GetGuid();
        var standardRoleId = await AuthApiTests.RoleIdAsync(admin, "Standard");

        var email = UniqueEmail("member");
        var added = await admin.PostAsJsonAsync($"{Base}/organization/members", new { email, displayName = "New Member", password = DefaultPassword, roleId = standardRoleId }, Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created);
        var member = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
        member.GetProperty("email").GetString().ShouldBe(email);
        member.GetProperty("displayName").GetString().ShouldBe("New Member");
        member.GetProperty("roleName").GetString().ShouldBe("Standard");
        member.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        var memberUserId = member.GetProperty("userId").GetGuid();

        await (await admin.PostAsJsonAsync($"{Base}/organization/members", new { email, roleId = standardRoleId }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "member.exists");
        await (await admin.PostAsJsonAsync($"{Base}/organization/members", new { email = UniqueEmail("nopass"), displayName = "No Pass", roleId = standardRoleId }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // Tek aktif yönetici kendini pasifleştiremez / rolünü düşüremez.
        await (await admin.PatchAsJsonAsync($"{Base}/organization/members/{adminUserId}", new { isActive = false }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "member.last_admin");
        await (await admin.PatchAsJsonAsync($"{Base}/organization/members/{adminUserId}", new { roleId = standardRoleId }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity, "member.last_admin");

        // Üye pasifleştirilince oturum açamaz (aktif organizasyonu kalmaz).
        (await admin.PatchAsJsonAsync($"{Base}/organization/members/{memberUserId}", new { isActive = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var members = await admin.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct);
        members.EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == memberUserId).GetProperty("isActive").GetBoolean().ShouldBeFalse();
        await (await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/login", new { email, password = DefaultPassword }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.no_active_organization");
    }

    [Fact]
    public async Task Organization_UpdateAndAudit()
    {
        var admin = await NewOrganizationAsync("Audit Org");

        (await admin.PutAsJsonAsync($"{Base}/organization", new { name = "Audit Org Renamed", defaultLocale = "en", timeZone = "Europe/London" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var organization = await admin.GetFromJsonAsync<JsonElement>($"{Base}/organization", Ct);
        organization.GetProperty("name").GetString().ShouldBe("Audit Org Renamed");
        organization.GetProperty("defaultLocale").GetString().ShouldBe("en");
        organization.GetProperty("timeZone").GetString().ShouldBe("Europe/London");

        await (await admin.PutAsJsonAsync($"{Base}/organization", new { name = "X", defaultLocale = "tr", timeZone = "Mars/Olympus" }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        (await admin.PostAsJsonAsync($"{Base}/organization/roles", new { name = "Audited", permissions = new[] { "crm.deals.read" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var page = await admin.GetFromJsonAsync<JsonElement>($"{Base}/organization/audit?page=1&pageSize=50", Ct);
        page.GetProperty("total").GetInt64().ShouldBeGreaterThanOrEqualTo(4); // 2 sistem rolü + üyelik (kayıt) + org güncelleme + rol
        var items = page.GetProperty("items").EnumerateArray().ToList();

        var roleCreated = items.First(i => i.GetProperty("entityType").GetString() == "Role" && i.GetProperty("action").GetString() == "created");
        roleCreated.GetProperty("changes").GetProperty("name").GetProperty("new").GetString().ShouldBe("Audited");
        roleCreated.GetProperty("userDisplayName").GetString().ShouldBe("Test Audit Org");
        roleCreated.TryGetProperty("userId", out _).ShouldBeTrue();

        var orgUpdated = items.First(i => i.GetProperty("entityType").GetString() == "Tenant" && i.GetProperty("action").GetString() == "updated");
        var nameChange = orgUpdated.GetProperty("changes").GetProperty("name");
        nameChange.GetProperty("old").GetString().ShouldBe("Audit Org");
        nameChange.GetProperty("new").GetString().ShouldBe("Audit Org Renamed");

        // En yeni önce.
        var times = items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        times.ShouldBe(times.OrderByDescending(t => t).ToList());

        var paged = await admin.GetFromJsonAsync<JsonElement>($"{Base}/organization/audit?page=2&pageSize=2", Ct);
        paged.GetProperty("items").GetArrayLength().ShouldBeLessThanOrEqualTo(2);
        paged.GetProperty("total").GetInt64().ShouldBe(page.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task CrossTenantIsolation_AdminOfA_CannotSeeOrModify_OrganizationB()
    {
        var adminA = await NewOrganizationAsync("Tenant A");
        var adminB = await NewOrganizationAsync("Tenant B");

        var meB = await adminB.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        var adminBUserId = meB.GetProperty("user").GetProperty("id").GetGuid();
        var roleB = await (await adminB.PostAsJsonAsync($"{Base}/organization/roles", new { name = "B Only Role", permissions = new[] { "crm.leads.read" } }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var roleBId = roleB.GetProperty("id").GetGuid();
        var standardB = await AuthApiTests.RoleIdAsync(adminB, "Standard");

        // Listeler yalnız A'yı içerir.
        var membersA = await adminA.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct);
        membersA.GetArrayLength().ShouldBe(1);
        membersA.EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()).ShouldNotContain(adminBUserId);
        var rolesA = await adminA.GetFromJsonAsync<JsonElement>($"{Base}/organization/roles", Ct);
        rolesA.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldNotContain(roleBId);
        rolesA.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldNotContain(standardB);

        // B'nin kimlikleriyle değişiklik denemeleri bulunamaz (varlık sızdırılmaz).
        await (await adminA.PatchAsJsonAsync($"{Base}/organization/members/{adminBUserId}", new { isActive = false }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.PutAsJsonAsync($"{Base}/organization/roles/{roleBId}", new { name = "Hijacked", permissions = Array.Empty<string>() }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await adminA.DeleteAsync($"{Base}/organization/roles/{roleBId}", Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // A, B'nin rolünü kendi üyesine atayamaz.
        await (await adminA.PostAsJsonAsync($"{Base}/organization/members", new { email = UniqueEmail("cross"), displayName = "Cross", password = DefaultPassword, roleId = roleBId }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Denetim kaydı yalnız A'nın kayıtları.
        var auditA = await adminA.GetFromJsonAsync<JsonElement>($"{Base}/organization/audit?page=1&pageSize=200", Ct);
        var entityIds = auditA.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()).ToList();
        entityIds.ShouldNotContain(roleBId.ToString());
        entityIds.ShouldNotContain(standardB.ToString());

        // B tarafında hiçbir şey değişmedi.
        var roleBAfter = (await adminB.GetFromJsonAsync<JsonElement>($"{Base}/organization/roles", Ct)).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == roleBId);
        roleBAfter.GetProperty("name").GetString().ShouldBe("B Only Role");
        var adminBMember = (await adminB.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct)).EnumerateArray().Single();
        adminBMember.GetProperty("isActive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Permissions_Catalog_ReturnsKeysWithGroups()
    {
        var admin = await NewOrganizationAsync("Catalog Org");

        var permissions = await admin.GetFromJsonAsync<JsonElement>($"{Base}/permissions", Ct);

        var list = permissions.EnumerateArray().Select(p => (Key: p.GetProperty("key").GetString()!, Group: p.GetProperty("group").GetString()!)).ToList();
        list.Count.ShouldBe(16);
        list.ShouldAllBe(p => p.Group == "org" || p.Group == "crm");
        list.ShouldContain(("org.users.read", "org"));
        list.ShouldContain(("crm.reports.read", "crm"));
    }

    [Fact]
    public async Task Me_Patch_UpdatesDisplayNameAndLocale()
    {
        var admin = await NewOrganizationAsync("Profile Org");

        (await admin.PatchAsJsonAsync($"{Base}/me", new { displayName = "Yeni Ad", locale = "en" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await admin.PatchAsJsonAsync($"{Base}/me", new { locale = "fr" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        var me = await admin.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        me.GetProperty("user").GetProperty("displayName").GetString().ShouldBe("Yeni Ad");
        me.GetProperty("user").GetProperty("locale").GetString().ShouldBe("en");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> NewOrganizationAsync(string name)
    {
        var client = factory.CreateClient();
        var auth = await client.SignUpAsync(name, UniqueEmail("admin"));
        return client.WithToken(auth.AccessToken);
    }
}
