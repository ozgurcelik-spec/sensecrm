using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Commerce.Contracts;

/// <summary>
/// Teklif kabul edildi. Kabul işlemiyle aynı transaction'da Commerce outbox'ına yazılır (M6A'da tüketici yoktur; sonraki
/// kartlar/workflow dinler).
/// </summary>
public sealed record QuoteAccepted(
    Guid TenantId,
    Guid QuoteId,
    string Number,
    Guid AccountId,
    Guid? DealId,
    decimal GrandTotal,
    string Currency,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Satış siparişi oluşturuldu. <see cref="Source"/>: <c>"quote"</c> (tekliften dönüşüm) veya <c>"direct"</c>.</summary>
public sealed record SalesOrderCreated(
    Guid TenantId,
    Guid OrderId,
    string Number,
    Guid AccountId,
    Guid? DealId,
    Guid? QuoteId,
    decimal GrandTotal,
    string Currency,
    string Source,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary><see cref="SalesOrderCreated.Source"/> değerleri.</summary>
public static class SalesOrderSources
{
    public const string Quote = "quote";
    public const string Direct = "direct";
}

/// <summary>Fatura oluşturuldu. <see cref="Source"/>: <c>"order"</c> (siparişten dönüşüm) veya <c>"direct"</c> (M9C).</summary>
public sealed record InvoiceCreated(
    Guid TenantId,
    Guid InvoiceId,
    string Number,
    Guid AccountId,
    Guid? DealId,
    Guid? OrderId,
    decimal GrandTotal,
    string Currency,
    string Source,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Fatura gönderildi (<c>draft → sent</c>).</summary>
public sealed record InvoiceSent(
    Guid TenantId,
    Guid InvoiceId,
    string Number,
    Guid AccountId,
    decimal GrandTotal,
    string Currency,
    DateOnly? DueDate,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>
/// Fatura tamamen tahsil edildi (bakiye 0'a indi). Tahsilat silinip yeniden ödenirse yeniden yayınlanır; tüketici idempotent olmalıdır.
/// Vade aşımı olayı yoktur (türetilmiş durum).
/// </summary>
public sealed record InvoicePaid(
    Guid TenantId,
    Guid InvoiceId,
    string Number,
    Guid AccountId,
    decimal GrandTotal,
    string Currency,
    DateOnly PaidOn,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Fatura iptal edildi (sipariş yeniden faturalanabilir hâle gelir).</summary>
public sealed record InvoiceCancelled(
    Guid TenantId,
    Guid InvoiceId,
    string Number,
    Guid? OrderId,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Satın alma emri oluşturuldu.</summary>
public sealed record PurchaseOrderCreated(
    Guid TenantId,
    Guid PurchaseOrderId,
    string Number,
    Guid VendorId,
    decimal GrandTotal,
    string Currency,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Satın alma emri teslim alındı (<c>confirmed → received</c>; stok yok).</summary>
public sealed record PurchaseOrderReceived(
    Guid TenantId,
    Guid PurchaseOrderId,
    string Number,
    Guid VendorId,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary><see cref="InvoiceCreated.Source"/> değerleri.</summary>
public static class InvoiceSources
{
    public const string Order = "order";
    public const string Direct = "direct";
}
