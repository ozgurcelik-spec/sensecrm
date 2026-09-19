using System.Security.Cryptography;
using System.Text;
using Crm.Modules.Sales.Contracts;
using Crm.Modules.Sales.Domain.Deals;
using Crm.Modules.Sales.Domain.Pipelines;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Events;
using Crm.Shared.Infrastructure.Persistence.Outbox;

namespace Crm.Modules.Sales.Infrastructure.Events;

/// <summary>
/// Sales'in <see cref="DealStageChanged"/> domain event'ini (outbox üzerinden, Worker'da) modüller arası
/// <see cref="DealStageChangedIntegration"/> olayına çevirir ve yine Sales outbox'ına ekler (M4 workflow tetikleyicisi).
/// Mevcut davranış değişmez: domain event'i eskisi gibi işlenir; bu yalnız ek bir yayındır.
/// İdempotent: integration olayın kimliği domain event kimliğinden türetilir; olay tekrar işlenirse aynı kimlik doğar
/// ve tüketiciler (<c>ruleId + eventId</c>) ikinci kez iş başlatmaz.
/// </summary>
public sealed class DealStageChangedIntegrationPublisher(IIntegrationEventOutbox outbox, ITenantContext tenant) : IDomainEventHandler<DealStageChanged>
{
    public Task Handle(DealStageChanged domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        outbox.Enqueue(new DealStageChangedIntegration(
            tenant.TenantId,
            domainEvent.DealId,
            domainEvent.PipelineId,
            domainEvent.FromStageId,
            domainEvent.ToStageId,
            KindName(domainEvent.ToKind),
            domainEvent.Amount,
            domainEvent.Currency)
        {
            EventId = DeriveEventId(domainEvent.EventId),
        });
        return Task.CompletedTask;
    }

    private static string KindName(StageKind kind) => kind switch
    {
        StageKind.Won => DealStageKinds.Won,
        StageKind.Lost => DealStageKinds.Lost,
        _ => DealStageKinds.Open,
    };

    /// <summary>SHA-256(domain event kimliği + sabit tuz) → ilk 16 bayt (deterministik Guid).</summary>
    internal static Guid DeriveEventId(Guid domainEventId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("deal-stage-changed-integration:" + domainEventId.ToString("N")));
        return new Guid(hash.AsSpan(0, 16));
    }
}
