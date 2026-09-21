using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Application.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Commerce.Api.Controllers;

/// <summary>
/// Fiyat listesi gövdesi (POST/PUT, tam değiştirme). <c>pricingModel</c> (<c>perProduct|flat</c>) POST'ta zorunludur; PUT'ta gönderilmezse değişmemiş sayılır
/// (farklı değer → 409 <c>pricebook.model_immutable</c>; <c>currency</c> için de aynı). <c>flat</c> için <c>adjustmentPercent</c> zorunlu (−99.99…1000.00), <c>perProduct</c>'ta yasak.
/// </summary>
public sealed record PriceBookRequest(
    string Name,
    Guid? OwnerUserId,
    bool? IsActive,
    PricingModel? PricingModel,
    decimal? AdjustmentPercent,
    string? Currency,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Description);

public sealed record PriceBookEntryRequest(decimal UnitPrice);

public sealed record ResolvePricesRequest(IReadOnlyList<Guid>? ProductIds);

public sealed record AccountDefaultRequest(Guid PriceBookId);

/// <summary>Fiyat listeleri (Zoho "Price Book"): <c>perProduct</c> girdi tablosu veya <c>flat</c> yüzde; sunucu tarafı fiyat çözümü; firma varsayılan listesi.</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.PriceBooks)]
[Authorize]
public sealed class PriceBooksController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<PriceBookDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] bool? isActive,
        [FromQuery] string? currency,
        [FromQuery] bool? effective,
        [FromQuery] Guid? ownerUserId,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListPriceBooksQuery(paging, new PriceBookFilter(isActive, currency, effective, ownerUserId)), ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<PriceBookDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetPriceBookQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<PriceBookDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] PriceBookRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreatePriceBookCommand(
                request.Name,
                request.OwnerUserId,
                request.IsActive,
                request.PricingModel,
                request.AdjustmentPercent,
                request.Currency,
                request.ValidFrom,
                request.ValidTo,
                request.Description),
            ct);
        return created.IsFailure ? Problem(created.Error) : CreatedAtAction(nameof(Get), new { id = created.Value.Id }, created.Value);
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] PriceBookRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdatePriceBookCommand(
                id,
                request.Name,
                request.OwnerUserId,
                request.IsActive,
                request.PricingModel,
                request.AdjustmentPercent,
                request.Currency,
                request.ValidFrom,
                request.ValidTo,
                request.Description),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeletePriceBookCommand(id), ct));

    // ---- Girdiler (yalnız perProduct) ------------------------------------------------------------------------------------

    [HttpGet(ApiRoutes.IdParam + "/entries")]
    [ProducesResponseType<PagedResult<PriceBookEntryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListEntries(Guid id, [FromQuery] PagedQuery paging, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ListPriceBookEntriesQuery(id, paging), ct));

    [HttpPut(ApiRoutes.IdParam + "/entries/{productId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpsertEntry(Guid id, Guid productId, [FromBody] PriceBookEntryRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new UpsertPriceBookEntryCommand(id, productId, request.UnitPrice), ct));

    [HttpDelete(ApiRoutes.IdParam + "/entries/{productId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteEntry(Guid id, Guid productId, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new DeletePriceBookEntryCommand(id, productId), ct));

    /// <summary>Fiyat çözümü (<c>crm.pricebooks.read</c>): 1–100 tekil ürün; etkin olmayan liste → 409 <c>pricebook.not_effective</c>.</summary>
    [HttpPost(ApiRoutes.IdParam + "/resolve")]
    [ProducesResponseType<ResolvePricesResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolvePricesRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Query(new ResolvePricesQuery(id, request.ProductIds), ct));

    // ---- Firma varsayılan listesi ------------------------------------------------------------------------------------------

    /// <summary>Firma varsayılanı: 200 <c>{ priceBookId, priceBookName, isEffective }</c> veya 204 (varsayılan yok).</summary>
    [HttpGet("accounts/{accountId:guid}/default")]
    [ProducesResponseType<AccountDefaultPriceBookDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GetAccountDefault(Guid accountId, CancellationToken ct)
    {
        var result = await Dispatcher.Query(new GetAccountDefaultPriceBookQuery(accountId), ct);
        if (result.IsFailure)
        {
            return Problem(result.Error);
        }

        return result.Value.Default is { } dto ? Ok(dto) : NoContent();
    }

    [HttpPut("accounts/{accountId:guid}/default")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetAccountDefault(Guid accountId, [FromBody] AccountDefaultRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new SetAccountDefaultPriceBookCommand(accountId, request.PriceBookId), ct));

    [HttpDelete("accounts/{accountId:guid}/default")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearAccountDefault(Guid accountId, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new ClearAccountDefaultPriceBookCommand(accountId), ct));
}
