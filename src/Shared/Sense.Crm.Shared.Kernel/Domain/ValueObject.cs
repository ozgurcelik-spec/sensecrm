namespace Sense.Crm.Shared.Kernel.Domain;

/// <summary>Bileşenleri üzerinden eşitlik taşıyan değer nesnesi.</summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other)
    {
        if (other is null || other.GetType() != GetType())
        {
            return false;
        }

        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    public override int GetHashCode() =>
        GetEqualityComponents().Aggregate(17, (hash, component) => unchecked((hash * 31) + (component?.GetHashCode() ?? 0)));

    public static bool operator ==(ValueObject? left, ValueObject? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
