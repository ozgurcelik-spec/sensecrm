using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Application.AuditLog;
using Sense.Crm.Modules.Identity.Application.Members;
using Sense.Crm.Modules.Identity.Application.Organization;
using Sense.Crm.Modules.Identity.Application.Roles;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Identity.Api.Controllers;

public sealed record UpdateOrganizationRequest(string Name, string DefaultLocale, string TimeZone);

public sealed record AddMemberRequest(string Email, string? DisplayName, Guid RoleId);

public sealed record UpdateMemberRequest(Guid? RoleId, bool? IsActive);

public sealed record RoleRequest(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// Aktif organizasyonun yönetimi: ayarlar, üyeler, roller, denetim kaydı. Organizasyon her zaman token'daki `tid`'dir;
/// URL'de taşınmaz. Başka organizasyona ait kimlikler kiracı filtresi nedeniyle bulunmaz (404 not_found).
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(IdentityRoutes.Organization)]
[Authorize]
public sealed class OrganizationController : ApiControllerBase
{
    private const string UserIdParam = "members/{userId:guid}";
    private const string RoleIdParam = "roles/" + ApiRoutes.IdParam;

    [HttpGet]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => FromResult(await Dispatcher.Query(new GetOrganizationQuery(), ct));

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update([FromBody] UpdateOrganizationRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdateOrganizationCommand(request.Name, request.DefaultLocale, request.TimeZone), ct));

    [HttpGet("members")]
    [ProducesResponseType<IReadOnlyList<MemberDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMembers(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListMembersQuery(), ct));

    /// <summary>
    /// Üye ekle. Yeni e-posta → hesap açılır, yanıtta bir kez <c>temporaryPassword</c> döner (yönetici parola seçmez);
    /// mevcut e-posta → bekleyen davet (<c>status: pending</c>), otomatik katılım yok. Yanıt saklanmaz (<c>no-store</c>).
    /// </summary>
    [HttpPost("members")]
    [ProducesResponseType<AddMemberResultDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddMember([FromBody] AddMemberRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return CreatedWithBody(await Dispatcher.Send(new AddMemberCommand(request.Email, request.DisplayName, request.RoleId), ct));
    }

    [HttpPatch(UserIdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateMember(Guid userId, [FromBody] UpdateMemberRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdateMemberCommand(userId, request.RoleId, request.IsActive), ct));

    [HttpGet("roles")]
    [ProducesResponseType<IReadOnlyList<RoleDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRoles(CancellationToken ct) => FromResult(await Dispatcher.Query(new ListRolesQuery(), ct));

    [HttpPost("roles")]
    [ProducesResponseType<RoleDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRole([FromBody] RoleRequest request, CancellationToken ct) =>
        CreatedWithBody(await Dispatcher.Send(new CreateRoleCommand(request.Name, request.Permissions), ct));

    [HttpPut(RoleIdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] RoleRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpdateRoleCommand(id, request.Name, request.Permissions), ct));

    [HttpDelete(RoleIdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteRole(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteRoleCommand(id), ct));

    /// <summary>?page=1&amp;pageSize=50 → { items, total }.</summary>
    [HttpGet("audit")]
    [ProducesResponseType<AuditPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Audit([FromQuery] PagedQuery paging, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetAuditLogQuery(paging), ct));
}
