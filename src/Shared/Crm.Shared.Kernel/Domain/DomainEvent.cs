namespace Crm.Shared.Kernel.Domain;

/// <summary>Aggregate içinde gerçekleşen, aynı transaction'da outbox'a yazılan olay.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
