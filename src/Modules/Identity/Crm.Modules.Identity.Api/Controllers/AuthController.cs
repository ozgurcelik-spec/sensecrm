using Asp.Versioning;
using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Application.Auth;
using Crm.Modules.Identity.Application.Me;
using Crm.Modules.Identity.Application.Organization;
using Crm.Modules.Identity.Application.Platform;
using Crm.Modules.Identity.Domain;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Kernel.Results;
using Crm.Shared.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Crm.Modules.Identity.Api.Controllers;

/// <summary>Identity uç noktaları (web istemcisiyle sözleşme; bkz. docs/architecture/backend.md). Taban: /api/v1.</summary>
public static class IdentityRoutes
{
    public const string Auth = ApiRoutes.VersionedBase + "/auth";
    public const string Me = ApiRoutes.VersionedBase + "/me";
    public const string Permissions = ApiRoutes.VersionedBase + "/permissions";
    public const string Organization = ApiRoutes.VersionedBase + "/organization";
    public const string Audit = ApiRoutes.VersionedBase + "/audit";
    public const string Platform = ApiRoutes.VersionedBase + "/platform";
}

/// <summary>GET /auth/config yanıtı.</summary>
public sealed record AuthConfigDto(bool SignupEnabled);

public sealed record CreateOrganizationRequest(string OrganizationName, string AdminDisplayName, string AdminEmail, string? AdminPassword, string Locale);

public sealed record SignUpRequest(string OrganizationName, string DisplayName, string Email, string Password, string Locale);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record SwitchOrganizationRequest(Guid OrganizationId);

public sealed record UpdateMeRequest(string? DisplayName, string? Locale);

/// <summary>Kayıt, giriş, token yenileme, çıkış, organizasyon değiştirme. Anonim uçlar IP bazlı hız sınırlıdır.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IdentityRoutes.Auth)]
public sealed class AuthController(IRegistrationPolicy registration) : ApiControllerBase
{
    /// <summary>
    /// Anonim istemci yapılandırması (gizli bilgi yok). Web, kayıt bağlantısını buna göre gösterir. Ayar değişikliği (yeniden
    /// başlatma gerektirir) kısa sürede yansısın diye yalnız 60 sn önbelleğe izin verilir.
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    [ProducesResponseType<AuthConfigDto>(StatusCodes.Status200OK)]
    public IActionResult GetConfig()
    {
        Response.Headers.CacheControl = "public, max-age=60";
        return Ok(new AuthConfigDto(registration.SignupEnabled));
    }

    /// <summary>
    /// Herkese açık kayıt. <c>Registration:Mode=disabled</c> iken (Production varsayılanı) girdi doğrulamasından önce 403
    /// <c>auth.signup_disabled</c> döner; organizasyonlar <c>POST /platform/organizations</c> ile açılır.
    /// </summary>
    [HttpPost("signup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicyNames.Auth)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SignUp([FromBody] SignUpRequest request, CancellationToken ct) =>
        !registration.SignupEnabled
            ? Problem(Error.Forbidden(IdentityErrors.SignupDisabled))
            : FromResult(await Dispatcher.Send(
                new SignUpCommand(request.OrganizationName, request.DisplayName, request.Email, request.Password, request.Locale, DeviceInfo, IpAddress), ct));

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicyNames.Auth)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new LoginCommand(request.Email, request.Password, DeviceInfo, IpAddress), ct));

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicyNames.Auth)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new RefreshTokenCommand(request.RefreshToken, DeviceInfo, IpAddress), ct));

    /// <summary>Refresh token'ın oturum ailesini kapatır. Süresi dolmuş access token ile de çağrılabilsin diye anonimdir.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicyNames.Auth)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new LogoutCommand(request.RefreshToken), ct));

    [HttpPost("switch-organization")]
    [Authorize]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SwitchOrganization([FromBody] SwitchOrganizationRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new SwitchOrganizationCommand(request.OrganizationId, DeviceInfo, IpAddress), ct));

    private string? DeviceInfo => Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    private string? IpAddress => HttpContext.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Oturum açmış kullanıcı.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IdentityRoutes.Me)]
[Authorize]
public sealed class MeController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<MeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetMeQuery(), ct));

    [HttpPatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update([FromBody] UpdateMeRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdateMeCommand(request.DisplayName, request.Locale), ct));
}

/// <summary>Birleşik izin kataloğu (tüm modüller): [{ key, group }].</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IdentityRoutes.Permissions)]
[Authorize]
public sealed class PermissionsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PermissionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListPermissionsQuery(), ct));
}

/// <summary>
/// Platform yöneticisi uçları (<c>isPlatformAdmin</c>): herkese açık kayıt kapalıyken organizasyon açma. Yetki handler'da her istekte
/// veritabanından doğrulanır; yönetici olmayan çağıran 403 alır. Bu uç kiracı verisini okumaz/değiştirmez, yalnız yeni organizasyonu açar.
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IdentityRoutes.Platform)]
[Authorize]
public sealed class PlatformController : ApiControllerBase
{
    /// <summary>Yeni organizasyon + ilk Administrator. <c>adminPassword</c> null ise üretilen tek seferlik parola yanıtta bir kez döner.</summary>
    [HttpPost("organizations")]
    [ProducesResponseType<CreatedOrganizationDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateOrganization([FromBody] CreateOrganizationRequest request, CancellationToken ct)
    {
        // Yanıt üretilen parolayı taşıyabilir: hiçbir aracı katman önbelleğe almasın.
        Response.Headers.CacheControl = "no-store";
        return CreatedWithBody(await Dispatcher.Send(
            new CreateOrganizationCommand(request.OrganizationName, request.AdminDisplayName, request.AdminEmail, request.AdminPassword, request.Locale), ct));
    }
}
