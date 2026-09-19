namespace Crm.Modules.Activities.Contracts;

/// <summary>Activities varlık türlerinin denetim kaydındaki adları (CLR tip adı) ve kayıt bazlı okuma izinleri.</summary>
public static class ActivitiesAuditEntities
{
    public const string Activity = "Activity";

    public static IReadOnlyDictionary<string, string> ReadPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Activity] = ActivitiesPermissions.Read,
    };
}
