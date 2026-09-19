using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Domain.Users;
using Crm.Modules.Identity.Infrastructure.Security;
using Crm.Tests.Shared.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;
using IdentityOptions = Crm.Modules.Identity.Application.IdentityOptions;

namespace Crm.Modules.Identity.Tests.Api;

/// <summary>
/// M3 (giriş: zamanlama, genel hata, IP+hesap azaltma, e-posta kovası), M5 (refresh: atomik dönüşüm, eşzamanlılık toleransı, mutlak aile
/// ömrü), M2 (kimlik doğrulamalı hız sınırı, istek boyutu), L4 (JWT algoritma/kid), L5 (hash yineleme + yeniden hash), L6 (sayfalama).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LoginAndSessionHardeningApiTests(CrmApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- M3 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_RevealsLockedOutOrDisabled_OnlyWhenThePasswordIsCorrect()
    {
        await using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Identity:MaxFailedAccessAttempts", "3");
            b.UseSetting("Identity:LoginThrottleMaxFailures", "1000");
        });
        var email = UniqueEmail("lock");
        await host.CreateClient().SignUpAsync("Lock Org", email);
        var client = host.CreateClient();

        // 3 hatalı deneme hesabı kilitler ama yanıt hep genel: saldırgan kilitlendiğini öğrenemez.
        for (var i = 0; i < 4; i++)
        {
            await (await Login(client, email, "yanlis-parola-" + i)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        // Yalnız DOĞRU parola durumu söyler; kilit süresi yanlış denemelerle uzatılmadı.
        var lockoutBefore = await ScalarAsync("SELECT lockout_end_utc FROM identity.users WHERE normalized_email = @e", ("e", email.ToUpperInvariant()));
        lockoutBefore.ShouldNotBe(DBNull.Value);
        await (await Login(client, email, DefaultPassword)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.locked_out");
        await (await Login(client, email, "hala-yanlis")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        (await ScalarAsync("SELECT lockout_end_utc FROM identity.users WHERE normalized_email = @e", ("e", email.ToUpperInvariant()))).ShouldBe(lockoutBefore, "kilitliyken yanlış deneme kilidi uzatmaz");

        // Pasif hesap: yanlış parola → genel hata; doğru parola → user_disabled.
        var disabled = UniqueEmail("disabled");
        await host.CreateClient().SignUpAsync("Disabled Org", disabled);
        await ExecuteAsync("UPDATE identity.users SET is_active = false WHERE normalized_email = @e", ("e", disabled.ToUpperInvariant()));
        await (await Login(client, disabled, "yanlis-parola-x")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        await (await Login(client, disabled, DefaultPassword)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.user_disabled");

        // Bilinmeyen hesap: aynı genel yanıt.
        await (await Login(client, UniqueEmail("ghost"), DefaultPassword)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [Fact]
    public async Task Login_FromOneIp_CannotLockAKnownAccount_ButIsThrottledAfterFiveFailures()
    {
        var email = UniqueEmail("throttle");
        await factory.CreateClient().SignUpAsync("Throttle Org", email);
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await (await Login(client, email, "yanlis-parola-" + i)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        // Aynı IP + hesap eşiği (5) aşıldı: bu IP artık reddedilir (429) — doğru parola bile denenmez.
        await (await Login(client, email, DefaultPassword)).ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");

        // Ama hesap KİLİTLENMEDİ (kilit eşiği 10 > IP eşiği 5): tek IP bilinen bir e-postayı kilitleyemez.
        (await ScalarAsync("SELECT lockout_end_utc IS NULL FROM identity.users WHERE normalized_email = @e", ("e", email.ToUpperInvariant()))).ShouldBe(true);
        (await ScalarAsync("SELECT failed_access_count FROM identity.users WHERE normalized_email = @e", ("e", email.ToUpperInvariant()))).ShouldBe(5);
    }

    [Fact]
    public async Task Login_HasAnEmailKeyedRateLimitBucket_AcrossClients()
    {
        await using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:LoginEmail:PermitLimit", "3");
            b.UseSetting("Identity:LoginThrottleMaxFailures", "1000");
        });
        var email = UniqueEmail("bucket");
        await host.CreateClient().SignUpAsync("Bucket Org", email);

        for (var i = 0; i < 3; i++)
        {
            await (await Login(host.CreateClient(), email, "yanlis-parola-" + i)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        await (await Login(host.CreateClient(), email, DefaultPassword)).ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");
        (await Login(host.CreateClient(), UniqueEmail("other-bucket"), DefaultPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // başka e-posta etkilenmez
    }

    [Fact]
    public async Task Login_RejectsOversizedPasswords_WithoutHashingThem()
    {
        var response = await Login(factory.CreateClient(), UniqueEmail("big"), new string('x', 129));

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public void UnknownUserVerification_CostsAboutAsMuchAsAWrongPasswordVerification()
    {
        var hasher = new AspNetPasswordHasher(Options.Create(new IdentityOptions { PasswordHashIterations = 210_000 }));
        var hash = hasher.Hash("Dogru.Parola.123");
        _ = hasher.Check(hash, "warm-up"); // JIT / ilk çağrı maliyetini ele
        hasher.VerifyDummy("warm-up");

        var wrong = Time(() => hasher.Check(hash, "Yanlis.Parola.123"));
        var dummy = Time(() => hasher.VerifyDummy("Yanlis.Parola.123"));

        dummy.ShouldBeGreaterThan(wrong * 0.4, "sahte doğrulama gerçek doğrulamayla aynı maliyette olmalı (zamanlama farkı hesabı ifşa etmesin)");
        dummy.ShouldBeLessThan(wrong * 2.5);
    }

    [Fact]
    public void LoginThrottle_KeysOnIpAndAccount_SoAnotherIpIsNotBlocked_AndSuccessResets()
    {
        var throttle = new LoginThrottle(
            Options.Create(new Crm.Shared.Contracts.Configuration.RateLimitingOptions()),
            Options.Create(new IdentityOptions { LoginThrottleMaxFailures = 3 }),
            TimeProvider.System);

        for (var i = 0; i < 3; i++)
        {
            throttle.RecordFailure("10.0.0.1", "A@X.COM");
        }

        throttle.IsBlocked("10.0.0.1", "A@X.COM").ShouldBeTrue();
        throttle.IsBlocked("10.0.0.2", "A@X.COM").ShouldBeFalse("başka IP engellenmez");
        throttle.IsBlocked("10.0.0.1", "B@X.COM").ShouldBeFalse("başka hesap engellenmez");
        throttle.Reset("10.0.0.1", "A@X.COM");
        throttle.IsBlocked("10.0.0.1", "A@X.COM").ShouldBeFalse();
    }

    // ---- M5 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentRefresh_WithTheSameToken_LetsExactlyOneWin_AndDoesNotRevokeTheFamily()
    {
        var auth = await factory.CreateClient().SignUpAsync("Concurrent Org", UniqueEmail("conc"));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AuthApiTests.RefreshAsync(factory.CreateClient(), auth.RefreshToken)));

        var winners = results.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        winners.Count.ShouldBe(1, "aynı token ile eşzamanlı yenilemeden yalnız biri başarılı olur");
        foreach (var loser in results.Except(winners))
        {
            await loser.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        }

        // Kaybedenler aileyi kapatmadı: kazananın yeni token'ı hâlâ geçerli.
        var next = (await winners[0].Content.ReadFromJsonAsync<AuthResponse>(Ct))!;
        (await AuthApiTests.RefreshAsync(factory.CreateClient(), next.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReusingARotatedToken_WithinTheGraceWindowFromTheSameClient_DoesNotKillTheSession()
    {
        var auth = await factory.CreateClient().SignUpAsync("Grace Org", UniqueEmail("grace"));
        var rotated = await AuthApiTests.RefreshAsync(factory.CreateClient(), auth.RefreshToken, "browser/1.0");
        var next = (await rotated.Content.ReadFromJsonAsync<AuthResponse>(Ct))!;

        // Hemen, aynı istemciden tekrar: reddedilir ama hırsızlık sayılmaz (aile açık kalır).
        // (İlk yenileme başka User-Agent ile yapıldığından 'aynı istemci' = "browser/1.0".)
        await (await AuthApiTests.RefreshAsync(factory.CreateClient(), auth.RefreshToken, "browser/1.0")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        (await AuthApiTests.RefreshAsync(factory.CreateClient(), next.RefreshToken, "browser/1.0")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReusingARotatedToken_AfterTheGraceWindow_IsTheft_AndRevokesTheFamily()
    {
        var auth = await factory.CreateClient().SignUpAsync("Theft Org", UniqueEmail("theft"));
        var next = (await (await AuthApiTests.RefreshAsync(factory.CreateClient(), auth.RefreshToken)).Content.ReadFromJsonAsync<AuthResponse>(Ct))!;

        // Döndürme anını 5 dakika öncesine çek (tolerans 10 sn'yi aştı).
        await ExecuteAsync("UPDATE identity.refresh_tokens SET revoked_at = now() - interval '5 minutes' WHERE replaced_by_token_hash IS NOT NULL AND user_id = (SELECT user_id FROM identity.refresh_tokens ORDER BY id DESC LIMIT 1)");

        await (await AuthApiTests.RefreshAsync(factory.CreateClient(), auth.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
        await (await AuthApiTests.RefreshAsync(factory.CreateClient(), next.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
    }

    [Fact]
    public async Task RefreshFamily_HasAnAbsoluteLifetime_ThatRotationDoesNotExtend()
    {
        var auth = await factory.CreateClient().SignUpAsync("Absolute Org", UniqueEmail("absolute"));
        var second = (await (await AuthApiTests.RefreshAsync(factory.CreateClient(), auth.RefreshToken)).Content.ReadFromJsonAsync<AuthResponse>(Ct))!;

        // Dönüşüm aile mutlak ömrünü aynen devralır; token ömrü hiçbir zaman aile ömrünü aşmaz; aile ~30 gün.
        var rows = await RowsAsync("SELECT family_expires_at, expires_at FROM identity.refresh_tokens WHERE family_id = (SELECT family_id FROM identity.refresh_tokens ORDER BY id DESC LIMIT 1) ORDER BY id");
        rows.Count.ShouldBe(2);
        rows[1][0].ShouldBe(rows[0][0], "aile ömrü dönüşümle uzamaz");
        ((DateTime)rows[1][1]).ShouldBeLessThanOrEqualTo((DateTime)rows[1][0]);
        ((DateTime)rows[0][0]).ShouldBeInRange(DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));

        // Aile mutlak ömrü dolduğunda (token'ın kendi ömrü dolmamış olsa bile) yenileme reddedilir.
        await ExecuteAsync("UPDATE identity.refresh_tokens SET family_expires_at = now() - interval '1 minute' WHERE family_id = (SELECT family_id FROM identity.refresh_tokens ORDER BY id DESC LIMIT 1)");
        await (await AuthApiTests.RefreshAsync(factory.CreateClient(), second.RefreshToken)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.invalid_refresh_token");
    }

    [Fact]
    public async Task AccessToken_LivesFifteenMinutes()
    {
        var auth = await factory.CreateClient().SignUpAsync("Ttl Org", UniqueEmail("ttl"));

        (auth.ExpiresAt - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.1));
    }

    // ---- L4 / L5 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Jwt_UsesRs256AndAKeyThumbprintKid_AndRejectsOtherAlgorithms()
    {
        var auth = await factory.CreateClient().SignUpAsync("Jwt Org", UniqueEmail("jwt"));

        var token = new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken);
        token.Header.Alg.ShouldBe("RS256");
        token.Header.Kid.ShouldNotBeNullOrWhiteSpace();
        token.Header.Kid.ShouldNotBe("crm-dev");
        token.Header.Kid.Length.ShouldBe(16);

        // alg=none ve HS256 ile hazırlanmış (imzasız/yanlış imzalı) token'lar reddedilir.
        var payload = auth.AccessToken.Split('.')[1];
        foreach (var alg in new[] { "none", "HS256" })
        {
            var header = Base64Url(JsonSerializer.Serialize(new { alg, typ = "JWT", kid = token.Header.Kid }));
            var forged = $"{header}.{payload}.{(alg == "none" ? string.Empty : Base64Url("bogus-signature"))}";
            await (await factory.CreateClient().WithToken(forged).GetAsync($"{Base}/me", Ct)).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "auth.unauthenticated");
        }
    }

    [Fact]
    public void PasswordHasher_UsesAtLeast210000Iterations()
    {
        var hasher = new AspNetPasswordHasher(Options.Create(new IdentityOptions()));

        IterationCount(hasher.Hash("Bir.Parola.12345")).ShouldBeGreaterThanOrEqualTo(210_000);
    }

    [Fact]
    public async Task Login_UpgradesLegacyHashes_ToTheCurrentIterationCount()
    {
        var email = UniqueEmail("legacy");
        await factory.CreateClient().SignUpAsync("Legacy Org", email);
        var legacy = new PasswordHasher<User>().HashPassword(null!, DefaultPassword); // ASP.NET varsayılanı: 100 000 yineleme
        IterationCount(legacy).ShouldBeLessThan(210_000);
        await ExecuteAsync("UPDATE identity.users SET password_hash = @h WHERE normalized_email = @e", ("h", legacy), ("e", email.ToUpperInvariant()));

        (await Login(factory.CreateClient(), email, DefaultPassword)).StatusCode.ShouldBe(HttpStatusCode.OK); // eski hash doğrulanır

        var stored = (string)(await ScalarAsync("SELECT password_hash FROM identity.users WHERE normalized_email = @e", ("e", email.ToUpperInvariant())))!;
        IterationCount(stored).ShouldBeGreaterThanOrEqualTo(210_000);
        (await Login(factory.CreateClient(), email, DefaultPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- M2 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuthenticatedRequests_AreRateLimitedPerUser_AndPerTenant_ButAnonymousHealthIsNot()
    {
        await using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:User:PermitLimit", "5");
            b.UseSetting("RateLimiting:User:WindowSeconds", "60");
            b.UseSetting("RateLimiting:Tenant:PermitLimit", "8");
        });
        var admin = host.CreateClient();
        var auth = await admin.SignUpAsync("Limited Org", UniqueEmail("limited"));
        admin.WithToken(auth.AccessToken);

        for (var i = 0; i < 5; i++)
        {
            (await admin.GetAsync($"{Base}/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await admin.GetAsync($"{Base}/me", Ct);
        await limited.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");
        limited.Headers.Contains("Retry-After").ShouldBeTrue();

        // Anonim uçlar kullanıcı/kiracı sınırına girmez.
        for (var i = 0; i < 10; i++)
        {
            (await host.CreateClient().GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await host.CreateClient().GetAsync($"{Base}/auth/config", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Başka kullanıcı (başka kiracı) etkilenmez.
        var other = host.CreateClient();
        other.WithToken((await other.SignUpAsync("Other Limited Org", UniqueEmail("other-limited"))).AccessToken);
        (await other.GetAsync($"{Base}/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantLimit_IsSharedAcrossThatTenantsUsers()
    {
        await using var host = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Tenant:PermitLimit", "6"));
        var adminClient = host.CreateClient();
        adminClient.WithToken((await adminClient.SignUpAsync("Tenant Limit Org", UniqueEmail("tl"))).AccessToken);
        var standard = await AuthApiTests.RoleIdAsync(adminClient, "Standard");
        var added = await (await adminClient.PostAsJsonAsync($"{Base}/organization/members", new { email = UniqueEmail("tm"), displayName = "Tenant Member", roleId = standard }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        // Türetilmiş host'un imza anahtarı ayrıdır: üye token'ı bu host'tan alınır (GET /me geçici parolayla da açıktır).
        var memberOnHost = host.CreateClient();
        memberOnHost.WithToken((await memberOnHost.LoginAsync(added.GetProperty("email").GetString()!, added.GetProperty("temporaryPassword").GetString()!)).AccessToken);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            statuses.Add((await (i % 2 == 0 ? adminClient : memberOnHost).GetAsync($"{Base}/me", Ct)).StatusCode);
        }

        statuses.Count(s => s == HttpStatusCode.TooManyRequests).ShouldBeGreaterThan(0, "iki kullanıcı aynı kiracı kotasını paylaşır");
    }

    [Fact]
    public void KestrelRequestBodyLimit_DefaultsToOneMegabyte_AndIsConfigurable()
    {
        factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize.ShouldBe(1_048_576);

        using var custom = factory.WithWebHostBuilder(b => b.UseSetting("RequestLimits:MaxRequestBodyBytes", "2048"));
        custom.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize.ShouldBe(2048);
    }

    // ---- L6 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("page=99999999999")]
    [InlineData("page=2147483648&pageSize=100")]
    [InlineData("pageSize=99999999999999999999")]
    [InlineData("page=1000001")]
    public async Task AbsurdPagingValues_Return400Validation_NotA500(string query)
    {
        var auth = await factory.CreateClient().SignUpAsync("Paging Org", UniqueEmail("paging"));
        var client = factory.CreateClient().WithToken(auth.AccessToken);

        foreach (var path in new[] { "organization/audit", "leads", "activities" })
        {
            var response = await client.GetAsync($"{Base}/{path}?{query}", Ct);
            await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation");
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldContain(n => n == "page" || n == "pageSize");
        }
    }

    [Theory]
    [InlineData("page=1000000&pageSize=100")]
    [InlineData("page=2147483647&pageSize=1")]
    [InlineData("page=-7&pageSize=-1")]
    [InlineData("pageSize=2147483647")]
    public async Task ExtremeButRepresentablePagingValues_AreClampedSafely(string query)
    {
        var auth = await factory.CreateClient().SignUpAsync("Clamp Org", UniqueEmail("clamp"));
        var client = factory.CreateClient().WithToken(auth.AccessToken);

        foreach (var path in new[] { "organization/audit", "leads", "activities" })
        {
            var response = await client.GetAsync($"{Base}/{path}?{query}", Ct);
            // page > 1 000 000 için 400, geri kalan (kırpılan) değerler için 200; hiçbiri 500 değildir.
            ((int)response.StatusCode).ShouldBeOneOf(200, 400);
        }
    }

    // ---- yardımcılar -------------------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> Login(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync($"{Base}/auth/login", new { email, password }, Ct);

    private static double Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            action();
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>ASP.NET Identity v3 hash biçimi: [0]=0x01, [1..4]=PRF, [5..8]=yineleme sayısı (big-endian).</summary>
    private static int IterationCount(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        bytes[0].ShouldBe((byte)0x01);
        return (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8];
    }

    private async Task<object> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<List<object[]>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object[]>();
        while (await reader.ReadAsync(Ct))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
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
