using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;

namespace Sense.Crm.Modules.Commerce.Application;

// ---- Ortak: adres bloğu -----------------------------------------------------------------------------------------------

/// <summary>
/// Belge/tedarikçi adres bloğu (faturalama / teslimat). Tüm alanlar isteğe bağlı metindir (<c>street</c> ≤ 200, diğerleri ≤ 100; kırpılır);
/// tüm alanlar boşsa blok <c>null</c> saklanır ve yanıtta yazılmaz. Koordinat yoktur.
/// </summary>
public sealed record DocumentAddressDto(string? Street, string? Building, string? City, string? State, string? PostalCode, string? Country);

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
    Guid? VendorId,
    string? VendorName,
    decimal? PurchasePrice,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ProductFilter(bool? IsActive, string? Currency, Guid? VendorId = null);

// ---- Belge kalemi (teklif, sipariş, fatura, satın alma emri ortak biçim) ------------------------------------------------

/// <summary>
/// Kalem isteği: hesaplanan alanlar yoktur (gövdede gelse de yok sayılır, sunucu hesaplar). <c>unitPrice</c> <b>isteğe bağlıdır</b>: verilirse override
/// olarak esastır; verilmezse sunucu fiyat listesi/katalog (satın alma emrinde <c>purchasePrice</c>) ile çözer.
/// </summary>
public sealed record LineRequest(Guid? ProductId, string? Description, decimal Quantity, decimal? UnitPrice, decimal DiscountPercent, decimal TaxRate);

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
    decimal Adjustment,
    decimal GrandTotal,
    DateOnly? ValidUntil,
    string? Carrier,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    string? PriceBookName,
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
    Guid? InvoiceId,
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
    Guid? InvoiceId,
    string? InvoiceNumber,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Adjustment,
    decimal GrandTotal,
    DateOnly OrderDate,
    DateOnly? DueDate,
    string? CustomerPoNumber,
    decimal? ExciseTax,
    decimal? SalesCommission,
    string? Pending,
    string? Carrier,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    string? PriceBookName,
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

// ---- Fatura -------------------------------------------------------------------------------------------------------

public sealed record InvoicePaymentDto(
    Guid Id,
    decimal Amount,
    DateOnly PaidOn,
    PaymentMethod? Method,
    string? Reference,
    string? Notes,
    Guid RecordedByUserId,
    DateTime RecordedAt);

/// <summary>Fatura liste satırı; <c>status</c> etkin durumdur (<c>partiallyPaid | paid | overdue</c> türetilir).</summary>
public sealed record InvoiceSummaryDto(
    Guid Id,
    string Number,
    string Subject,
    InvoiceStatus Status,
    Guid AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealName,
    Guid? OrderId,
    string? OrderNumber,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal GrandTotal,
    decimal PaidAmount,
    decimal BalanceAmount,
    DateOnly InvoiceDate,
    DateOnly? DueDate,
    DateTime CreatedAt);

public sealed record InvoiceDto(
    Guid Id,
    string Number,
    string Subject,
    InvoiceStatus Status,
    Guid AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? DealId,
    string? DealName,
    Guid? OrderId,
    string? OrderNumber,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Adjustment,
    decimal GrandTotal,
    decimal PaidAmount,
    decimal BalanceAmount,
    DateOnly InvoiceDate,
    DateOnly? DueDate,
    string? CustomerPoNumber,
    decimal? ExciseTax,
    decimal? SalesCommission,
    string? Carrier,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    Guid? PriceBookId,
    string? PriceBookName,
    string? Terms,
    string? Notes,
    DateTime? SentAt,
    DateTime? CancelledAt,
    string? CancelReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IReadOnlyList<InvoicePaymentDto> Payments,
    IReadOnlyList<DocumentLineDto> Lines);

public sealed record InvoiceFilter(
    InvoiceStatus? Status,
    Guid? AccountId,
    Guid? ContactId,
    Guid? DealId,
    Guid? OrderId,
    Guid? OwnerUserId,
    DateOnly? InvoiceFrom,
    DateOnly? InvoiceTo,
    DateOnly? DueFrom,
    DateOnly? DueTo);

