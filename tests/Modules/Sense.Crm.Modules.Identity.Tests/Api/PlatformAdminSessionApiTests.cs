using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Application.Provisioning;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Identity.Tests.Api;

/// <summary>
/// C-SEC2 M4 (platform yöneticisi oturumları): refresh token ailesinin mutlak ömrü (varsayılan 8 saat) ve boşta kalma süresi (refresh token ömrü, varsayılan 60 dk) yapılandırılabilir ve
/// yalnız platform yöneticisi için kısadır (normal kullanıcı: 30 gün). M6: bootstrap yöneticisi geçici parolalıdır; devre dışı bırakılan yönetici giriş/yenileme yapamaz.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformAdminSessionApiTests(CrmApiFactory factory)
{
    private const string AdminPassword = "Platform.Sifre.12345";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlatformAdminSessions_UseTheShortDefaults_AndOtherUsersKeepThirtyDays()
    {
        var (adminEmail, admin) = await NewPlatformAdminAsync(factory.Services, factory.CreateClient);
        var normalEmail = UniqueEmail("normal");
        await factory.CreateClient().SignUpAsync("Normal Org", normalEmail);

        var adminRow = await RefreshRowAsync(adminEmail);
        var normalRow = await RefreshRowAsync(normalEmail);

        admin.MustChangePassword.ShouldBeFalse("oturum parola değişiminden sonra açıldı");
        (adminRow.FamilyExpiresAt - DateTime.UtcNow).ShouldBeInRange(TimeSpan.FromHours(7.9), TimeSpan.FromHours(8.1));
        (adminRow.ExpiresAt - DateTime.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61), "boşta kalma = refresh token ömrü");
        (normalRow.FamilyExpiresAt - DateTime.UtcNow).ShouldBeInRange(TimeSpan.FromDays(29), TimeSpan.FromDays(31));
    }

    [Fact]
    public async Task PlatformAdminSessionLifetimes_AreConfigurable_AndRotationNeverExtendsTheAbsoluteLifetime()
    {
        await using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Identity:PlatformAdminRefreshFamilyHours", "2");
            b.UseSetting("Identity:PlatformAdminRefreshIdleMinutes", "10");
        });
        var (email, first) = await NewPlatformAdminAsync(host.Services, host.CreateClient);

        var initial = await RefreshRowAsync(email);
        (initial.FamilyExpiresAt - DateTime.UtcNow).ShouldBeInRange(TimeSpan.FromHours(1.9), TimeSpan.FromHours(2.1));
        (initial.ExpiresAt - DateTime.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));

        var rotated = await host.CreateClient().PostAsJsonAsync($"{Base}/auth/refresh", new { refreshToken = first.RefreshToken }, Ct);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK, await rotated.Content.ReadAsStringAsync(Ct));
        var next = (await rotated.Content.ReadFromJsonAsync<AuthResponse>(Ct))!;
        next.RefreshToken.ShouldNotBe(first.RefreshToken);

        var rows = await RefreshRowsAsync(email);
        rows.Count.ShouldBe(2);
        rows[1].FamilyExpiresAt.ShouldBe(rows[0].FamilyExpiresAt, "dönüşüm mutlak ömrü uzatmaz");
        rows[1].ExpiresAt.ShouldBeLessThanOrEqualTo(rows[1].FamilyExpiresAt);

        // Boşta kalma süresi dolan token yenilenemez (istemci açık değildi).
        await ExecuteAsync("UPDATE identity.refresh_tokens SET expires_at = now() - interval '1 minute' WHERE replaced_by_token_hash IS NULL AND revoked_at IS NULL AND user_id = (SELECT id FROM identity.users WHERE normalized_email = @e)", ("e", email.ToUpperInvariant()));
        await (await host.CreateClient().PostAsJsonAsync($"{Base}/auth/refresh", new { refreshToken = next.RefreshToken }, Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
    }

    [Fact]
    public async Task ADeactivatedPlatformAdmin_CanNeitherLogInNorRefresh_AndCanBeReactivated()
    {
        var (email, auth) = await NewPlatformAdminAsync(factory.Services, factory.CreateClient);
        await ExecuteAsync("UPDATE identity.users SET is_active = false WHERE normalized_email = @e", ("e", email.ToUpperInvariant()));

        var refresh = await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/refresh", new { refreshToken = auth.RefreshToken }, Ct);
        refresh.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        var login = await factory.CreateClient().PostAsJsonAsync($"{Base}/auth/login", new { email, password = AdminPassword }, Ct);
        login.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        await ExecuteAsync("UPDATE identity.users SET is_active = true WHERE normalized_email = @e", ("e", email.ToUpperInvariant()));
        (await factory.CreateClient().LoginAsync(email, AdminPassword)).AccessToken.ShouldNotBeNullOrEmpty();
    }

    // ---- Alan davranışı (domain) ---------------------------------------------------------------------------------------

    [Fact]
    public void User_Deactivate_ClosesTheAccount_RenewsTheSecurityStamp_AndIsIdempotent_Reactivate_ClearsTheLockout()
    {
        var user = User.Create("alan@example.com", "Alan", "tr", "hash");
        var stamp = user.SecurityStamp;
        var now = DateTime.UtcNow;

        user.Deactivate();
        user.IsActive.ShouldBeFalse();
        user.SecurityStamp.ShouldNotBe(stamp);
        user.CanSignIn(now).IsFailure.ShouldBeTrue();

        var deactivatedStamp = user.SecurityStamp;
        user.Deactivate();
        user.SecurityStamp.ShouldBe(deactivatedStamp, "ikinci çağrı değişiklik yapmaz");

        user.RecordFailedAccess(now, new LockoutPolicy(1, TimeSpan.FromMinutes(15)));
        user.IsLockedOut(now).ShouldBeTrue();
        user.Reactivate();
        user.IsActive.ShouldBeTrue();
        user.IsLockedOut(now).ShouldBeFalse();
        user.FailedAccessCount.ShouldBe(0);
    }

    [Fact]
    public void User_RevokePlatformAdmin_RemovesTheFlag_AndIsIdempotent()
    {
        var user = User.Create("yonetici@example.com", "Yonetici", "tr", "hash");
        user.GrantPlatformAdmin();
        user.IsPlatformAdmin.ShouldBeTrue();

        user.RevokePlatformAdmin();
        user.RevokePlatformAdmin();

        user.IsPlatformAdmin.ShouldBeFalse();
    }

    // ---- yardımcılar ---------------------------------------------------------------------------------------------------

    private static async Task<(string Email, AuthResponse Auth)> NewPlatformAdminAsync(IServiceProvider services, Func<HttpClient> createClient)
    {
        var email = UniqueEmail("padmin");
        using (var scope = services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapper>().EnsureAsync(email, AdminPassword, "Platform Yonetici", null, Ct);
            result.Outcome.ShouldBe(PlatformAdminOutcome.Created);
        }

        // İlk giriş: geçici parola → parolayı değiştir (yeni oturum ailesi platform yöneticisi ömrüyle açılır).
        var client = createClient();
        var first = await client.LoginAsync(email, AdminPassword);
        first.MustChangePassword.ShouldBeTrue();
        client.WithToken(first.AccessToken);
        var changed = await client.PostAsJsonAsync($"{Base}/me/password", new { currentPassword = AdminPassword, newPassword = AdminPassword }, Ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        return (email, (await changed.Content.ReadFromJsonAsync<AuthResponse>(Ct))!);
    }

    private sealed record RefreshRow(DateTime ExpiresAt, DateTime FamilyExpiresAt);

    private async Task<RefreshRow> RefreshRowAsync(string email) => (await RefreshRowsAsync(email))[^1];

    private async Task<List<RefreshRow>> RefreshRowsAsync(string email)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT t.expires_at, t.family_expires_at FROM identity.refresh_tokens t
            WHERE t.family_id = (SELECT t2.family_id FROM identity.refresh_tokens t2 JOIN identity.users u ON u.id = t2.user_id WHERE u.normalized_email = @e ORDER BY t2.id DESC LIMIT 1)
            ORDER BY t.id
            """,
            connection);
        command.Parameters.AddWithValue("e", email.ToUpperInvariant());
        var rows = new List<RefreshRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new RefreshRow(reader.GetDateTime(0), reader.GetDateTime(1)));
        }

        return rows;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}
