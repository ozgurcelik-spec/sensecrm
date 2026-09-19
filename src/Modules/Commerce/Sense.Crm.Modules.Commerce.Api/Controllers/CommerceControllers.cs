using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Application.Orders;
using Sense.Crm.Modules.Commerce.Application.Products;
using Sense.Crm.Modules.Commerce.Application.Quotes;
using Sense.Crm.Modules.Commerce.Application.Reports;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Commerce.Api.Controllers;

/// <summary>Commerce uç noktaları (docs/plan/m6a-ticaret.md). Taban: /api/v1. Yetki her sorgu/komutun [RequiresPermission]'ındadır.</summary>
public static class CommerceRoutes
{
    public const string Products = ApiRoutes.VersionedBase + "/products";
    public const string Quotes = ApiRoutes.VersionedBase + "/quotes";
    public const string Orders = ApiRoutes.VersionedBase + "/orders";
    public const string CommerceReports = ApiRoutes.VersionedBase + "/reports/commerce";
}

/// <summary>
/// Ürün gövdesi (POST/PUT, tam değiştirme). <c>name</c> zorunlu; <c>currency</c> verilmezse <c>TRY</c>; <c>taxRate</c> API varsayılanı 0;
/// <c>isActive</c> verilmezse oluştururken true, güncellerken mevcut korunur.
/// </summary>
public sealed record ProductRequest(
    string Name,
    string? Code,
    string? Description,
    decimal UnitPrice,
    string? Currency,
    decimal TaxRate,
    string? Unit,
    bool? IsActive);

/// <summary>
/// Teklif gövdesi (POST/PUT, tam değiştirme). Gövdedeki hesaplanan alanlar (<c>lineTotal</c>, <c>grandTotal</c> …) yok sayılır; sunucu hesaplar.
/// <c>ownerUserId</c> verilmezse çağıran (güncellemede mevcut sahip); <c>validUntil</c> <c>YYYY-MM-DD</c>.
/// </summary>
public sealed record QuoteRequest(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? ValidUntil,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    IReadOnlyList<LineRequest>? Lines);

/// <summary>Sipariş gövdesi (POST/PUT): teklifle aynı, <c>validUntil</c> yerine <c>orderDate</c> (verilmezse bugün — kiracı saati; güncellemede mevcut korunur).</summary>
public sealed record OrderRequest(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? OrderDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    IReadOnlyList<LineRequest>? Lines);

public sealed record ReasonRequest(string? Reason);

public sealed record ExtendRequest(DateOnly? ValidUntil);

/// <summary>Ürün kataloğu. Silme yumuşaktır; belge kalemleri anlık görüntü olduğundan silme her zaman serbesttir.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.Products)]
[Authorize]
public sealed class ProductsController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<ProductDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagedQuery paging, [FromQuery] bool? isActive, [FromQuery] string? currency, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListProductsQuery(paging, new ProductFilter(isActive, currency)), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetProductQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<ProductDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] ProductRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateProductCommand(request.Name, request.Code, request.Description, request.UnitPrice, request.Currency, request.TaxRate, request.Unit, request.IsActive),
            ct);
        return created.IsFailure ? Problem(created.Error) : CreatedAtAction(nameof(Get), new { id = created.Value.Id }, created.Value);
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ProductRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateProductCommand(id, request.Name, request.Code, request.Description, request.UnitPrice, request.Currency, request.TaxRate, request.Unit, request.IsActive),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteProductCommand(id), ct));
}

