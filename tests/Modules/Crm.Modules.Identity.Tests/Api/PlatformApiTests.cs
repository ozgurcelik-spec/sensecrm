using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Application.Provisioning;
using Crm.Tests.Shared.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Identity.Tests.Api;

/// <summary>
/// Pilot yayın (M5): kayıt denetimi (<c>Registration:Mode</c>), <c>GET /auth/config</c>, platform yöneticisi ucu
/// (<c>POST /platform/organizations</c>), <c>create-platform-admin</c> mantığı ve Production başlangıç korumaları.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformApiTests(CrmApiFactory factory)
{
    private const string AdminPassword = "Platform.Sifre.12345";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- create-platform-admin (PlatformAdminBootstrapper) ------------------------------------------------------------

    [Fact]
    public async Task Bootstrap_CreatesPlatformAdmin_IsIdempotent_AndCanLogIn()
    {
        var email = UniqueEmail("boot");

        var first = await EnsureAdminAsync(email, AdminPassword);
        first.Outcome.ShouldBe(PlatformAdminOutcome.Created);

        // İkinci çalıştırma (farklı parola verilse bile) hiçbir şeyi değiştirmez.
        var second = await EnsureAdminAsync(email, "Baska.Parola.99999");
        second.Outcome.ShouldBe(PlatformAdminOutcome.Unchanged);

        var auth = await factory.CreateClient().LoginAsync(email, AdminPassword);
        var me = await factory.CreateClient().WithToken(auth.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        me.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();
        me.GetProperty("organization").GetProperty("name").GetString().ShouldBe("Platform");
        me.GetProperty("role").GetProperty("name").GetString().ShouldBe("Administrator");
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("crm.approvals.decide");
    }

    [Fact]
    public async Task Bootstrap_PromotesExistingAccount_WithoutChangingItsPassword()
    {
        var email = UniqueEmail("promote");
        await factory.CreateClient().SignUpAsync("Promoted Org", email);

        var result = await EnsureAdminAsync(email, password: null);
        result.Outcome.ShouldBe(PlatformAdminOutcome.Promoted);

        var auth = await factory.CreateClient().LoginAsync(email); // eski parola geçerli
        var me = await factory.CreateClient().WithToken(auth.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        me.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();
        me.GetProperty("organization").GetProperty("name").GetString().ShouldBe("Promoted Org");
    }

    [Theory]
    [InlineData("", AdminPassword)]
    [InlineData("not-an-email", AdminPassword)]
    [InlineData("valid@example.com", "short")]
    [InlineData("valid@example.com", null)]
    public async Task Bootstrap_RejectsInvalidInput_AndCreatesNothing(string email, string? password)
    {
        var result = await EnsureAdminAsync(email.Length == 0 ? null : email, password);

        result.Outcome.ShouldBe(PlatformAdminOutcome.Invalid);
        result.Problem.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- POST /platform/organizations --------------------------------------------------------------------------------

    [Fact]
    public async Task CreateOrganization_WithPassword_CreatesOrganizationAndWorkingAdmin()
    {
        var platform = await PlatformAdminClientAsync();
        var adminEmail = UniqueEmail("acme-admin");

        var response = await platform.PostAsJsonAsync($"{Base}/platform/organizations", NewOrgRequest("Acme Holding", adminEmail, AdminPassword, "en"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("name").GetString().ShouldBe("Acme Holding");
        body.GetProperty("slug").GetString().ShouldStartWith("acme-holding");
        body.GetProperty("adminEmail").GetString().ShouldBe(adminEmail);
        body.GetProperty("adminAccountCreated").GetBoolean().ShouldBeTrue();
        body.TryGetProperty("generatedPassword", out _).ShouldBeFalse("verilen parola yanıtta geri dönmez");

        var admin = await factory.CreateClient().LoginAsync(adminEmail, AdminPassword);
        var me = await factory.CreateClient().WithToken(admin.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        me.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(body.GetProperty("organizationId").GetGuid());
        me.GetProperty("organization").GetProperty("defaultLocale").GetString().ShouldBe("en");
        me.GetProperty("role").GetProperty("name").GetString().ShouldBe("Administrator");
        me.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task CreateOrganization_WithNullPassword_ReturnsGeneratedOneTimePassword()
    {
        var platform = await PlatformAdminClientAsync();
        var adminEmail = UniqueEmail("gen-admin");

        var response = await platform.PostAsJsonAsync($"{Base}/platform/organizations", NewOrgRequest("Generated Pw Org", adminEmail, password: null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var generated = body.GetProperty("generatedPassword").GetString();
        generated.ShouldNotBeNullOrWhiteSpace();
        generated.Length.ShouldBeGreaterThanOrEqualTo(16);

        (await factory.CreateClient().LoginAsync(adminEmail, generated)).AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateOrganization_WithExistingAccountEmail_OnlyAddsAdministratorMembership()
    {
        var existingEmail = UniqueEmail("existing");
        var own = await factory.CreateClient().SignUpAsync("Existing Home Org", existingEmail);
        var platform = await PlatformAdminClientAsync();

        var response = await platform.PostAsJsonAsync($"{Base}/platform/organizations", NewOrgRequest("Second Home Org", existingEmail, "ignored-password-1"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("adminAccountCreated").GetBoolean().ShouldBeFalse();
        body.TryGetProperty("generatedPassword", out _).ShouldBeFalse();

        // Hesabın parolası değişmedi ve artık iki organizasyonu var.
        var me = await factory.CreateClient().WithToken(own.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        me.GetProperty("organizations").EnumerateArray().Select(o => o.GetProperty("name").GetString())
            .ShouldBe(["Existing Home Org", "Second Home Org"], ignoreOrder: true);
        (await factory.CreateClient().LoginAsync(existingEmail)).AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateOrganization_WithInvalidInput_ReturnsValidationErrors()
    {
        var platform = await PlatformAdminClientAsync();

        var response = await platform.PostAsJsonAsync(
            $"{Base}/platform/organizations",
            new { organizationName = "", adminDisplayName = "", adminEmail = "nope", adminPassword = "123", locale = "de" },
            Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        foreach (var field in new[] { "organizationName", "adminDisplayName", "adminEmail", "adminPassword", "locale" })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue(field);
        }
    }

    [Fact]
    public async Task CreateOrganization_IsForbiddenForOrdinaryAdmins_AndUnauthorizedAnonymously()
    {
        var ordinary = await factory.CreateClient().SignUpAsync("Ordinary Org", UniqueEmail("ordinary"));
        var request = NewOrgRequest("Sneaky Org", UniqueEmail("sneaky"), AdminPassword);

        await (await factory.CreateClient().WithToken(ordinary.AccessToken).PostAsJsonAsync($"{Base}/platform/organizations", request, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await (await factory.CreateClient().PostAsJsonAsync($"{Base}/platform/organizations", request, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }

    [Fact]
    public async Task CreateOrganization_ChecksTheFlagInTheDatabase_NotOnlyTheToken()
    {
        var email = UniqueEmail("revoked");
        await EnsureAdminAsync(email, AdminPassword);
        var platform = factory.CreateClient().WithToken((await factory.CreateClient().LoginAsync(email, AdminPassword)).AccessToken);

        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("UPDATE identity.users SET is_platform_admin = false WHERE normalized_email = @email", connection);
            command.Parameters.AddWithValue("email", email.ToUpperInvariant());
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        // Token'da hâlâ platform bayrağı var, ama yetki geri alındı.
        await (await platform.PostAsJsonAsync($"{Base}/platform/organizations", NewOrgRequest("Revoked Org", UniqueEmail("rev-admin"), AdminPassword), Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task CreateOrganization_KeepsTenantsIsolated_FromEachOtherAndFromThePlatformOrganization()
    {
        var platform = await PlatformAdminClientAsync();
        var platformMe = await platform.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        var platformOrgId = platformMe.GetProperty("organization").GetProperty("id").GetGuid();
        var membersBefore = (await platform.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct)).GetArrayLength();

        var emailA = UniqueEmail("iso-a");
        var emailB = UniqueEmail("iso-b");
        var orgA = await (await platform.PostAsJsonAsync($"{Base}/platform/organizations", NewOrgRequest("Isolation A", emailA, AdminPassword), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var orgB = await (await platform.PostAsJsonAsync($"{Base}/platform/organizations", NewOrgRequest("Isolation B", emailB, AdminPassword), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var adminA = factory.CreateClient().WithToken((await factory.CreateClient().LoginAsync(emailA, AdminPassword)).AccessToken);
        var adminB = factory.CreateClient().WithToken((await factory.CreateClient().LoginAsync(emailB, AdminPassword)).AccessToken);

        // Her organizasyon yalnız kendi tek üyesini ve kendi 2 sistem rolünü görür.
        foreach (var (client, userId) in new[] { (adminA, orgA.GetProperty("adminUserId").GetGuid()), (adminB, orgB.GetProperty("adminUserId").GetGuid()) })
        {
            var members = await client.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct);
            members.EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()).ShouldBe([userId]);
            (await client.GetFromJsonAsync<JsonElement>($"{Base}/organization/roles", Ct)).GetArrayLength().ShouldBe(2);
        }

        // A'nın yöneticisi B'nin yöneticisini/rollerini/denetim kayıtlarını göremez ya da değiştiremez.
        var adminBUserId = orgB.GetProperty("adminUserId").GetGuid();
        await (await adminA.PatchAsJsonAsync($"{Base}/organization/members/{adminBUserId}", new { isActive = false }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        var rolesB = (await adminB.GetFromJsonAsync<JsonElement>($"{Base}/organization/roles", Ct)).EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        var auditA = await adminA.GetFromJsonAsync<JsonElement>($"{Base}/organization/audit?page=1&pageSize=100", Ct);
        auditA.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()).ShouldNotContain(id => rolesB.Contains(id));

        // Platform organizasyonu değişmedi: üye sayısı aynı, yeni organizasyonların verisi denetim kaydında yok.
        (await platform.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct)).GetArrayLength().ShouldBe(membersBefore);
        var platformAudit = await platform.GetFromJsonAsync<JsonElement>($"{Base}/organization/audit?page=1&pageSize=100", Ct);
        platformAudit.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()).ShouldNotContain(id => rolesB.Contains(id));
        (await platform.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct)).GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(platformOrgId);

        // Yeni organizasyonun kendi denetim kaydı var ve işlemi yapan platform yöneticisine atfedilir.
        var auditOfB = await adminB.GetFromJsonAsync<JsonElement>($"{Base}/organization/audit?page=1&pageSize=100", Ct);
        auditOfB.GetProperty("total").GetInt64().ShouldBeGreaterThan(0);
        auditOfB.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityId").GetString()).ShouldContain(rolesB[0]);
    }

    // ---- Registration:Mode + GET /auth/config ------------------------------------------------------------------------

    [Fact]
    public async Task AuthConfig_IsOpenInTesting_AndAnonymousAndCacheable()
    {
        var response = await factory.CreateClient().GetAsync($"{Base}/auth/config", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.Public.ShouldBeTrue();
        response.Headers.CacheControl?.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("signupEnabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Signup_WhenDisabled_Returns403_LocalizedForBothLanguages_EvenForInvalidBodies_AndConfigReportsIt()
    {
        await using var disabled = factory.WithWebHostBuilder(b => b.UseSetting("Registration:Mode", "disabled"));
        var client = disabled.CreateClient();

        var valid = await client.PostAsJsonAsync($"{Base}/auth/signup", new { organizationName = "Nope", displayName = "N", email = UniqueEmail("nope"), password = DefaultPassword, locale = "tr" }, Ct);
        await valid.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.signup_disabled");
        (await valid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("kayıt kapalı");

        using var english = new HttpRequestMessage(HttpMethod.Post, $"{Base}/auth/signup") { Content = JsonContent.Create(new { }) };
        english.Headers.AcceptLanguage.ParseAdd("en-US");
        var response = await client.SendAsync(english, Ct);
        await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.signup_disabled");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("sign-up is disabled");

        (await client.GetFromJsonAsync<JsonElement>($"{Base}/auth/config", Ct)).GetProperty("signupEnabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task WhenSignupIsDisabled_PlatformAdminStillCreatesOrganizations_AndTheirUsersLogIn()
    {
        var email = UniqueEmail("plat-disabled");
        await EnsureAdminAsync(email, AdminPassword);
        await using var disabled = factory.WithWebHostBuilder(b => b.UseSetting("Registration:Mode", "disabled"));
        var platform = disabled.CreateClient().WithToken((await disabled.CreateClient().LoginAsync(email, AdminPassword)).AccessToken);
        var adminEmail = UniqueEmail("dis-admin");

        var created = await platform.PostAsJsonAsync($"{Base}/platform/organizations", NewOrgRequest("Closed Registration Org", adminEmail, AdminPassword), Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        (await disabled.CreateClient().LoginAsync(adminEmail, AdminPassword)).AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData("", false, true)]
    [InlineData(null, true, false)]
    [InlineData("open", false, true)]
    [InlineData("OPEN", true, true)]
    [InlineData("disabled", false, false)]
    [InlineData(" Disabled ", false, false)]
    public void RegistrationPolicy_DefaultsByEnvironment_AndExplicitModeWins(string? mode, bool production, bool expectedEnabled)
    {
        // production=true -> Development/Testing değil (varsayılan kapalı); production=false -> Development/Testing (varsayılan açık).
        var policy = RegistrationPolicy.Resolve(new RegistrationOptions { Mode = mode }, isDevelopmentLike: !production);

        policy.SignupEnabled.ShouldBe(expectedEnabled);
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("disabled", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("closed", false)]
    [InlineData("true", false)]
    public void RegistrationModes_ValidatesConfiguredValue(string? mode, bool valid) => RegistrationModes.IsValid(mode).ShouldBe(valid);

    // ---- Production başlangıç korumaları -----------------------------------------------------------------------------

    [Fact]
    public void Production_RefusesToStart_WithoutSigningKey()
    {
        using var production = ProductionFactory(signingKeyPem: string.Empty, connectionString: factory.ConnectionString);

        var ex = Should.Throw<Exception>(() => production.CreateClient());

        Flatten(ex).ShouldContain("Auth:SigningKeyPem");
    }

    [Fact]
    public void Production_RefusesToStart_WithoutConnectionString()
    {
        using var production = ProductionFactory(signingKeyPem: NewSigningKeyPem(), connectionString: string.Empty);

        var ex = Should.Throw<Exception>(() => production.CreateClient());

        Flatten(ex).ShouldContain("ConnectionStrings:Database");
    }

    [Fact]
    public void Production_RefusesToStart_WithInvalidRegistrationMode()
    {
        using var production = ProductionFactory(NewSigningKeyPem(), factory.ConnectionString, registrationMode: "maybe");

        var ex = Should.Throw<Exception>(() => production.CreateClient());

        Flatten(ex).ShouldContain("Registration:Mode");
    }

    [Fact]
    public async Task Production_WithSigningKeyAndConnection_StartsWithSignupDisabledByDefault()
    {
        using var production = ProductionFactory(NewSigningKeyPem(), factory.ConnectionString);
        var client = production.CreateClient();

        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>($"{Base}/auth/config", Ct)).GetProperty("signupEnabled").GetBoolean().ShouldBeFalse();
        await (await client.PostAsJsonAsync($"{Base}/auth/signup", new { organizationName = "Prod Org", displayName = "P", email = UniqueEmail("prod"), password = DefaultPassword, locale = "tr" }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.signup_disabled");
    }

    // ---- yardımcılar -------------------------------------------------------------------------------------------------

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> ProductionFactory(string signingKeyPem, string connectionString, string? registrationMode = null) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:SigningKeyPem", signingKeyPem);
            b.UseSetting("ConnectionStrings:Database", connectionString);
            b.UseSetting("Registration:Mode", registrationMode ?? string.Empty);
        });

    private static string NewSigningKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException!)
        {
            messages.Add(e.Message);
            if (e.InnerException is null)
            {
                break;
            }
        }

        return string.Join(" | ", messages);
    }

    private async Task<PlatformAdminResult> EnsureAdminAsync(string? email, string? password)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapper>()
            .EnsureAsync(email, password, displayName: "Platform Yönetici", organizationName: null, Ct);
    }

    /// <summary>Yeni bir platform yöneticisi oluşturur (Migrator komutuyla aynı mantık) ve oturum açmış istemciyi döner.</summary>
    private async Task<HttpClient> PlatformAdminClientAsync()
    {
        var email = UniqueEmail("platform");
        (await EnsureAdminAsync(email, AdminPassword)).Outcome.ShouldBe(PlatformAdminOutcome.Created);
        var auth = await factory.CreateClient().LoginAsync(email, AdminPassword);
        return factory.CreateClient().WithToken(auth.AccessToken);
    }

    private static object NewOrgRequest(string name, string adminEmail, string? password, string locale = "tr") =>
        new { organizationName = name, adminDisplayName = "Yönetici " + name, adminEmail, adminPassword = password, locale };
}
