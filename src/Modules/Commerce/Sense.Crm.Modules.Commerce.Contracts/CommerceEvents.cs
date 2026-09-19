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
