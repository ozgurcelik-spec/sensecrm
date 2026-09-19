using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Service.Domain.Cases;

/// <summary>
/// Numaralama sayacı: <c>(TenantId, Year)</c> bileşik anahtar, <see cref="LastValue"/> yıl içindeki son sıra. Yalnız
/// <c>ICaseNumberGenerator</c>'ın tek ham SQL'iyle (<c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>) yazılır; talep INSERT'iyle aynı
/// transaction'dadır, satır kilidi eşzamanlı oluşturmaları sıraya dizer (boşluksuz) ve geri almada sayaç da geri alınır.
/// Denetimsiz teknik tablodur (Lead onaylı bilinçli istisna); denetim kolonları da taşımaz.
/// </summary>
public sealed class CaseCounter : ITenantEntity
{
    private CaseCounter()
    {
    }

    public Guid TenantId { get; private init; }

    public int Year { get; private init; }

    public int LastValue { get; private set; }
}
