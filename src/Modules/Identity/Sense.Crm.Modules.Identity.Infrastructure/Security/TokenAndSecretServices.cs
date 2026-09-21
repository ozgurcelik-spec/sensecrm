using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Domain.Roles;
using Sense.Crm.Modules.Identity.Domain.Tenants;
using Sense.Crm.Modules.Identity.Domain.Users;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Security;
using IdentityOptions = Sense.Crm.Modules.Identity.Application.IdentityOptions;

namespace Sense.Crm.Modules.Identity.Infrastructure.Security;

/// <summary>JWT ayarları (host'un Auth bölümüyle aynı şema; Identity yalnız üretim tarafını okur).</summary>
public sealed class JwtIssuerOptions
{
    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;
}

public sealed class JwtTokenService(RsaSecurityKey key, IOptions<JwtIssuerOptions> jwt, IOptions<IdentityOptions> identity, TimeProvider clock) : ITokenService
{
    private static readonly JwtSecurityTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(identity.Value.RefreshTokenDays);

    public TimeSpan RefreshFamilyLifetime => TimeSpan.FromDays(identity.Value.RefreshFamilyDays);

    public TimeSpan RefreshTokenLifetimeFor(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsPlatformAdmin ? TimeSpan.FromMinutes(Math.Max(identity.Value.PlatformAdminRefreshIdleMinutes, 1)) : RefreshTokenLifetime;
    }

    public TimeSpan RefreshFamilyLifetimeFor(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsPlatformAdmin ? TimeSpan.FromHours(Math.Max(identity.Value.PlatformAdminRefreshFamilyHours, 1)) : RefreshFamilyLifetime;
    }

    public AccessToken IssueAccessToken(User user, Tenant tenant, Role role)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(identity.Value.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(ClaimNames.Subject, user.Id.ToString()),
            new(ClaimNames.Tenant, tenant.Id.ToString()),
            new(ClaimNames.TenantSlug, tenant.Slug),
            new(ClaimNames.Email, user.Email),
            new(ClaimNames.Name, user.DisplayName),
            new(ClaimNames.Language, user.Locale),
            new(ClaimNames.Roles, role.Name),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };

        if (user.IsPlatformAdmin)
        {
            claims.Add(new Claim(ClaimNames.PlatformAdmin, ClaimNames.TrueValue));
        }

        // Geçici parola: değiştirilene kadar yalnız parola değiştirme uçları çalışır (PasswordChangeRequiredMiddleware; stateless bayrak).
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(ClaimNames.PasswordChangeRequired, ClaimNames.TrueValue));
        }

        var token = new JwtSecurityToken(
            issuer: jwt.Value.Issuer,
            audience: jwt.Value.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new AccessToken(Handler.WriteToken(token), expires);
    }
}

/// <summary>
/// PBKDF2-HMAC-SHA512 (ASP.NET Core PasswordHasher, Identity v3 biçimi; framework içi). Yineleme sayısı yapılandırılır
/// (<c>Identity:PasswordHashIterations</c>, varsayılan 210 000 — OWASP: HMAC-SHA512 için ≥ 210 000). Daha düşük yinelemeli eski
/// hash'ler doğrulanır ve <see cref="PasswordVerification.SuccessRehashNeeded"/> döner (giriş sırasında yeniden hash'lenir).
/// User tipi yalnız generic parametre.
/// </summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher;
    private readonly Lazy<string> _dummyHash;

    public AspNetPasswordHasher(IOptions<IdentityOptions> identity)
    {
        _hasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = Math.Max(identity.Value.PasswordHashIterations, MinIterations),
        }));

        // Kullanıcı bulunamayan girişte aynı maliyeti ödemek için rastgele bir parolanın hash'i (süreç başına bir kez).
        _dummyHash = new Lazy<string>(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(DummyPasswordBytes))));
    }

    private const int MinIterations = 10_000;
    private const int DummyPasswordBytes = 24;

    // PasswordHasher<TUser> kullanıcı nesnesini hash/verify için hiç okumaz; bu katmanda User örneği yoktur.
    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public PasswordVerification Check(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return PasswordVerification.Failed;
        }

        return _hasher.VerifyHashedPassword(null!, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };
    }

    public void VerifyDummy(string password) => _hasher.VerifyHashedPassword(null!, _dummyHash.Value, password);
}

