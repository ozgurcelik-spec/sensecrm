namespace Sense.Crm.Shared.Kernel.Domain;

/// <summary>
/// Kendi kimliği olmayan satırlar (owned koleksiyonlar, ilişki tabloları, altyapı tabloları) için denetim kolonu tabanı.
/// Böylece istisnasız her tablo CreatedAt / CreatedUserId / ModifiedDate / ModifiedUserId taşır ve interceptor tarafından doldurulur.
/// </summary>
public abstract class AuditableRecord : IAuditable
{
    public DateTime CreatedAt { get; set; }

    public Guid? CreatedUserId { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public Guid? ModifiedUserId { get; set; }
}