/// <summary>Teklifler: kalemli, KDV/iskontolu; durum makinesi ve tekliften siparişe dönüşüm (bkz. plan).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.Quotes)]
[Authorize]
public sealed class QuotesController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<QuoteSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] QuoteStatus? status,
        [FromQuery] Guid? accountId,
        [FromQuery] Guid? contactId,
        [FromQuery] Guid? dealId,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] DateOnly? validFrom,
        [FromQuery] DateOnly? validTo,
        [FromQuery] bool? converted,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(
            new ListQuotesQuery(paging, new QuoteFilter(status, accountId, contactId, dealId, ownerUserId, validFrom, validTo, converted)),
            ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetQuoteQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] QuoteRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateQuoteCommand(
                request.Subject,
                request.AccountId,
                request.ContactId,
                request.DealId,
                request.ValidUntil,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Lines),
            ct);
        return created.IsFailure ? Problem(created.Error) : CreatedAtAction(nameof(Get), new { id = created.Value.Id }, created.Value);
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] QuoteRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateQuoteCommand(
                id,
                request.Subject,
                request.AccountId,
                request.ContactId,
                request.DealId,
                request.ValidUntil,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Lines),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteQuoteCommand(id), ct));

    /// <summary><c>draft → sent</c> (yalnızca durum değişikliği; e-posta gönderimi yok).</summary>
    [HttpPost(ApiRoutes.IdParam + "/send")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new SendQuoteCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/accept")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new AcceptQuoteCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/reject")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] ReasonRequest? request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new RejectQuoteCommand(id, request?.Reason), ct));

    [HttpPost(ApiRoutes.IdParam + "/revert")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revert(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new RevertQuoteCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/extend")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Extend(Guid id, [FromBody] ExtendRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new ExtendQuoteCommand(id, request.ValidUntil), ct));

    /// <summary>Kabul edilmiş teklifi tek transaction'da satış siparişine dönüştürür (201 + <c>Location: /api/v1/orders/{id}</c> + sipariş detayı). İzin: <c>crm.quotes.read</c> + <c>crm.orders.write</c>.</summary>
    [HttpPost(ApiRoutes.IdParam + "/convert")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Convert(Guid id, CancellationToken ct)
    {
        var order = await Dispatcher.Send(new ConvertQuoteCommand(id), ct);
        return order.IsFailure
            ? Problem(order.Error)
            : CreatedAtAction(nameof(OrdersController.Get), "Orders", new { id = order.Value.Id }, order.Value);
    }
}

/// <summary>Satış siparişleri: doğrudan veya tekliften dönüşümle; durum makinesi (bkz. plan).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.Orders)]
[Authorize]
public sealed class OrdersController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<OrderSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] SalesOrderStatus? status,
        [FromQuery] Guid? accountId,
        [FromQuery] Guid? contactId,
        [FromQuery] Guid? dealId,
        [FromQuery] Guid? quoteId,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] DateOnly? orderFrom,
        [FromQuery] DateOnly? orderTo,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(
            new ListOrdersQuery(paging, new OrderFilter(status, accountId, contactId, dealId, quoteId, ownerUserId, orderFrom, orderTo)),
            ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetOrderQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] OrderRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateOrderCommand(
                request.Subject,
                request.AccountId,
                request.ContactId,
                request.DealId,
                request.OrderDate,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Lines),
            ct);
        return created.IsFailure ? Problem(created.Error) : CreatedAtAction(nameof(Get), new { id = created.Value.Id }, created.Value);
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] OrderRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateOrderCommand(
                id,
                request.Subject,
                request.AccountId,
                request.ContactId,
                request.DealId,
                request.OrderDate,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Lines),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteOrderCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new ConfirmOrderCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/fulfill")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Fulfill(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new FulfillOrderCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] ReasonRequest? request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new CancelOrderCommand(id, request?.Reason), ct));
}

/// <summary>
/// Ticaret raporu (<c>crm.reports.read</c>). <c>from</c>/<c>to</c> <c>YYYY-MM-DD</c> (uçlar dahil, kiracı saat diliminde takvim günü); verilmezse son 12 ay.
/// </summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.CommerceReports)]
[Authorize]
public sealed class CommerceReportsController : ApiControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType<CommerceSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new GetCommerceSummaryQuery(from, to), ct));
}
