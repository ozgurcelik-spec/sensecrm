using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.ApiKeys;
using Sense.Crm.Modules.Integrations.Application.OpenApi;
using Sense.Crm.Modules.Integrations.Application.Webhooks;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Integrations.Api.Controllers;

/// <summary>Integrations uç noktaları (docs/plan/m8b-entegrasyonlar.md). Taban: <c>/api/v1/integrations</c>. Yetki her sorgu/komutun <c>[RequiresPermission]</c>'ındadır (<c>org.integrations.manage</c>).</summary>
public static class IntegrationsRoutes
{
    public const string Base = ApiRoutes.VersionedBase + "/integrations";
    public const string Webhooks = Base + "/webhooks";
    public const string WebhookEvents = Base + "/webhook-events";
    public const string Status = Base + "/status";
    public const string Deliveries = Base + "/deliveries";
    public const string ApiKeys = Base + "/api-keys";
    public const string OpenApi = Base + "/openapi.json";
}

public sealed record CreateWebhookRequest(string? Name, string? Url, IReadOnlyList<string>? EventTypes, string? Description, bool? Enabled);

public sealed record UpdateWebhookRequest(string? Name, string? Url, IReadOnlyList<string>? EventTypes, string? Description, bool? Enabled);

public sealed record RotateSecretRequest(int? GraceHours);

public sealed record CreateApiKeyRequest(string? Name, IReadOnlyList<string>? Scopes, DateTime? ExpiresAt, IReadOnlyList<string>? AllowedCidrs, string? Description);

