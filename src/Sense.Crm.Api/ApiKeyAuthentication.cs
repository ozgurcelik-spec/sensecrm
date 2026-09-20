using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Web.DependencyInjection;

namespace Sense.Crm.Api;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

public static class ApiKeySchemes
{
    /// <summary>Varsayılan şema (Bearer <c>crmk_</c> öneki → ApiKey, aksi → JwtBearer).</summary>
    public const string Smart = "Smart";

    public const string BearerPrefix = "Bearer " + ApiKeyClaimNames.TokenPrefix;
    public const string ErrorItemKey = "crm.api_key.error";

    /// <summary>
    /// Şema seçici: <c>Authorization</c> başlıklarından <b>herhangi biri</b> <c>Bearer crmk_</c> ile başlıyorsa ApiKey şeması (çoklu başlıkla JWT'ye kaçış yok: doğrulayıcı birden çok başlığı reddeder).
    /// <c>X-Api-Key</c> desteklenmez. Sahte JWT üretilmez; anahtar sunucuda kurulan ayrı kimlik olarak doğrulanır.
    /// </summary>
    public static bool IsApiKeyRequest(HttpContext context) =>
        context.Request.Headers.Authorization.Any(h => h is not null && h.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <c>ApiKey</c> kimlik doğrulama şeması (D9): <see cref="IApiKeyAuthenticator"/> sonucundan <b>sunucuda</b> bir <see cref="ClaimsPrincipal"/> kurar (<c>sub</c> = oluşturan, <c>tid</c>, <c>api_key_id</c>, <c>scp</c>, <c>auth_method = api_key</c>);
/// <c>platform_admin</c> asla eklenmez. Başarısızlık nedeni (<c>401 auth.unauthenticated | api_key.*</c>, <c>403</c>, <c>429</c>) challenge'da aynı ProblemDetails sözleşmesiyle yazılır.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authenticator = Context.RequestServices.GetRequiredService<IApiKeyAuthenticator>();
        var headers = Request.Headers.Authorization.Where(h => h is not null).Select(h => h!).ToList();
        var result = await authenticator.AuthenticateAsync(headers, Context.Connection.RemoteIpAddress?.ToString(), Context.RequestAborted).ConfigureAwait(false);
        if (result.Identity is not { } identity)
        {
            Context.Items[ApiKeySchemes.ErrorItemKey] = result.Error ?? Error.Unauthorized(ErrorCodes.Unauthenticated);
            return AuthenticateResult.Fail("API key rejected.");
        }

        var claims = new List<Claim>
        {
            new(ClaimNames.Subject, identity.CreatorUserId.ToString("D")),
            new(ClaimNames.Tenant, identity.TenantId.ToString("D")),
            new(ClaimNames.Name, identity.CreatorName),
            new(ApiKeyClaimNames.ApiKeyId, identity.KeyId.ToString("D")),
            new(ApiKeyClaimNames.ApiKeyPrefix, identity.Prefix),
            new(ApiKeyClaimNames.ApiKeyName, identity.KeyName),
            new(ApiKeyClaimNames.AuthMethod, ApiKeyClaimNames.AuthMethodApiKey),
        };
        if (!string.IsNullOrEmpty(identity.TenantSlug))
        {
            claims.Add(new Claim(ClaimNames.TenantSlug, identity.TenantSlug));
        }

        claims.AddRange(identity.Scopes.Select(s => new Claim(ApiKeyClaimNames.Scopes, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyClaimNames.Scheme, ClaimNames.Subject, ClaimNames.Roles));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var error = Context.Items.TryGetValue(ApiKeySchemes.ErrorItemKey, out var stored) && stored is Error e ? e : Error.Unauthorized(ErrorCodes.Unauthenticated);
        await ProblemResponses.WriteAsync(Context, error, Context.RequestAborted).ConfigureAwait(false);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        await ProblemResponses.WriteAsync(Context, Error.Forbidden(ErrorCodes.Forbidden), Context.RequestAborted).ConfigureAwait(false);
}
