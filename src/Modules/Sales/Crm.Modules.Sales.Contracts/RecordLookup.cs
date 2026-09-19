namespace Crm.Modules.Sales.Contracts;

/// <summary>Başka modüllerin (ör. Activities) Sales kayıtlarına bağlanabildiği kayıt türleri. Tel biçimi camelCase string.</summary>
public enum RecordType
{
    Account,
    Contact,
    Lead,
    Deal,
}

/// <summary>Bir Sales kaydına başvuru (tür + kimlik).</summary>
public readonly record struct RecordRef(RecordType Type, Guid Id);

/// <summary>
/// Modüller arası kayıt arama sözleşmesi (Sales uygular). Aktif kiracı ve yumuşak silme filtresi altında çalışır:
/// başka organizasyonun veya silinmiş bir kaydın varlığı/adı asla dönmez. Activities gibi tüketiciler ilişkili kaydın
/// varlığını doğrulamak ve görünen adını (firma adı, kişi tam adı, lead tam adı, fırsat adı) göstermek için kullanır.
/// </summary>
public interface IRecordLookup
{
    /// <summary>Kayıt aktif organizasyonda var ve silinmemiş mi.</summary>
    Task<bool> ExistsAsync(RecordType type, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Kaydın görünen adı; kayıt yoksa/silinmişse/başka organizasyondaysa null.</summary>
    Task<string?> GetDisplayNameAsync(RecordType type, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Toplu ad çözümü (tür başına tek sorgu). Bulunamayan kayıtlar sonuçta yer almaz.</summary>
    Task<IReadOnlyDictionary<RecordRef, string>> GetDisplayNamesAsync(IReadOnlyCollection<RecordRef> records, CancellationToken cancellationToken = default);
}

/// <summary>Kişinin tutarlılık denetimi için gereken özeti (tam ad + bağlı firma; firmasız kişi <c>AccountId = null</c>).</summary>
public sealed record ContactLink(Guid Id, string FullName, Guid? AccountId);

/// <summary>Fırsatın tutarlılık denetimi için gereken özeti (ad, firma, isteğe bağlı kişi, para birimi).</summary>
public sealed record DealLink(Guid Id, string Name, Guid AccountId, Guid? ContactId, string Currency);

/// <summary>
/// Kayıt ilişkilerini okuma sözleşmesi (Sales uygular; Commerce teklif/sipariş tutarlılık kuralları için kullanır: kişi firmaya,
/// fırsat firmaya uymalı). <see cref="IRecordLookup"/> ile aynı kural: aktif kiracı + yumuşak silme filtresi altında çalışır;
/// başka organizasyonun veya silinmiş kayıt <c>null</c> döner.
/// </summary>
public interface IRecordRelationLookup
{
    Task<ContactLink?> GetContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task<DealLink?> GetDealAsync(Guid dealId, CancellationToken cancellationToken = default);
}
