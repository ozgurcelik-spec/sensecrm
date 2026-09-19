using Sense.Crm.Modules.Marketing.Domain.Campaigns;
using Sense.Crm.Modules.Marketing.Domain.Members;

namespace Sense.Crm.Modules.Marketing.Domain;

// Depo arayüzleri komut tarafıdır (agregat yükle/ekle/sil). Sorgular kiracı ve yumuşak silme filtresi altında çalışır:
// başka organizasyonun kaydı burada hiç bulunmaz (null → not_found). Yumuşak silinen kampanyanın üyeleri hiçbir depo
// yolundan dönmez (kampanya filtresinden geçer).

public interface ICampaignRepository
{
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct);

    void Add(Campaign campaign);

    void Remove(Campaign campaign);
}

public interface ICampaignMemberRepository
{
    /// <summary>
    /// Kampanyanın üyelik yazımlarını <b>işlem (transaction) süresince</b> serileştirir (PostgreSQL advisory lock; commit/rollback'te
    /// kendiliğinden düşer). Toplu ekleme, ön kontrolü (zaten üye mi) ve eklemeyi bu kilidin altında yapar; böylece eşzamanlı iki
    /// istek aynı üyeleri aynı anda eklemeye çalışmaz (benzersiz ihlali/deadlock oluşmaz). Açık bir transaction gerektirir.
    /// </summary>
    Task LockCampaignMembersAsync(Guid campaignId, CancellationToken ct);

    /// <summary>Verilen kampanyada, verilen türde ve kimliklerle zaten üye olanların kayıt (Sales) kimlikleri.</summary>
    Task<IReadOnlySet<Guid>> GetExistingMemberIdsAsync(Guid campaignId, CampaignMemberType type, IReadOnlyCollection<Guid> memberIds, CancellationToken ct);

    /// <summary>Kampanyadaki üyelik <b>satır</b> kimlikleriyle üyelikler (bu kampanyada olmayanlar dönmez).</summary>
    Task<IReadOnlyList<CampaignMember>> GetByIdsAsync(Guid campaignId, IReadOnlyCollection<Guid> membershipIds, CancellationToken ct);

    /// <summary>
    /// Üyelikleri ekler ve <b>kaydeder</b>; eşzamanlı ekleme yüzünden benzersiz indeks ihlali olursa (başka bir istek aynı
    /// üyeyi önce ekledi) çakışan satırları atlayıp bir kez yeniden dener. Gerçekten eklenen kayıt kimliklerini döner.
    /// </summary>
    Task<IReadOnlySet<Guid>> AddRangeIgnoringDuplicatesAsync(IReadOnlyList<CampaignMember> members, CancellationToken ct);

    void Remove(CampaignMember member);

    /// <summary>
    /// <c>(memberType = lead, memberId = leadId)</c> olan, dönüşmemiş ve silinmemiş kampanyaya ait tüm üyelikler
    /// (<c>LeadConverted</c> için; kampanya durumu önemsizdir).
    /// </summary>
    Task<IReadOnlyList<CampaignMember>> GetOpenLeadMembershipsAsync(Guid leadId, CancellationToken ct);
}