/// <summary>
/// Bellek içi giriş azaltma (<see cref="ILoginThrottle"/>): e-posta anahtarlı sabit pencere kovası + (IP, hesap) hatalı deneme sayacı.
/// Tek örnek varsayımı (runbook); yeniden başlatmada sıfırlanır. Süresi geçen girdiler zaman zaman temizlenir.
/// </summary>
public sealed class LoginThrottle(IOptions<RateLimitingOptions> rateLimits, IOptions<IdentityOptions> identity, TimeProvider clock) : ILoginThrottle
{
    private const int SweepThreshold = 20_000;

    private readonly ConcurrentDictionary<string, Window> _emailBuckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Window> _failures = new(StringComparer.Ordinal);

    private sealed class Window(DateTime startedAtUtc)
    {
        public DateTime StartedAtUtc { get; set; } = startedAtUtc;

        public int Count { get; set; }
    }

    public bool TryAcquireEmailBucket(string normalizedEmail)
    {
        var settings = rateLimits.Value.LoginEmail;
        var window = TimeSpan.FromSeconds(Math.Max(settings.WindowSeconds, 1));
        return Increment(_emailBuckets, normalizedEmail, window, settings.PermitLimit, out _);
    }

    public bool IsBlocked(string? ip, string normalizedEmail)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return _failures.TryGetValue(Key(ip, normalizedEmail), out var w)
            && now - w.StartedAtUtc < FailureWindow
            && w.Count >= identity.Value.LoginThrottleMaxFailures;
    }

    public void RecordFailure(string? ip, string normalizedEmail) =>
        Increment(_failures, Key(ip, normalizedEmail), FailureWindow, int.MaxValue, out _);

    public void Reset(string? ip, string normalizedEmail) => _failures.TryRemove(Key(ip, normalizedEmail), out _);

    private TimeSpan FailureWindow => TimeSpan.FromMinutes(Math.Max(identity.Value.LoginThrottleWindowMinutes, 1));

    private static string Key(string? ip, string normalizedEmail) => string.Concat(ip ?? "unknown", "|", normalizedEmail);

    /// <returns>false: pencerede <paramref name="limit"/> aşıldı (bu çağrı sayılmadı).</returns>
    private bool Increment(ConcurrentDictionary<string, Window> store, string key, TimeSpan windowLength, int limit, out int count)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (store.Count > SweepThreshold)
        {
            Sweep(store, now, windowLength);
        }

        var window = store.GetOrAdd(key, _ => new Window(now));
        lock (window)
        {
            if (now - window.StartedAtUtc >= windowLength)
            {
                window.StartedAtUtc = now;
                window.Count = 0;
            }

            if (window.Count >= limit)
            {
                count = window.Count;
                return false;
            }

            window.Count++;
            count = window.Count;
            return true;
        }
    }

    private static void Sweep(ConcurrentDictionary<string, Window> store, DateTime now, TimeSpan windowLength)
    {
        foreach (var (key, window) in store)
        {
            if (now - window.StartedAtUtc >= windowLength)
            {
                store.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>Kriptografik rastgele token (base64url) ve SHA-256 hash (hex).</summary>
public sealed class SecretGenerator : ISecretGenerator
{
    private const int SuffixBytes = 2;

    public string NewToken(int bytes = IdentityDefaults.TokenBytes) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    public string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // Karışabilen karakterler (0/O, 1/l/I) yok; tek seferlik parola telefonla/kâğıttan okunabilsin.
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public string NewPassword(int length = IdentityDefaults.GeneratedPasswordLength) =>
        string.Create(length, PasswordAlphabet, static (span, alphabet) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
        });

    public string NewSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SuffixBytes));
}