/// <summary>Webhook abonelikleri: HTTPS hedefi, olay türü süzgeci, imza sırrı (bir kez gösterilir), test ping'i.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IntegrationsRoutes.Webhooks)]
[Authorize]
public sealed class WebhooksController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<WebhookSubscriptionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagedQuery paging, [FromQuery] bool? enabled, [FromQuery] string? eventType, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListWebhooksQuery(paging, enabled, eventType), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<WebhookSubscriptionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetWebhookQuery(id), ct));

    /// <summary>201 + abonelik + <c>secret</c> (yalnız burada; <c>Cache-Control: no-store</c>).</summary>
    [HttpPost]
    [ProducesResponseType<WebhookSubscriptionDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateWebhookRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return CreatedWithBody(
            await Dispatcher.Send(new CreateWebhookSubscriptionCommand(request.Name ?? string.Empty, request.Url ?? string.Empty, request.EventTypes ?? [], request.Description, request.Enabled ?? true), ct));
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWebhookRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateWebhookSubscriptionCommand(id, request.Name ?? string.Empty, request.Url ?? string.Empty, request.EventTypes ?? [], request.Description, request.Enabled ?? true), ct));

    [HttpPost(ApiRoutes.IdParam + "/enable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new EnableWebhookCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DisableWebhookCommand(id), ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteWebhookCommand(id), ct));

    /// <summary>Yeni sır anında geçerli; eski sır <c>graceHours</c> (0–168, varsayılan 24) boyunca çift imzayla desteklenir. Yanıt <c>no-store</c>.</summary>
    [HttpPost(ApiRoutes.IdParam + "/rotate-secret")]
    [ProducesResponseType<RotatedSecretDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RotateSecret(Guid id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RotateSecretRequest? request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return FromResult(await Dispatcher.Send(new RotateWebhookSecretCommand(id, request?.GraceHours), ct));
    }

    /// <summary>Gövdesiz → 202 <c>{ deliveryId }</c>; Worker birkaç sn içinde teslim eder.</summary>
    [HttpPost(ApiRoutes.IdParam + "/test")]
    [ProducesResponseType<DeliveryAcceptedDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var result = await Dispatcher.Send(new SendWebhookTestCommand(id), ct);
        return result.IsSuccess ? StatusCode(StatusCodes.Status202Accepted, result.Value) : Problem(result.Error);
    }
}

/// <summary>Olay kataloğu ve dağıtım durumu.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Authorize]
public sealed class IntegrationsInfoController : ApiControllerBase
{
    [HttpGet(IntegrationsRoutes.WebhookEvents)]
    [ProducesResponseType<IReadOnlyList<WebhookEventDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Events(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListWebhookEventsQuery(), ct));

    [HttpGet(IntegrationsRoutes.Status)]
    [ProducesResponseType<IntegrationsStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetIntegrationsStatusQuery(), ct));

    /// <summary>Kiracıya (plan) göre süzülmüş OpenAPI belgesi; yalnız <c>org.integrations.manage</c> sahibine. <c>Cache-Control: private, no-store</c>.</summary>
    [HttpGet(IntegrationsRoutes.OpenApi)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenApi(CancellationToken ct)
    {
        var result = await Dispatcher.Query(new GetOpenApiDocumentQuery(), ct);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        Response.Headers.CacheControl = "private, no-store";
        return Content(result.Value.Json, MediaTypes.Json);
    }
}

/// <summary>Teslimat günlüğü: liste/ayrıntı/yeniden gönderme.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IntegrationsRoutes.Deliveries)]
[Authorize]
public sealed class DeliveriesController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<DeliveryListItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] Guid? subscriptionId,
        [FromQuery] string? status,
        [FromQuery] string? eventType,
        [FromQuery] Guid? eventId,
        [FromQuery] string? kind,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListDeliveriesQuery(paging, subscriptionId, status, eventType, eventId, kind, from, to), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<DeliveryDetailDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetDeliveryQuery(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/redeliver")]
    [ProducesResponseType<DeliveryAcceptedDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Redeliver(Guid id, CancellationToken ct)
    {
        var result = await Dispatcher.Send(new RedeliverDeliveryCommand(id), ct);
        return result.IsSuccess ? StatusCode(StatusCodes.Status202Accepted, result.Value) : Problem(result.Error);
    }
}

/// <summary>API anahtarları (makine istemcileri). Ham anahtar yalnız oluşturma yanıtında bir kez döner (<c>no-store</c>).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IntegrationsRoutes.ApiKeys)]
[Authorize]
public sealed class ApiKeysController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<ApiKeyDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagedQuery paging, [FromQuery] string? status, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListApiKeysQuery(paging, status), ct));

    /// <summary>Yalnız <b>anahtar kimliğiyle</b> çağrılır (JWT ile <c>403 forbidden</c>): istemcinin anahtarını sınaması içindir.</summary>
    [HttpGet("current")]
    [ProducesResponseType<CurrentApiKeyDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Current(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetCurrentApiKeyQuery(), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<ApiKeyDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetApiKeyQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<ApiKeyDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return CreatedWithBody(await Dispatcher.Send(
            new CreateApiKeyCommand(request.Name ?? string.Empty, request.Scopes ?? [], request.ExpiresAt, request.AllowedCidrs, request.Description), ct));
    }

    /// <summary><c>{ name?, description?, allowedCidrs? }</c> (kapsam/bitiş değişmez); gönderilmeyen alan korunur, <c>description: null</c> açıklamayı temizler.</summary>
    [HttpPatch(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        string? name = null;
        string? description = null;
        var descriptionSet = false;
        IReadOnlyList<string>? cidrs = null;
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in body.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "name" when property.Value.ValueKind == JsonValueKind.String:
                        name = property.Value.GetString();
                        break;
                    case "description":
                        descriptionSet = true;
                        description = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                        break;
                    case "allowedcidrs" when property.Value.ValueKind == JsonValueKind.Array:
                        cidrs = [.. property.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty)];
                        break;
                    default:
                        break;
                }
            }
        }

        return FromResult(await Dispatcher.Send(new UpdateApiKeyCommand(id, name, description, descriptionSet, cidrs), ct));
    }

    [HttpPost(ApiRoutes.IdParam + "/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new RevokeApiKeyCommand(id), ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteApiKeyCommand(id), ct));

    [HttpGet(ApiRoutes.IdParam + "/usage")]
    [ProducesResponseType<ApiKeyUsageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Usage(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetApiKeyUsageQuery(id, from, to), ct));
}
