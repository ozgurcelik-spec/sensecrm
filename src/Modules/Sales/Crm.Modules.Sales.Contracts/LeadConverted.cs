using Crm.Shared.Contracts.Events;

namespace Crm.Modules.Sales.Contracts;

/// <summary>
/// Bir potansiyel müşteri (lead) firma + kişi (+ fırsat) olarak dönüştürüldüğünde yayınlanan integration event.
/// Dönüştürme transaction'ıyla aynı anda outbox'a yazılır (M3 aktiviteleri/M4 workflow tüketir).
/// </summary>
public sealed record LeadConverted(
    Guid TenantId,
    Guid LeadId,
    Guid AccountId,
    Guid ContactId,
    Guid? DealId,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);
