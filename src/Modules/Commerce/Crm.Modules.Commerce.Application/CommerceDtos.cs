using Crm.Modules.Commerce.Domain.Orders;
using Crm.Modules.Commerce.Domain.Quotes;

namespace Crm.Modules.Commerce.Application;

// ---- Ürün ---------------------------------------------------------------------------------------------------------

public sealed record ProductDto(
    Guid Id,
    string Name,
    string? Code,
    string? Description,
    decimal UnitPrice,
    string Currency,
    decimal TaxRate,
    string? Unit,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ProductFilter(bool? IsActive, string? Currency);

// ---- Belge kalemi (teklif ve sipariş ortak biçim) ---------------------------------------------------------------------

/// <summary>Kalem isteği: hesaplanan alanlar yoktur (gövdede gelse de yok sayılır, sunucu hesaplar).</summary>
public sealed record LineRequest(Guid? ProductId, string? Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxRate);

public sealed record DocumentLineDto(
    Guid Id,
    int Position,
    Guid? ProductId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal TaxRate,
    decimal LineSubtotal,
    decimal LineDiscount,
    decimal LineTax,
    decimal LineTotal);

// ---- Teklif -------------------------------------------------------------------------------------------------------

public sealed record QuoteSummaryDto(
    Guid Id,
    string Number,
    string Subject,
    QuoteStatus Status,
    Guid AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealName,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal GrandTotal,
    DateOnly? ValidUntil,
    Guid? ConvertedOrderId,
    DateTime CreatedAt);

public sealed record QuoteDto(
    Guid Id,
    string Number,
    string Subject,
    QuoteStatus Status,
    Guid AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealName,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    DateOnly? ValidUntil,
    string? Terms,
    string? Notes,
    DateTime? SentAt,
    DateTime? AcceptedAt,
    DateTime? RejectedAt,
    string? RejectionReason,
    Guid? ConvertedOrderId,
    string? ConvertedOrderNumber,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IReadOnlyList<DocumentLineDto> Lines);

public sealed record QuoteFilter(
    QuoteStatus? Status,
    Guid? AccountId,
    Guid? ContactId,
    Guid? DealId,
    Guid? OwnerUserId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool? Converted);

// ---- Sipariş ------------------------------------------------------------------------------------------------------

public sealed record OrderSummaryDto(
    Guid Id,
    string Number,
    string Subject,
    SalesOrderStatus Status,
    Guid AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealName,
    Guid? QuoteId,
    string? QuoteNumber,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal GrandTotal,
    DateOnly OrderDate,
    DateTime CreatedAt);

public sealed record OrderDto(
    Guid Id,
    string Number,
    string Subject,
    SalesOrderStatus Status,
    Guid AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealName,
    Guid? QuoteId,
    string? QuoteNumber,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    DateOnly OrderDate,
    string? Terms,
    string? Notes,
    DateTime? FulfilledAt,
    DateTime? CancelledAt,
    string? CancelReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IReadOnlyList<DocumentLineDto> Lines);

public sealed record OrderFilter(
    SalesOrderStatus? Status,
    Guid? AccountId,
    Guid? ContactId,
    Guid? DealId,
    Guid? QuoteId,
    Guid? OwnerUserId,
    DateOnly? OrderFrom,
    DateOnly? OrderTo);

// ---- Rapor --------------------------------------------------------------------------------------------------------

public sealed record StatusTotalDto<TStatus>(TStatus Status, int Count, decimal Amount);

public sealed record QuoteReportDto(int TotalCount, decimal TotalAmount, IReadOnlyList<StatusTotalDto<QuoteStatus>> ByStatus);

public sealed record OrderReportDto(int TotalCount, decimal TotalAmount, IReadOnlyList<StatusTotalDto<SalesOrderStatus>> ByStatus);

public sealed record CommerceSummaryDto(IReadOnlyList<string> Currencies, QuoteReportDto Quotes, OrderReportDto Orders, decimal? ConversionRate);
