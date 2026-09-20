using Sense.Crm.Shared.Contracts.Events;

namespace Sense.Crm.Modules.Sales.Contracts;

/// <summary>
/// Firma oluşturuldu (<c>CreateAccountHandler</c>). Oluşturma transaction'ıyla aynı anda outbox'a yazılır (M8B webhook <c>account.created</c>). Lead dönüşümü firma yaratır ama bu olayı
/// <b>üretmez</b> (tüketici <c>lead.converted</c> kullanır).
/// </summary>
public sealed record AccountCreated(
    Guid TenantId,
    Guid AccountId,
    Guid OwnerUserId,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);

/// <summary>Kişi oluşturuldu (<c>CreateContactHandler</c>); lead dönüşümü bu olayı üretmez (bkz. <see cref="AccountCreated"/>).</summary>
public sealed record ContactCreated(
    Guid TenantId,
    Guid ContactId,
    Guid? AccountId,
    Guid OwnerUserId,
    Guid? ActorUserId = null) : IntegrationEvent(TenantId, ActorUserId);
