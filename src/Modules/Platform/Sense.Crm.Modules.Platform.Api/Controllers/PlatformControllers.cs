using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Application.Console;
using Sense.Crm.Modules.Platform.Application.Tenant;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Authorization;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Platform.Api.Controllers;

/// <summary>Platform uç noktaları (docs/plan/m7-saas-hazirlik.md). Taban: /api/v1.</summary>
public static class PlatformRoutes
{
    public const string PlatformBase = ApiRoutes.VersionedBase + "/platform";
    public const string Organizations = PlatformBase + "/organizations";
    public const string Subscription = ApiRoutes.VersionedBase + "/subscription";
    public const string Onboarding = ApiRoutes.VersionedBase + "/onboarding";
    public const string TenantIdParam = "{tenantId:guid}";
}

/// <summary>Abonelik değiştirme gövdesi (PUT, tam ve kalıcı değiştirme): <c>planCode*</c>, <c>trialEndsOn?</c> (yok/null = denemesiz), <c>overrides?</c> (yok/null = temiz).</summary>
public sealed record UpdateSubscriptionRequest(string? PlanCode, DateOnly? TrialEndsOn, JsonElement? Overrides);

/// <summary><c>currentPassword</c>: yalnız <c>mode = blocked</c> için zorunlu (step-up).</summary>
public sealed record SuspendRequest(string? Reason, string? Mode, string? CurrentPassword = null);

/// <summary><c>confirmTenantName*</c> (sunucudaki ad ile eşleşmeli) ve <c>currentPassword*</c> (step-up) zorunludur.</summary>
public sealed record DeletionRequestBody(string? Reason, int? RetentionDays, string? ConfirmTenantName = null, string? CurrentPassword = null);

/// <summary>Step-up korumalı gövde (imha yeniden deneme): <c>currentPassword*</c>.</summary>
public sealed record StepUpBody(string? CurrentPassword = null);

/// <summary>Platform yöneticisi geri alma gövdesi: <c>currentPassword*</c> (step-up), <c>deactivate?</c> (hesabı da pasifleştir).</summary>
public sealed record RevokePlatformAdminBody(string? CurrentPassword = null, bool Deactivate = false);

