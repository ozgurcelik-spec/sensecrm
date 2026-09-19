using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Modules.Identity.Application;
using Crm.Tests.Shared.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Identity.Tests.Api;

/// <summary>
/// H4 — hesap modeli: yönetici parola seçmez (sunucu üretimi geçici parola + zorunlu değiştirme), mevcut hesap davetle (bekleyen üyelik)
/// katılır, başka organizasyonun hesap verisi sızmaz, parola değiştirme tüm diğer oturumları kapatır, parola politikası.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountModelApiTests(CrmApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- (a) geçici parola + zorunlu değiştirme ----------------------------------------------------------------------

    [Fact]
    public async Task NewAccount_GetsTemporaryPassword_AndMustChangeItBeforeAnythingElse()
    {
        var admin = await NewOrgAsync("Temp Pw Org");
        var standard = await AuthApiTests.RoleIdAsync(admin, "Standard");
        var email = UniqueEmail("newbie");

        var added = await admin.PostAsJsonAsync($"{Base}/organization/members", new { email, displayName = "Newbie", roleId = standard }, Ct);
        var body = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, body.ToString());
        var temporary = body.GetProperty("temporaryPassword").GetString()!;
        body.TryGetProperty("userId", out _).ShouldBeTrue();

        var login = await factory.CreateClient().LoginAsync(email, temporary);
        login.MustChangePassword.ShouldBeTrue();
        var client = factory.CreateClient().WithToken(login.AccessToken);

        // Yalnız GET /me, POST /me/password, GET /me/invitations*, POST /auth/logout çalışır; gerisi 403 auth.password_change_required.
        var me = await client.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct);
        me.GetProperty("user").GetProperty("mustChangePassword").GetBoolean().ShouldBeTrue();
        (await client.GetAsync($"{Base}/me/invitations", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var url in new[] { $"{Base}/organization", $"{Base}/organization/members", $"{Base}/permissions", $"{Base}/leads", $"{Base}/approvals/summary", $"{Base}/activities/summary" })
        {
            await (await client.GetAsync(url, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.password_change_required");
        }

        await (await client.PatchAsJsonAsync($"{Base}/me", new { displayName = "X" }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.password_change_required");
        await (await client.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = Guid.NewGuid() }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.password_change_required");

        // Çıkış (anonim) serbest.
        (await client.PostAsJsonAsync($"{Base}/auth/logout", new { refreshToken = login.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ChangePassword_ClearsTheFlag_ReturnsNewSession_AndRevokesEveryOtherSession()
    {
        var admin = await NewOrgAsync("Change Pw Org");
        var standard = await AuthApiTests.RoleIdAsync(admin, "Standard");
        var email = UniqueEmail("changer");
        var added = await (await admin.PostAsJsonAsync($"{Base}/organization/members", new { email, displayName = "Changer", roleId = standard }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var temporary = added.GetProperty("temporaryPassword").GetString()!;

        var first = await factory.CreateClient().LoginAsync(email, temporary);
        var otherDevice = await factory.CreateClient().LoginAsync(email, temporary); // ikinci oturum (başka cihaz)
        var client = factory.CreateClient().WithToken(first.AccessToken);

        // Yanlış mevcut parola → 401 auth.invalid_credentials; politika ihlali → 400 validation (alan newPassword).
        await (await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = "wrong-password-1", newPassword = "Yeni.Parola.2026" }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        var weak = await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = temporary, newPassword = "kisa" }, Ct);
        await weak.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        (await weak.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("newPassword", out _).ShouldBeTrue();

        var changed = await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = temporary, newPassword = "Yeni.Parola.2026" }, Ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        var session = (await changed.Content.ReadFromJsonAsync<AuthResponse>(Ct))!;
        session.MustChangePassword.ShouldBeFalse();

        // Yeni oturum tam yetkili; eski geçici parola geçersiz; DİĞER tüm oturumların refresh token'ları iptal.
        var fresh = factory.CreateClient().WithToken(session.AccessToken);
        (await fresh.GetAsync($"{Base}/organization/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fresh.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct)).GetProperty("user").GetProperty("mustChangePassword").GetBoolean().ShouldBeFalse();
        await (await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/login", new { email, password = temporary }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        await (await AuthApiTests.RefreshAsync(factory.CreateClient(), first.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        await (await AuthApiTests.RefreshAsync(factory.CreateClient(), otherDevice.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        (await AuthApiTests.RefreshAsync(factory.CreateClient(), session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.CreateClient().LoginAsync(email, "Yeni.Parola.2026")).MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangePassword_RequiresAuthentication_AndCorrectCurrentPassword_AndBlocksBruteForce()
    {
        (await factory.CreateClient().PostAsJsonAsync($"{Base}/me/password", new { currentPassword = "a", newPassword = "b" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var email = UniqueEmail("brute");
        var auth = await factory.CreateClient().SignUpAsync("Brute Org", email);
        var client = factory.CreateClient().WithToken(auth.AccessToken);
        for (var i = 0; i < 5; i++)
        {
            await (await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = "wrong-guess-" + i, newPassword = "Yeni.Parola.2026" }, Ct))
                .ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        // Aynı IP + hesap için eşik aşıldı: doğru parola bile artık denenmez (429), kaba kuvvet oracle'ı kapanır.
        await (await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = DefaultPassword, newPassword = "Yeni.Parola.2026" }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");
    }

    // ---- (b)(c) mevcut e-posta: bekleyen davet; sızıntı yok -----------------------------------------------------------

    [Fact]
    public async Task ExistingAccount_BecomesAPendingInvitation_WithoutLeakingAccountData_AndOnlyTheOwnerCanAnswer()
    {
        var orgA = await NewOrgAsync("Invite A");
        var victimEmail = UniqueEmail("victim");
        var victim = await factory.CreateClient().SignUpAsync("Victim Home", victimEmail);
        var standard = await AuthApiTests.RoleIdAsync(orgA, "Standard");

        var added = await orgA.PostAsJsonAsync($"{Base}/organization/members", new { email = victimEmail, displayName = "Attacker Chosen Name", roleId = standard }, Ct);
        var body = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, body.ToString());
        (body.GetProperty("email").GetString(), body.GetProperty("roleName").GetString(), body.GetProperty("status").GetString()).ShouldBe((victimEmail, "Standard", "pending"));
        body.TryGetProperty("userId", out _).ShouldBeFalse("mevcut hesabın kimliği sızmaz");
        body.TryGetProperty("temporaryPassword", out _).ShouldBeFalse();
        body.TryGetProperty("displayName", out _).ShouldBeFalse("başka organizasyondaki hesabın adı sızmaz");

        // Listede yalnız e-posta + durum (ad boş, userId yerine davet kimliği); etkin üye gibi yetki vermez.
        var members = (await orgA.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct)).EnumerateArray().ToList();
        var pending = members.Single(m => m.GetProperty("status").GetString() == "pending");
        (pending.GetProperty("email").GetString(), pending.GetProperty("displayName").GetString(), pending.GetProperty("roleName").GetString()).ShouldBe((victimEmail, string.Empty, "Standard"));
        pending.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        pending.TryGetProperty("invitedAt", out _).ShouldBeTrue();
        members.Count(m => m.GetProperty("status").GetString() == "active").ShouldBe(1);

        // Aynı davet tekrar açılamaz; bekleyen üyelik PATCH ile değiştirilemez.
        await (await orgA.PostAsJsonAsync($"{Base}/organization/members", new { email = victimEmail, roleId = standard }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Conflict, "member.exists");
        await (await orgA.PatchAsJsonAsync($"{Base}/organization/members/{pending.GetProperty("userId").GetGuid()}", new { isActive = true }, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Yalnız hesap sahibi görür; aktif organizasyon sayılmaz.
        var victimClient = factory.CreateClient().WithToken(victim.AccessToken);
        var invitations = (await victimClient.GetFromJsonAsync<JsonElement>($"{Base}/me/invitations", Ct)).EnumerateArray().ToList();
        var invitation = invitations.Single();
        (invitation.GetProperty("organizationName").GetString(), invitation.GetProperty("roleName").GetString()).ShouldBe(("Invite A", "Standard"));
        invitation.TryGetProperty("invitedAt", out _).ShouldBeTrue();
        invitation.GetProperty("id").GetGuid().ShouldBe(pending.GetProperty("userId").GetGuid());
        (await victimClient.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct)).GetProperty("organizations").GetArrayLength().ShouldBe(1);

        // Başka bir kullanıcı ne görür ne kabul/red edebilir (404 invitation.not_found; varlık sızdırılmaz); anonim 401.
        var stranger = factory.CreateClient().WithToken((await factory.CreateClient().SignUpAsync("Stranger Org", UniqueEmail("stranger"))).AccessToken);
        (await stranger.GetFromJsonAsync<JsonElement>($"{Base}/me/invitations", Ct)).GetArrayLength().ShouldBe(0);
        await (await stranger.PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/accept", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "invitation.not_found");
        await (await stranger.PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/decline", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "invitation.not_found");
        await (await orgA.PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/accept", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "invitation.not_found");
        (await factory.CreateClient().PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await victimClient.GetFromJsonAsync<JsonElement>($"{Base}/me/invitations", Ct)).GetArrayLength().ShouldBe(1, "başkasının denemeleri daveti etkilemez");

        // Hesap sahibi kabul eder → etkin üye ve geçilebilir organizasyon; davet listeden düşer.
        (await victimClient.PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await victimClient.GetFromJsonAsync<JsonElement>($"{Base}/me/invitations", Ct)).GetArrayLength().ShouldBe(0);
        var orgAId = (await victimClient.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct)).GetProperty("organizations").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "Invite A").GetProperty("id").GetGuid();
        var switched = await victimClient.PostAsJsonAsync($"{Base}/auth/switch-organization", new { organizationId = orgAId }, Ct);
        switched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var inA = factory.CreateClient().WithToken((await switched.Content.ReadFromJsonAsync<AuthResponse>(Ct))!.AccessToken);
        (await inA.GetFromJsonAsync<JsonElement>($"{Base}/me", Ct)).GetProperty("role").GetProperty("name").GetString().ShouldBe("Standard");
        (await orgA.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct)).EnumerateArray().Count(m => m.GetProperty("status").GetString() == "active").ShouldBe(2);

        // İkinci kabul: davet artık bekleyen değil → 404.
        await (await victimClient.PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/accept", null, Ct)).ShouldBeProblemAsync(HttpStatusCode.NotFound, "invitation.not_found");
    }

    [Fact]
    public async Task DecliningAnInvitation_RemovesIt_AndAllowsANewOne()
    {
        var org = await NewOrgAsync("Decline Org");
        var email = UniqueEmail("decliner");
        var user = factory.CreateClient().WithToken((await factory.CreateClient().SignUpAsync("Decliner Home", email)).AccessToken);
        var standard = await AuthApiTests.RoleIdAsync(org, "Standard");

        (await org.PostAsJsonAsync($"{Base}/organization/members", new { email, roleId = standard }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var invitation = (await user.GetFromJsonAsync<JsonElement>($"{Base}/me/invitations", Ct)).EnumerateArray().Single();
        (await user.PostAsync($"{Base}/me/invitations/{invitation.GetProperty("id").GetGuid()}/decline", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await user.GetFromJsonAsync<JsonElement>($"{Base}/me/invitations", Ct)).GetArrayLength().ShouldBe(0);
        (await org.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct)).GetArrayLength().ShouldBe(1);
        (await org.PostAsJsonAsync($"{Base}/organization/members", new { email, roleId = standard }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task InvitationsOfAnotherTenant_AreNotVisibleToAnyOtherOrganization()
    {
        var orgA = await NewOrgAsync("Vis A");
        var orgB = await NewOrgAsync("Vis B");
        var email = UniqueEmail("shared");
        await factory.CreateClient().SignUpAsync("Shared Home", email);

        (await orgA.PostAsJsonAsync($"{Base}/organization/members", new { email, roleId = await AuthApiTests.RoleIdAsync(orgA, "Standard") }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // B'nin üye listesinde A'nın daveti yok.
        (await orgB.GetFromJsonAsync<JsonElement>($"{Base}/organization/members", Ct)).EnumerateArray().Select(m => m.GetProperty("email").GetString()).ShouldNotContain(email);
    }

    // ---- (e) parola politikası ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kisa")]
    [InlineData("Ab1.234")]
    [InlineData("password123")]
    [InlineData("Password2026!")]
    [InlineData("qwertyuiop")]
    [InlineData("1234567890")]
    public async Task SignUp_RejectsWeakPasswords(string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/signup", new { organizationName = "Weak Org", displayName = "W", email = UniqueEmail("weak"), password, locale = "tr" }, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("password", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task SignUp_RejectsPasswordContainingTheEmailLocalPart_AndTooLongPasswords()
    {
        var local = "ozgurcelik" + Guid.NewGuid().ToString("N")[..6];
        var contains = await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/signup",
            new { organizationName = "Local Org", displayName = "L", email = $"{local}@example.com", password = $"Xx.{local.ToUpperInvariant()}.9", locale = "tr" }, Ct);
        await contains.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
        var message = (await contains.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").GetProperty("password").EnumerateArray().Select(e => e.GetString()).ToList();
        message.Any(m => m!.Contains("kullanıcı adı", StringComparison.Ordinal)).ShouldBeTrue(string.Join(" | ", message));

        await (await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/signup",
            new { organizationName = "Long Org", displayName = "L", email = UniqueEmail("long"), password = new string('a', 129), locale = "tr" }, Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");

        // 128 karakter sınırdadır ve kabul edilir; 10 karakter en kısa kabul edilendir.
        (await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/signup",
            new { organizationName = "Max Org", displayName = "M", email = UniqueEmail("max"), password = "Zq7!" + new string('k', 124), locale = "tr" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/signup",
            new { organizationName = "Min Org", displayName = "M", email = UniqueEmail("min"), password = "Zq7!wR2#xL", locale = "tr" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_AppliesTheSamePolicy_IncludingTheEmailLocalPart()
    {
        var local = "changepolicy" + Guid.NewGuid().ToString("N")[..6];
        var auth = await factory.CreateClient().SignUpAsync("Policy Org", $"{local}@example.com");
        var client = factory.CreateClient().WithToken(auth.AccessToken);

        foreach (var weak in new[] { "kisa", "password123", $"Zz.{local}.1" })
        {
            var response = await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = DefaultPassword, newPassword = weak }, Ct);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty("newPassword", out _).ShouldBeTrue(weak);
        }
    }

    [Fact]
    public async Task PlatformOrganization_RejectsWeakAdminPasswords()
    {
        var platformEmail = UniqueEmail("platform-policy");
        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<Application.Provisioning.PlatformAdminBootstrapper>()
                .EnsureAsync(platformEmail, "password123456", "P", null, Ct);
            result.Outcome.ShouldBe(Application.Provisioning.PlatformAdminOutcome.Invalid, "yaygın parola platform yöneticisi için de reddedilir");
        }
    }

    // ---- yardımcı ----------------------------------------------------------------------------------------------------

    private async Task<HttpClient> NewOrgAsync(string name)
    {
        var client = factory.CreateClient();
        var auth = await client.SignUpAsync(name, UniqueEmail("admin"));
        return client.WithToken(auth.AccessToken);
    }

}
