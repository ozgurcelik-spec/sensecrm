using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Application.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Commerce.Api.Controllers;

/// <summary>
/// Satın alma emri gövdesi (POST/PUT, tam değiştirme). <c>vendorId</c> zorunlu; kalem <c>unitPrice</c> verilmezse <c>product.purchasePrice</c> (tedarikçi tarafı fiyat).
/// <c>poDate</c> verilmezse bugün (güncellemede mevcut korunur). Satış belgeleriyle ilişki yoktur.
/// </summary>
public sealed record PurchaseOrderRequest(
    string Subject,
    Guid VendorId,
    Guid? ContactId,
    DateOnly? PoDate,
    DateOnly? DueDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    IReadOnlyList<LineRequest>? Lines,
    string? Carrier = null,
    decimal Adjustment = 0m,
    decimal? ExciseTax = null,
    decimal? SalesCommission = null,
    DocumentAddressDto? BillingAddress = null,
    DocumentAddressDto? ShippingAddress = null);

/// <summary>Satın alma emirleri (<c>PO-2026-0001</c>): durum makinesi <c>draft → confirmed → received</c>, <c>draft | confirmed → cancelled</c>; stok yoktur.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.PurchaseOrders)]
[Authorize]
public sealed class PurchaseOrdersController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<PurchaseOrderSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] PurchaseOrderStatus? status,
        [FromQuery] Guid? vendorId,
        [FromQuery] Guid? contactId,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] DateOnly? poFrom,
        [FromQuery] DateOnly? poTo,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(
            new ListPurchaseOrdersQuery(paging, new PurchaseOrderFilter(status, vendorId, contactId, ownerUserId, poFrom, poTo)),
            ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetPurchaseOrderQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] PurchaseOrderRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreatePurchaseOrderCommand(
                request.Subject,
                request.VendorId,
                request.ContactId,
                request.PoDate,
                request.DueDate,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Carrier,
                request.Adjustment,
                request.ExciseTax,
                request.SalesCommission,
                request.BillingAddress,
                request.ShippingAddress,
                request.Lines),
            ct);
        return created.IsFailure ? Problem(created.Error) : CreatedAtAction(nameof(Get), new { id = created.Value.Id }, created.Value);
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] PurchaseOrderRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdatePurchaseOrderCommand(
                id,
                request.Subject,
                request.VendorId,
                request.ContactId,
                request.PoDate,
                request.DueDate,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Carrier,
                request.Adjustment,
                request.ExciseTax,
                request.SalesCommission,
                request.BillingAddress,
                request.ShippingAddress,
                request.Lines),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeletePurchaseOrderCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new ConfirmPurchaseOrderCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/receive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Receive(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new ReceivePurchaseOrderCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] ReasonRequest? request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new CancelPurchaseOrderCommand(id, request?.Reason), ct));
}
