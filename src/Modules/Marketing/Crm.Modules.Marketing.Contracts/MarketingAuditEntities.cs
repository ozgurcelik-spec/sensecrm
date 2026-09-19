namespace Crm.Modules.Marketing.Contracts;

/// <summary>Marketing varlık türlerinin denetim kaydındaki adları (CLR tip adı) ve kayıt bazlı okuma izinleri.</summary>
public static class MarketingAuditEntities
{
    public const string Campaign = "Campaign";
    public const string CampaignMember = "CampaignMember";

    public static IReadOnlyDictionary<string, string> ReadPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Campaign] = MarketingPermissions.Read,
        [CampaignMember] = MarketingPermissions.Read,
    };
}
