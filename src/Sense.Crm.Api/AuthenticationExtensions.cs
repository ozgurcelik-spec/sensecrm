using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Web.DependencyInjection;

namespace Sense.Crm.Api;

/// <summary>JWT ayarları (appsettings "Auth").</summary>
public sealed class AuthOptions
{
    public string Issuer { get; set; } = AuthDefaults.Issuer;

    public string Audience { get; set; } = AuthDefaults.Audience;

    /// <summary>
    /// JWT başlığındaki <c>kid</c>. Boşsa (varsayılan) imza anahtarının açık kısmından türetilen parmak izi kullanılır (L4): anahtar
    /// döndürüldüğünde <c>kid</c> kendiliğinden değişir; üretimde sabit bir "dev" değeri yoktur.
    /// </summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// RSA özel anahtar (PEM). Üretimde ortam değişkeni/secret store'dan (<c>Auth__SigningKeyPem</c>) verilmelidir; depoya
    /// asla yazılmaz. Boşsa yalnız Development/Testing ortamında süreç başına geçici anahtar üretilir.
    /// </summary>
    public string? SigningKeyPem { get; set; }

    public int ClockSkewSeconds { get; set; } = AuthDefaults.ClockSkewSeconds;
}

public static class AuthDefaults
{
    public const string Issuer = "crm";
    public const string Audience = "crm-api";
    public const int ClockSkewSeconds = 30;
    public const int KeyThumbprintLength = 16;
    public const int DevKeySize = 2048;
    public const string TestingEnvironment = "Testing";
    public const string MissingKeyMessage = "Auth:SigningKeyPem is required outside Development/Testing (ephemeral keys are dev-only).";
}

public static class AuthenticationExtensions
{
    public static IServiceCollection AddCrmAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var auth = configuration.GetSection(ConfigurationSections.Auth).Get<AuthOptions>() ?? new AuthOptions();
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(ConfigurationSections.Auth));

        RSA rsa;
        if (!string.IsNullOrWhiteSpace(auth.SigningKeyPem))
        {
            rsa = RSA.Create();
            rsa.ImportFromPem(auth.SigningKeyPem);
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment(AuthDefaults.TestingEnvironment))
        {
            // Geliştirme: süreç başına geçici anahtar. Yeniden başlatmada access token'lar geçersizleşir,
            // refresh token'lar (veritabanında) çalışmaya devam eder.
            rsa = RSA.Create(AuthDefaults.DevKeySize);
        }
        else
        {
            throw new InvalidOperationException(AuthDefaults.MissingKeyMessage);
        }

        var key = new RsaSecurityKey(rsa) { KeyId = string.IsNullOrWhiteSpace(auth.KeyId) ? Thumbprint(rsa) : auth.KeyId };
        services.AddSingleton(key);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = auth.Issuer,
                    ValidateAudience = true,
                    ValidAudience = auth.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,

                    // L4: yalnız RS256 kabul edilir ("alg" başlığı saldırıları / algoritma karışıklığı kapanır).
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(auth.ClockSkewSeconds),
                    NameClaimType = ClaimNames.Subject,
                    RoleClaimType = ClaimNames.Roles,
                };
                o.Events = new JwtBearerEvents
                {
                    // 401 de diğer hatalarla aynı ProblemDetails sözleşmesiyle döner (code = auth.unauthenticated).
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        await ProblemResponses.WriteAsync(ctx.HttpContext, Error.Unauthorized(ErrorCodes.Unauthenticated), ctx.HttpContext.RequestAborted);
                    },

                    // M7: politika reddi (ör. /platform/** için PlatformAdmin politikası, kiracı yöneticisine 403) de aynı ProblemDetails sözleşmesiyle döner (code = forbidden).
                    OnForbidden = async ctx => await ProblemResponses.WriteAsync(ctx.HttpContext, Error.Forbidden(ErrorCodes.Forbidden), ctx.HttpContext.RequestAborted),
                };
            });

        return services;
    }

    /// <summary>Açık anahtarın (SubjectPublicKeyInfo) SHA-256 özetinin ilk baytlarının base64url'ü: anahtara bağlı, sırsız <c>kid</c>.</summary>
    internal static string Thumbprint(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..AuthDefaults.KeyThumbprintLength];
}
