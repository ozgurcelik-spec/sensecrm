using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Contracts.Security;

/// <summary>
/// İsteği API anahtarıyla yapan makine kimliği (M8B, D9): <c>ICurrentUser.ApiKey</c> doludur; <c>UserId</c> anahtarı oluşturan kullanıcıdır (act-as-creator).
/// <see cref="Scopes"/> sunucuda üretilen kapsamlardır (izin anahtarları); etkin izin = kapsam ∩ oluşturanın o anki izinleri.
/// </summary>
public sealed record ApiKeyPrincipal(Guid KeyId, string Prefix, string Name, IReadOnlySet<string> Scopes);

/// <summary>
/// API anahtarıyla çağrılabilen tek istisna işareti (M8B): anahtar çağrılarında <c>[RequiresPermission]</c> taşımayan (<c>[AnyAuthenticatedUser]</c>) istekler
/// varsayılan olarak <c>403 api_key.not_allowed</c> alır; bu işaret bilinçli istisnadır (mimari test onaylı listeyle birebir eşler).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ApiKeyAllowedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>API anahtarı kimliği için claim ve şema adları (sunucu üretir; istemci JWT'si yoktur).</summary>
public static class ApiKeyClaimNames
{
    public const string Scheme = "ApiKey";
    public const string ApiKeyId = "api_key_id";
    public const string ApiKeyPrefix = "api_key_prefix";
    public const string ApiKeyName = "api_key_name";
    public const string Scopes = "scp";
    public const string AuthMethod = "auth_method";
    public const string AuthMethodApiKey = "api_key";

    /// <summary>Taşıyıcı önek: <c>Authorization: Bearer crmk_…</c>; şema seçici bu önekle yönlendirir.</summary>
    public const string TokenPrefix = "crmk_";
}

/// <summary>API anahtarı hata kodları (web/istemci ile sözleşme).</summary>
public static class ApiKeyErrorCodes
{
    public const string NotAllowed = "api_key.not_allowed";
    public const string Expired = "api_key.expired";
    public const string Revoked = "api_key.revoked";
    public const string OwnerInactive = "api_key.owner_inactive";
    public const string IpNotAllowed = "api_key.ip_not_allowed";
    public const string ScopeNotAllowed = "api_key.scope_not_allowed";
    public const string NameTaken = "api_key.name_taken";
    public const string Active = "api_key.active";
}

/// <summary>Doğrulanmış API anahtarı kimliği (kiracı + oluşturan + kapsam); ham anahtar veya özet içermez.</summary>
public sealed record ApiKeyIdentity(
    Guid TenantId,
    string? TenantSlug,
    Guid KeyId,
    string Prefix,
    string KeyName,
    Guid CreatorUserId,
    string CreatorName,
    IReadOnlyList<string> Scopes);

/// <summary>Kimlik doğrulama sonucu: başarı → <see cref="Identity"/>; başarısızlık → <see cref="Error"/> (401/403/429; yalnız sır doğruyken belirli kodlar).</summary>
public sealed record ApiKeyAuthResult(ApiKeyIdentity? Identity, Error? Error)
{
    public bool Succeeded => Identity is not null;

    public static ApiKeyAuthResult Success(ApiKeyIdentity identity) => new(identity, null);

    public static ApiKeyAuthResult Fail(Error error) => new(null, error);
}

/// <summary>
/// API anahtarı doğrulama portu (uygulama Integrations.Infrastructure'da; kayıtlı değilse <c>TryAdd</c> varsayılanı her zaman reddeder). Yalnız
/// <c>Authorization: Bearer crmk_…</c> tek başlığı kabul edilir; başlık yok/biçim bozuk/bilinmeyen/yanlış sır → aynı <c>401 auth.unauthenticated</c>.
/// </summary>
public interface IApiKeyAuthenticator
{
    /// <param name="authorizationHeaders">Ham <c>Authorization</c> başlık değerleri (çoklu başlık reddedilir).</param>
    /// <param name="remoteIp">İstemci IP'si (ForwardedHeaders sonrası); IP izin listesi ve başarısızlık azaltması için.</param>
    Task<ApiKeyAuthResult> AuthenticateAsync(IReadOnlyList<string> authorizationHeaders, string? remoteIp, CancellationToken ct = default);
}

/// <summary>Anahtar kullanım kaydı (istek sayacı): bellek içi toplanır, periyodik olarak kalıcılaştırılır (host'a özgü).</summary>
public interface IApiKeyUsageSink
{
    /// <param name="statusCode">Yanıt durum kodu (4xx/5xx → hata, 429 → kısıtlanan).</param>
    void Record(Guid tenantId, Guid keyId, int statusCode);
}
