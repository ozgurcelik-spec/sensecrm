namespace Sense.Crm.Modules.Sales.Contracts;

/// <summary>
/// Modüller arası lead durumu sorgusu (Sales uygular; M6C Marketing için). Aktif kiracı ve yumuşak silme filtresi altında
/// çalışır: başka organizasyonun veya silinmiş lead'ler sonuçta asla yer almaz.
/// </summary>
public interface ILeadStatusLookup
{
    /// <summary>Verilen lead kimliklerinden durumu <c>converted</c> olanları döner (Marketing dönüşmüş lead'i kampanyaya eklemez).</summary>
    Task<IReadOnlySet<Guid>> GetConvertedLeadIdsAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct = default);
}
