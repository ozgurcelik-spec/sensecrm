using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Modules.Identity.Application;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Identity.Tests.Api;

/// <summary>Kayıt, giriş, /me, token yenileme (rotasyon + yeniden kullanım tespiti), çıkış, organizasyon değiştirme.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthApiTests(CrmApiFactory factory)
{
    [Fact]
    public async Task SignUp_ThenMe_ReturnsAdministratorOfNewOrganization()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail("signup");

        var auth = await client.SignUpAsync("Acme Satış", email, locale: "en");
        auth.AccessToken.ShouldNotBeNullOrWhiteSpace();
        auth.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        auth.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(10));
        auth.ExpiresAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(16));

        var me = await client.WithToken(auth.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", TestContext.Current.CancellationToken);

        me.GetProperty("user").GetProperty("email").GetString().ShouldBe(email);
        me.GetProperty("user").GetProperty("locale").GetString().ShouldBe("en");
        me.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse();
        me.GetProperty("organization").GetProperty("name").GetString().ShouldBe("Acme Satış");
        me.GetProperty("organization").GetProperty("slug").GetString().ShouldStartWith("acme-satis");
        me.GetProperty("organization").GetProperty("defaultLocale").GetString().ShouldBe("en");
        me.GetProperty("organization").GetProperty("timeZone").GetString().ShouldBe("Europe/Istanbul");
        me.GetProperty("role").GetProperty("name").GetString().ShouldBe("Administrator");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        permissions.ShouldContain("org.roles.manage");
        permissions.ShouldContain("crm.deals.write");
        me.GetProperty("organizations").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task SignUp_WithTakenEmail_ReturnsEmailTaken()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail("dup");
        await client.SignUpAsync("First Org", email);

        var response = await client.PostAsJsonAsync($"{Base}/auth/signup", new { organizationName = "Second", displayName = "X", email, password = DefaultPassword, locale = "tr" }, TestContext.Current.CancellationToken);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, "auth.email_taken");
    }

    [Fact]
    public async Task SignUp_WithInvalidInput_ReturnsValidationErrorsKeyedByCamelCaseField()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Base}/auth/signup", new { organizationName = "", displayName = "X", email = "not-an-email", password = "123", locale = "de" }, TestContext.Current.CancellationToken);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var errors = body.GetProperty("errors");
        errors.TryGetProperty("organizationName", out _).ShouldBeTrue();
        errors.TryGetProperty("email", out _).ShouldBeTrue();
        errors.TryGetProperty("password", out _).ShouldBeTrue();
        errors.TryGetProperty("locale", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Login_WithValidAndInvalidCredentials()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail("login");
        await client.SignUpAsync("Login Org", email);

        var ok = await client.LoginAsync(email.ToUpperInvariant());
        ok.AccessToken.ShouldNotBeNullOrWhiteSpace();

        var wrong = await client.PostAsJsonAsync($"{Base}/auth/login", new { email, password = "wrong-password" }, TestContext.Current.CancellationToken);
        await wrong.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");

        var unknown = await client.PostAsJsonAsync($"{Base}/auth/login", new { email = UniqueEmail("nobody"), password = DefaultPassword }, TestContext.Current.CancellationToken);
        await unknown.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [Fact]
    public async Task Refresh_RotatesTokens_AndReuseRevokesTheWholeFamily()
    {
        var client = factory.CreateClient();
        var first = await client.SignUpAsync("Refresh Org", UniqueEmail("refresh"));

        var rotated = await RefreshAsync(client, first.RefreshToken);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await rotated.Content.ReadFromJsonAsync<AuthResponse>(TestContext.Current.CancellationToken))!;
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        second.AccessToken.ShouldNotBe(first.AccessToken);

        // Eski (döndürülmüş) token tekrar kullanılırsa: reddedilir ve aile kapanır → yeni token da artık geçersiz.
        await (await RefreshAsync(client, first.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        await (await RefreshAsync(client, second.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var client = factory.CreateClient();
        var auth = await client.SignUpAsync("Logout Org", UniqueEmail("logout"));

        var logout = await client.PostAsJsonAsync($"{Base}/auth/logout", new { refreshToken = auth.RefreshToken }, TestContext.Current.CancellationToken);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await (await RefreshAsync(client, auth.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthenticatedProblem()
    {
        var response = await factory.CreateClient().GetAsync($"{Base}/me", TestContext.Current.CancellationToken);

        await response.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
    }

    [Fact]
    public async Task SwitchOrganization_IssuesTokenForTheOtherOrganization()
    {
        var userEmail = UniqueEmail("multi");
        var userClient = factory.CreateClient();
        var own = await userClient.SignUpAsync("Own Org", userEmail);

        // Başka bir organizasyonun yöneticisi mevcut hesabı Standard rolüyle üye olarak ekler (parola yok sayılır).
        var adminClient = factory.CreateClient();
        var admin = await adminClient.SignUpAsync("Other Org", UniqueEmail("other-admin"));
        adminClient.WithToken(admin.AccessToken);
        var standardRoleId = await RoleIdAsync(adminClient, "Standard");
        var add = await adminClient.PostAsJsonAsync($"{Base}/organization/members", new { email = userEmail, displayName = "Ignored", password = "ignored-password", roleId = standardRoleId }, TestContext.Current.CancellationToken);
        add.StatusCode.ShouldBe(HttpStatusCode.Created, await add.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var me = await userClient.WithToken(own.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", TestContext.Current.CancellationToken);
        var organizations = me.GetProperty("organizations").EnumerateArray().ToList();
        organizations.Count.ShouldBe(2);
        var otherOrgId = organizations.Single(o => o.GetProperty("name").GetString() == "Other Org").GetProperty("id").GetGuid();

        var switched = await userClient.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = otherOrgId }, TestContext.Current.CancellationToken);
        switched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var switchedAuth = (await switched.Content.ReadFromJsonAsync<AuthResponse>(TestContext.Current.CancellationToken))!;

        var meInOther = await factory.CreateClient().WithToken(switchedAuth.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", TestContext.Current.CancellationToken);
        meInOther.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(otherOrgId);
        meInOther.GetProperty("role").GetProperty("name").GetString().ShouldBe("Standard");
        meInOther.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldNotContain("org.roles.manage");

        // Sonraki girişte en son geçilen organizasyon açılır.
        var relogin = await factory.CreateClient().LoginAsync(userEmail);
        var meAfterLogin = await factory.CreateClient().WithToken(relogin.AccessToken).GetFromJsonAsync<JsonElement>($"{Base}/me", TestContext.Current.CancellationToken);
        meAfterLogin.GetProperty("organization").GetProperty("id").GetGuid().ShouldBe(otherOrgId);

        // Üyesi olmadığı bir organizasyona geçiş yasaktır.
        var foreign = await userClient.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        await foreign.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    internal static async Task<Guid> RoleIdAsync(HttpClient authorizedClient, string roleName)
    {
        var roles = await authorizedClient.GetFromJsonAsync<JsonElement>($"{Base}/organization/roles", TestContext.Current.CancellationToken);
        return roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == roleName).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync($"{Base}/auth/refresh", new { refreshToken }, TestContext.Current.CancellationToken);
}