// ---- Satın alma emri ------------------------------------------------------------------------------------------------

public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    string Number,
    string Subject,
    PurchaseOrderStatus Status,
    Guid VendorId,
    string? VendorName,
    Guid? ContactId,
    string? ContactName,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal GrandTotal,
    DateOnly PoDate,
    DateOnly? DueDate,
    DateTime CreatedAt);

public sealed record PurchaseOrderDto(
    Guid Id,
    string Number,
    string Subject,
    PurchaseOrderStatus Status,
    Guid VendorId,
    string? VendorName,
    Guid? ContactId,
    string? ContactName,
    Guid OwnerUserId,
    string? OwnerName,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Adjustment,
    decimal GrandTotal,
    DateOnly PoDate,
    DateOnly? DueDate,
    decimal? ExciseTax,
    decimal? SalesCommission,
    string? Carrier,
    DocumentAddressDto? BillingAddress,
    DocumentAddressDto? ShippingAddress,
    string? Terms,
    string? Notes,
    DateTime? ConfirmedAt,
    DateTime? ReceivedAt,
    DateTime? CancelledAt,
    string? CancelReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IReadOnlyList<DocumentLineDto> Lines);

public sealed record PurchaseOrderFilter(
    PurchaseOrderStatus? Status,
    Guid? VendorId,
    Guid? ContactId,
    Guid? OwnerUserId,
    DateOnly? PoFrom,
    DateOnly? PoTo);

// ---- Tedarikçi ----------------------------------------------------------------------------------------------------

public sealed record VendorDto(
    Guid Id,
    string Name,
    Guid OwnerUserId,
    string? OwnerName,
    string? Phone,
    string? Email,
    string? Website,
    string? Category,
    string? GlAccount,
    DocumentAddressDto? Address,
    string? Description,
    bool EmailOptOut,
    int ProductCount,
    int PurchaseOrderCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record VendorFilter(string? Category, Guid? OwnerUserId, bool? EmailOptOut);

// ---- Fiyat listesi ------------------------------------------------------------------------------------------------

public sealed record PriceBookDto(
    Guid Id,
    string Name,
    Guid OwnerUserId,
    string? OwnerName,
    bool IsActive,
    PricingModel PricingModel,
    decimal? AdjustmentPercent,
    string Currency,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsEffective,
    string? Description,
    int EntryCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record PriceBookFilter(bool? IsActive, string? Currency, bool? Effective, Guid? OwnerUserId);

public sealed record PriceBookEntryDto(
    Guid ProductId,
    string ProductName,
    string? ProductCode,
    decimal CatalogPrice,
    decimal UnitPrice,
    DateTime UpdatedAt);

public sealed record ResolvedPriceDto(Guid ProductId, decimal UnitPrice, PriceSource Source);

public sealed record ResolvePricesResponse(IReadOnlyList<ResolvedPriceDto> Items);

public sealed record AccountDefaultPriceBookDto(Guid PriceBookId, string PriceBookName, bool IsEffective);

// ---- Rapor --------------------------------------------------------------------------------------------------------

public sealed record StatusTotalDto<TStatus>(TStatus Status, int Count, decimal Amount);

public sealed record QuoteReportDto(int TotalCount, decimal TotalAmount, IReadOnlyList<StatusTotalDto<QuoteStatus>> ByStatus);

public sealed record OrderReportDto(int TotalCount, decimal TotalAmount, IReadOnlyList<StatusTotalDto<SalesOrderStatus>> ByStatus);

public sealed record InvoiceReportDto(
    int TotalCount,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal OutstandingAmount,
    int OverdueCount,
    decimal OverdueAmount,
    IReadOnlyList<StatusTotalDto<InvoiceStatus>> ByStatus);

public sealed record PurchaseOrderReportDto(int TotalCount, decimal TotalAmount, IReadOnlyList<StatusTotalDto<PurchaseOrderStatus>> ByStatus);

public sealed record CommerceSummaryDto(
    IReadOnlyList<string> Currencies,
    QuoteReportDto Quotes,
    OrderReportDto Orders,
    decimal? ConversionRate,
    InvoiceReportDto Invoices,
    PurchaseOrderReportDto PurchaseOrders);
