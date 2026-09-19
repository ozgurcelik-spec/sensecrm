namespace Sense.Crm.Shared.Contracts.Events;

/// <summary>Modüller arası yayınlanan olay. Outbox üzerinden, kiracı bağlamıyla teslim edilir.</summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }

    Guid TenantId { get; }

    DateTimeOffset OccurredAt { get; }

    Guid? ActorUserId { get; }
}

public abstract record IntegrationEvent(Guid TenantId, Guid? ActorUserId = null) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task Handle(TEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>Modüller arası olay taşıyıcısı. Faz 0: in-process; Faz 4: RabbitMQ adaptörü.</summary>
public interface IEventBus
{
    Task Publish<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;
}

/// <summary>
/// Komut handler'ından integration event yayınlama noktası: olayı, tipinin ait olduğu modülün (olay kaydı Contracts
/// assembly'si AddModuleHandlers'a verilmiş olmalı) outbox'ına ekler. Komutun SaveChanges'i ile aynı transaction'da
/// kalıcılaşır; OutboxProcessor sonra IEventBus'a yayınlar. Komut başarısız olursa hiçbir şey yazılmaz.
/// </summary>
public interface IIntegrationEventOutbox
{
    void Enqueue(IIntegrationEvent integrationEvent);
}
