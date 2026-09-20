using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sense.Crm.Modules.Commerce.Application;
using Sense.Crm.Modules.Commerce.Application.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Web.Controllers;

namespace Sense.Crm.Modules.Commerce.Api.Controllers;

/// <summary>
/// Fatura gövdesi (POST/PUT, tam değiştirme). Hesaplanan alanlar (<c>grandTotal</c>, <c>paidAmount</c> …) ve <c>orderId</c> yok sayılır (yalnız siparişten dönüşümle dolar).
/// <c>invoiceDate</c> verilmezse bugün (güncellemede mevcut korunur); kalem <c>unitPrice</c>'ı isteğe bağlıdır (yoksa sunucu çözer).
/// </summary>
public sealed record InvoiceRequest(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    Guid? OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    IReadOnlyList<LineRequest>? Lines,
    string? Carrier = null,
    decimal Adjustment = 0m,
    DocumentAddressDto? BillingAddress = null,
    DocumentAddressDto? ShippingAddress = null,
    Guid? PriceBookId = null,
    string? CustomerPoNumber = null,
    decimal? ExciseTax = null,
    decimal? SalesCommission = null);

/// <summary>Tahsilat gövdesi: <c>paidOn</c> verilmezse bugün; <c>method</c> ∈ <c>cash|bankTransfer|card|cheque|other</c> (isteğe bağlı).</summary>
public sealed record InvoicePaymentRequest(decimal Amount, DateOnly? PaidOn, PaymentMethod? Method, string? Reference, string? Notes);

/// <summary>Faturalar: kalemli, KDV/iskontolu; saklanan <c>draft|sent|cancelled</c> + türetilen <c>partiallyPaid|paid|overdue</c>; tahsilat defteri (bkz. plan).</summary>
[ApiVersion(ApiRoutes.DefaultVersion)]
[Route(CommerceRoutes.Invoices)]
[Authorize]
public sealed class InvoicesController : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<InvoiceSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PagedQuery paging,
        [FromQuery] InvoiceStatus? status,
        [FromQuery] Guid? accountId,
        [FromQuery] Guid? contactId,
        [FromQuery] Guid? dealId,
        [FromQuery] Guid? orderId,
        [FromQuery] Guid? ownerUserId,
        [FromQuery] DateOnly? invoiceFrom,
        [FromQuery] DateOnly? invoiceTo,
        [FromQuery] DateOnly? dueFrom,
        [FromQuery] DateOnly? dueTo,
        CancellationToken ct) =>
        FromResult(await Dispatcher.Query(
            new ListInvoicesQuery(paging, new InvoiceFilter(status, accountId, contactId, dealId, orderId, ownerUserId, invoiceFrom, invoiceTo, dueFrom, dueTo)),
            ct));

    [HttpGet(ApiRoutes.IdParam)]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Query(new GetInvoiceQuery(id), ct));

    [HttpPost]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] InvoiceRequest request, CancellationToken ct)
    {
        var created = await Dispatcher.Send(
            new CreateInvoiceCommand(
                request.Subject,
                request.AccountId,
                request.ContactId,
                request.DealId,
                request.InvoiceDate,
                request.DueDate,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Carrier,
                request.Adjustment,
                request.BillingAddress,
                request.ShippingAddress,
                request.PriceBookId,
                request.CustomerPoNumber,
                request.ExciseTax,
                request.SalesCommission,
                request.Lines),
            ct);
        return created.IsFailure ? Problem(created.Error) : CreatedAtAction(nameof(Get), new { id = created.Value.Id }, created.Value);
    }

    [HttpPut(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid id, [FromBody] InvoiceRequest request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(
            new UpdateInvoiceCommand(
                id,
                request.Subject,
                request.AccountId,
                request.ContactId,
                request.DealId,
                request.InvoiceDate,
                request.DueDate,
                request.OwnerUserId,
                request.Currency,
                request.Terms,
                request.Notes,
                request.Carrier,
                request.Adjustment,
                request.BillingAddress,
                request.ShippingAddress,
                request.PriceBookId,
                request.CustomerPoNumber,
                request.ExciseTax,
                request.SalesCommission,
                request.Lines),
            ct));

    [HttpDelete(ApiRoutes.IdParam)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new DeleteInvoiceCommand(id), ct));

    /// <summary><c>draft → sent</c> (yalnızca durum değişikliği; e-posta/PDF/e-fatura yok).</summary>
    [HttpPost(ApiRoutes.IdParam + "/send")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new SendInvoiceCommand(id), ct));

    /// <summary><c>sent → draft</c> (tahsilat yoksa).</summary>
    [HttpPost(ApiRoutes.IdParam + "/revert")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revert(Guid id, CancellationToken ct) => FromResult(await Dispatcher.Send(new RevertInvoiceCommand(id), ct));

    [HttpPost(ApiRoutes.IdParam + "/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] ReasonRequest? request, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new CancelInvoiceCommand(id, request?.Reason), ct));

    /// <summary>Tahsilat kaydı (201 + tahsilat gövdesi). Yalnız etkin <c>sent | partiallyPaid | overdue</c> fatura.</summary>
    [HttpPost(ApiRoutes.IdParam + "/payments")]
    [ProducesResponseType<InvoicePaymentDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RecordPayment(Guid id, [FromBody] InvoicePaymentRequest request, CancellationToken ct)
    {
        var payment = await Dispatcher.Send(new RecordInvoicePaymentCommand(id, request.Amount, request.PaidOn, request.Method, request.Reference, request.Notes), ct);
        return payment.IsFailure ? Problem(payment.Error) : CreatedAtAction(nameof(Get), new { id }, payment.Value);
    }

    /// <summary>Yanlış giriş düzeltmesi: tahsilatı siler (güncelleme ucu yoktur: sil + yeniden gir).</summary>
    [HttpDelete(ApiRoutes.IdParam + "/payments/{paymentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeletePayment(Guid id, Guid paymentId, CancellationToken ct) =>
        FromResult(await Dispatcher.Send(new DeleteInvoicePaymentCommand(id, paymentId), ct));
}