/// <summary>
/// Platform konsolu: organizasyonlar, abonelik, askı, silme talebi, kullanım. Sınıf düzeyinde <c>PlatformAdmin</c> politikası (JWT bayrağı, hızlı ret) +
/// her istek <c>[PlatformAdminOnly]</c> (veritabanından doğrulanır). Kiracı yöneticisi (tüm izinlerle bile) 403 alır; yanıtlarda kiracı iş verisi yoktur.
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(PlatformRoutes.Organizations)]
[Authorize(Policy = PlatformPolicies.PlatformAdmin)]
public sealed class PlatformOrganizationsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<OrganizationRowDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] string? status,
        [FromQuery] string? planCode,
        [FromQuery] string? source,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListOrganizationsQuery(paging, status, planCode, source), ct));

    [HttpGet(PlatformRoutes.TenantIdParam)]
    [ProducesResponseType<OrganizationDetailDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid tenantId, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetOrganizationQuery(tenantId), ct));

    [HttpPut(PlatformRoutes.TenantIdParam + "/subscription")]
    [ProducesResponseType<SubscriptionUpdateResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSubscription(Guid tenantId, [FromBody] UpdateSubscriptionRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdateSubscriptionCommand(tenantId, request.PlanCode, request.TrialEndsOn, request.Overrides), ct));

    [HttpPost(PlatformRoutes.TenantIdParam + "/suspend")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Suspend(Guid tenantId, [FromBody] SuspendRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new SuspendOrganizationCommand(tenantId, request.Reason, request.Mode, request.CurrentPassword), ct));

    [HttpPost(PlatformRoutes.TenantIdParam + "/reactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reactivate(Guid tenantId, CancellationToken ct) => FromResult(await Dispatcher.Send(new ReactivateOrganizationCommand(tenantId), ct));

    [HttpPost(PlatformRoutes.TenantIdParam + "/deletion-request")]
    [ProducesResponseType<DeletionRequestResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestDeletion(Guid tenantId, [FromBody] DeletionRequestBody request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new RequestDeletionCommand(tenantId, request.Reason, request.RetentionDays, request.ConfirmTenantName, request.CurrentPassword), ct));

    [HttpPost(PlatformRoutes.TenantIdParam + "/deletion-request/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CancelDeletion(Guid tenantId, CancellationToken ct) => FromResult(await Dispatcher.Send(new CancelDeletionCommand(tenantId), ct));

    /// <summary>Başarısız (<c>failed</c>) imha talebini yeniden denemeye alır (step-up korumalı; C-SEC2 L3). <c>lastError</c> detayda (<c>GET …/{tenantId}</c>) görünür.</summary>
    [HttpPost(PlatformRoutes.TenantIdParam + "/deletion-request/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RetryDeletion(Guid tenantId, [FromBody] StepUpBody request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new RetryDeletionCommand(tenantId, request.CurrentPassword), ct));

    [HttpGet(PlatformRoutes.TenantIdParam + "/usage")]
    [ProducesResponseType<UsageSeriesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Usage(Guid tenantId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetUsageQuery(tenantId, from, to), ct));

    [HttpPost(PlatformRoutes.TenantIdParam + "/usage/refresh")]
    [ProducesResponseType<UsageSeriesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshUsage(Guid tenantId, CancellationToken ct) => FromResult(await Dispatcher.Send(new RefreshUsageCommand(tenantId), ct));
}

/// <summary>Plan kataloğu (salt okunur), platform denetimi ve finans CSV dışa aktarma.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(PlatformRoutes.PlatformBase)]
[Authorize(Policy = PlatformPolicies.PlatformAdmin)]
public sealed class PlatformCatalogController : ApiControllerBase
{
    [HttpGet("plans")]
    [ProducesResponseType<IReadOnlyList<PlanDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Plans(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListPlansQuery(), ct));

    /// <summary>Platform yöneticisi hesapları (C-SEC2 M6; pasif olanlar dahil).</summary>
    [HttpGet("admins")]
    [ProducesResponseType<IReadOnlyList<PlatformAdminDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Admins(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListPlatformAdminsQuery(), ct));

    /// <summary>
    /// Platform yöneticisi yetkisini geri alır (isteğe bağlı hesabı da pasifleştirir; tüm oturumlar kapanır). Son aktif yönetici geri alınamaz (409). Step-up korumalı.
    /// </summary>
    [HttpPost("admins/{userId:guid}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeAdmin(Guid userId, [FromBody] RevokePlatformAdminBody request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new RevokePlatformAdminCommand(userId, request.CurrentPassword, request.Deactivate), ct));

    [HttpGet("audit")]
    [ProducesResponseType<PagedResult<PlatformAuditDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Audit(
        [FromQuery] PagedQuery paging,
        [FromQuery] Guid? tenantId,
        [FromQuery] string? action,
        [FromQuery] Guid? actorUserId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListPlatformAuditQuery(paging, tenantId, action, actorUserId, from, to), ct));

    /// <summary>
    /// CSV (<c>text/csv; charset=utf-8</c>, UTF-8 BOM, <c>attachment</c>), akış halinde. <c>from</c>/<c>to</c> (UTC günü, dahil) verilmezse önceki takvim ayı;
    /// aralık ≤ 400 gün. Aralık doğrulanır ve <c>usage.exported</c> denetimi yazılır; sonra gövde akıtılır.
    /// </summary>
    [HttpGet("usage/export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportUsage(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromServices] IUsageExportWriter writer,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        var info = await Dispatcher.Send(new ExportUsageCommand(from, to), ct);
        if (info.IsFailure)
        {
            return Problem(info.Error);
        }

        Response.ContentType = MediaTypes.Csv + "; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"usage-{info.Value.From:yyyy-MM-dd}-{info.Value.To:yyyy-MM-dd}.csv\"";
        Response.Headers.CacheControl = "no-store";
        await writer.WriteCsvAsync(info.Value.From, info.Value.To, Response.Body, clock.GetUtcNow().UtcDateTime, ct);
        return new EmptyResult();
    }
}

/// <summary>
/// Kiracı tarafı abonelik (yalnız kendi kiracısı; URL'de kiracı kimliği yok). <c>GET /subscription</c> askıda/deneme bitmiş kiracıda da çalışır
/// (<c>[TenantStatusExempt]</c>: durumu göstermek için).
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(PlatformRoutes.Subscription)]
[Authorize]
public sealed class SubscriptionController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<SubscriptionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetSubscriptionQuery(), ct));
}

/// <summary>İlk kurulum kontrol listesi (yalnız kendi kiracısı).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(PlatformRoutes.Onboarding)]
[Authorize]
public sealed class OnboardingController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<OnboardingDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetOnboardingQuery(), ct));

    [HttpPost("dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Dismiss(CancellationToken ct) => FromResult(await Dispatcher.Send(new DismissOnboardingCommand(), ct));
}
