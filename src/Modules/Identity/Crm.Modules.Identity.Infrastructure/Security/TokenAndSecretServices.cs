using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Domain.Roles;
using Crm.Modules.Identity.Domain.Tenants;
using Crm.Modules.Identity.Domain.Users;
using Crm.Shared.Contracts.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using IdentityOptions = Crm.Modules.Identity.Application.IdentityOptions;

namespace Crm.Modules.Identity.Infrastructure.Security;

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

/// <summary>PBKDF2 (ASP.NET Core PasswordHasher, framework içi). User tipi yalnız generic parametre.</summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();

    // PasswordHasher<TUser> kullanıcı nesnesini hash/verify için hiç okumaz; bu katmanda User örneği yoktur.
    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        return _hasher.VerifyHashedPassword(null!, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}

/// <summary>Kriptografik rastgele token (base64url) ve SHA-256 hash (hex).</summary>
public sealed class SecretGenerator : ISecretGenerator
{
    private const int SuffixBytes = 2;

    public string NewToken(int bytes = IdentityDefaults.TokenBytes) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    public string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public string NewSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SuffixBytes));
}
