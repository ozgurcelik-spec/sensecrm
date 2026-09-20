using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Application.Vendors;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Commerce.Api.Controllers;

/// <summary>
/// Tedarikçi gövdesi (POST/PUT, tam değiştirme). <c>name</c> zorunlu; <c>ownerUserId</c> verilmezse çağıran (güncellemede mevcut); <c>emailOptOut</c> verilmezse false
/// (güncellemede mevcut korunur). <c>website</c> mutlak <c>http/https</c> adresi olmalıdır.
/// </summary>
public sealed record VendorRequest(
    string Name,
    Guid? OwnerUserId,
    string? Phone,
    string? Email,
    string? Website,
    string? Category,
    string? GlAccount,
    DocumentAddressDto? Address,
    string? Description,
    bool? EmailOptOut);

/// <summary>Tedarikçiler (Zoho "Vendor"): ürünlerin birincil tedarikçisi ve satın alma emirlerinin muhatabı. Silme yumuşaktır; silinmemiş PO'su olan tedarikçi silinemez.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.Vendors)]
[Authorize]
public sealed class VendorsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<VendorDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] string? category,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] bool? emailOptOut,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListVendorsQuery(paging, new VendorFilter(category, ownerUserId, emailOptOut)), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<VendorDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetVendorQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<VendorDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] VendorRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateVendorCommand(
                request.Name,
                request.OwnerUserId,
                request.Phone,
                request.Email,
                request.Website,
                request.Category,
                request.GlAccount,
                request.Address,
                request.Description,
                request.EmailOptOut),
            ct);
        return created.IsFailure ? Problem(created.Error) : CreatedAtAction(nameof(Get), new { id = created.Value.Id }, created.Value);
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] VendorRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateVendorCommand(
                id,
                request.Name,
                request.OwnerUserId,
                request.Phone,
                request.Email,
                request.Website,
                request.Category,
                request.GlAccount,
                request.Address,
                request.Description,
                request.EmailOptOut),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteVendorCommand(id), ct));
}
