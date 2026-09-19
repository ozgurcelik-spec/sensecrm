using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Application.AuditLog;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Identity.Api.Controllers;

/// <summary>
/// Kayıt bazlı denetim geçmişi: <c>GET /audit?entityType=Account&amp;entityId={id}</c> → <c>/organization/audit</c> ile aynı biçim.
/// Yetki ilgili kaynağın <c>crm.&lt;kaynak&gt;.read</c> izni (veya <c>org.audit.read</c>); kontrol handler'dadır.
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IdentityRoutes.Audit)]
[Authorize]
public sealed class AuditController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<AuditPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromQuery] string? entityType, [FromQuery] string? entityId, [FromQuery] PagedQuery paging, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetEntityAuditQuery(entityType ?? string.Empty, entityId ?? string.Empty, paging), ct));
}
