namespace Crm.Modules.Sales.Contracts;

/// <summary>Bir kişinin firma bağı (<see cref="AccountId"/> yoksa kişinin firması yok).</summary>
public sealed record ContactAccountLink(Guid ContactId, Guid? AccountId);

/// <summary>
/// Modüller arası kişi → firma çözümü (Sales uygular; Service kullanır): talep oluştururken kişiden firma türetmek ve
/// kişi/firma uyuşmazlığını denetlemek için. Aktif kiracı + yumuşak silme filtresi altında çalışır. <see cref="IRecordLookup"/>
/// bilinçli olarak genişletilmedi (arayüze üye eklemek sahte uygulamaları bozar).
/// </summary>
public interface IContactAccountLookup
{
    /// <summary>Kişi yok / silinmiş / başka organizasyondaysa null; kişinin firması yoksa <c>AccountId = null</c>.</summary>
    Task<ContactAccountLink?> FindAsync(Guid contactId, CancellationToken cancellationToken = default);
}
