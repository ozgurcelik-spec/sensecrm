using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Marketing.Domain.Members;

/// <summary>Kampanya üyesi türü (Sales.Contracts <c>RecordType</c>'ın <c>lead</c>/<c>contact</c> alt kümesinin domain karşılığı).</summary>
public enum CampaignMemberType
{
    Lead,
    Contact,
}

/// <summary>Üye durumu: eklendi → gönderildi → yanıtladı; <c>converted</c> yalnız <c>LeadConverted</c> ile olur.</summary>
public enum CampaignMemberStatus
{
    Added,
    Sent,
    Responded,
    Converted,
    Unsubscribed,
}

/// <summary><see cref="CampaignMember.ChangeStatus"/> sonucu.</summary>
public enum MemberStatusChange
{
    /// <summary>Durum gerçekten değişti.</summary>
    Changed,

    /// <summary>İstenen durum zaten geçerli (no-op).</summary>
    Unchanged,

    /// <summary><c>converted</c> üyenin durumu kilitlidir; değişmedi.</summary>
    Locked,
}

/// <summary>
/// Kampanya üyeliği: bir lead/kişinin (Sales'te; yumuşak bağ, FK yok) bir kampanyadaki durumu. Fiziksel silinir (denetim kaydı
/// <c>deleted</c> bırakır). Kurallar: <c>converted</c> elle ayarlanamaz, yalnız <c>lead</c> içindir ve kilitlidir; elle ayarlanabilir
/// durumlar birbirine serbestçe geçer. <c>(campaign, memberType, memberId)</c> benzersizdir.
/// </summary>
public sealed class CampaignMember : TenantAggregateRoot<Guid>, IAuditLogged
{
    private CampaignMember()
    {
    }

    private CampaignMember(Guid id, Guid tenantId, Guid campaignId, CampaignMemberType memberType, Guid memberId, Guid? addedByUserId, DateTime nowUtc)
        : base(id, tenantId)
    {
        CampaignId = campaignId;
        MemberType = memberType;
        MemberId = memberId;
        AddedByUserId = addedByUserId;
        AddedAt = nowUtc;
        StatusChangedAt = nowUtc;
        Status = CampaignMemberStatus.Added;
    }

    public Guid CampaignId { get; private set; }

    public CampaignMemberType MemberType { get; private set; }

    /// <summary>Sales lead/kişi kimliği (yumuşak bağ: kayıt silinse de üyelik kalır).</summary>
    public Guid MemberId { get; private set; }

    public CampaignMemberStatus Status { get; private set; }

    public DateTime AddedAt { get; private set; }

    /// <summary>Durum son değiştiğinde (ekleme anı dahil) UTC.</summary>
    public DateTime StatusChangedAt { get; private set; }

    public Guid? AddedByUserId { get; private set; }

    public bool IsLocked => Status == CampaignMemberStatus.Converted;

    /// <summary>Yeni üyelik: durum <c>added</c>, <c>addedAt = statusChangedAt = nowUtc</c>.</summary>
    public static CampaignMember Create(Guid tenantId, Guid campaignId, CampaignMemberType memberType, Guid memberId, Guid? addedByUserId, DateTime nowUtc) =>
        new(Guid.CreateVersion7(), Guard.NotDefault(tenantId), Guard.NotDefault(campaignId), memberType, Guard.NotDefault(memberId), addedByUserId, nowUtc);

    /// <summary>
    /// Elle durum değişikliği: <c>added|sent|responded|unsubscribed</c> (birbirine serbest). <c>converted</c> hedefi
    /// <c>validation</c> (yalnız <c>LeadConverted</c> ile olur); <c>converted</c> üye <see cref="MemberStatusChange.Locked"/> döner.
    /// </summary>
    public Result<MemberStatusChange> ChangeStatus(CampaignMemberStatus target, DateTime nowUtc)
    {
        if (target == CampaignMemberStatus.Converted)
        {
            return Error.Validation(MarketingErrors.ManualConvertedStatus);
        }

        if (IsLocked)
        {
            return MemberStatusChange.Locked;
        }

        if (target == Status)
        {
            return MemberStatusChange.Unchanged;
        }

        Status = target;
        StatusChangedAt = nowUtc;
        return MemberStatusChange.Changed;
    }

    /// <summary>
    /// <c>LeadConverted</c> ile otomatik dönüşüm: yalnız <c>lead</c> üyeliği <c>converted</c> olur; zaten dönüşmüşse dokunulmaz
    /// (idempotent). Durum gerçekten değiştiyse true döner.
    /// </summary>
    public bool MarkConverted(DateTime nowUtc)
    {
        if (MemberType != CampaignMemberType.Lead || IsLocked)
        {
            return false;
        }

        Status = CampaignMemberStatus.Converted;
        StatusChangedAt = nowUtc;
        return true;
    }
}
