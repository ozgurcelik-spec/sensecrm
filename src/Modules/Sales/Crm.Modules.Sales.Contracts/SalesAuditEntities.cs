namespace Crm.Modules.Sales.Contracts;

/// <summary>Sales varlık türlerinin denetim kaydındaki adları (CLR tip adı) ve kayıt bazlı okuma izinleri.</summary>
public static class SalesAuditEntities
{
    public const string Account = "Account";
    public const string Contact = "Contact";
    public const string Lead = "Lead";
    public const string Deal = "Deal";
    public const string Pipeline = "Pipeline";
    public const string PipelineStage = "PipelineStage";

    public static IReadOnlyDictionary<string, string> ReadPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Account] = SalesPermissions.AccountsRead,
        [Contact] = SalesPermissions.ContactsRead,
        [Lead] = SalesPermissions.LeadsRead,
        [Deal] = SalesPermissions.DealsRead,
        [Pipeline] = SalesPermissions.DealsRead,
        [PipelineStage] = SalesPermissions.DealsRead,
    };
}
