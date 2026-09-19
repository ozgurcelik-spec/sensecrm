namespace Sense.Crm.Modules.Service.Contracts;

/// <summary>
/// Service varlık türlerinin denetim kaydındaki adları (CLR tip adı) ve kayıt bazlı okuma izinleri. <c>Case</c> ve
/// <c>CaseComment</c> <c>crm.cases.read</c> ile okunur; <c>SlaPolicy</c> bildirilmez (yalnız <c>org.audit.read</c>).
/// <c>CaseEvent</c> ve <c>CaseCounter</c> bilinçli olarak denetim dışıdır (değişmez olay günlüğü / teknik sayaç).
/// </summary>
public static class ServiceAuditEntities
{
    public const string Case = "Case";
    public const string CaseComment = "CaseComment";
    public const string SlaPolicy = "SlaPolicy";

    public static IReadOnlyDictionary<string, string> ReadPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Case] = ServicePermissions.CasesRead,
        [CaseComment] = ServicePermissions.CasesRead,
    };
}
