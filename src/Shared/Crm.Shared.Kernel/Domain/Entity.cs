namespace Crm.Shared.Kernel.Domain;

/// <summary>
/// Kimliği olan domain nesnesi. Eşitlik yalnızca Id üzerinden.
/// Her tablo denetim kolonları taşır: CreatedAt, CreatedUserId, ModifiedDate, ModifiedUserId (interceptor doldurur).
/// </summary>
public abstract class Entity<TId> : IAuditable, IEquatable<Entity<TId>>
    where TId : notnull
{
    protected Entity(TId id) => Id = id;

    /// <summary>EF Core materialization için.</summary>
    protected Entity() => Id = default!;

    public TId Id { get; protected init; }

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public Guid? CreatedUserId { get; set; }

    /// <inheritdoc />
    public DateTime? ModifiedDate { get; set; }

    /// <inheritdoc />
    public Guid? ModifiedUserId { get; set; }

    public bool Equals(Entity<TId>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return GetType() == other.GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}

/// <summary>Domain event üretebilen aggregate kökü.</summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>Kiracıya ait aggregate: TenantId interceptor tarafından otomatik atanır ve değiştirilemez.</summary>
public abstract class TenantAggregateRoot<TId> : AggregateRoot<TId>, ITenantEntity
    where TId : notnull
{
    protected TenantAggregateRoot(TId id, Guid tenantId) : base(id) => TenantId = tenantId;

    protected TenantAggregateRoot()
    {
    }

    public Guid TenantId { get; protected init; }
}

/// <summary>Kiracıya ait alt entity (aggregate kökü olmayan).</summary>
public abstract class TenantEntity<TId> : Entity<TId>, ITenantEntity
    where TId : notnull
{
    protected TenantEntity(TId id, Guid tenantId) : base(id) => TenantId = tenantId;

    protected TenantEntity()
    {
    }

    public Guid TenantId { get; protected init; }
}
