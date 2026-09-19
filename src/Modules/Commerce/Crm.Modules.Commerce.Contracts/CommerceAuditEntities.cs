namespace Crm.Modules.Commerce.Contracts;

/// <summary>Commerce varlık türlerinin denetim kaydındaki adları (CLR tip adı) ve kayıt bazlı okuma izinleri.</summary>
public static class CommerceAuditEntities
{
    public const string Product = "Product";
    public const string Quote = "Quote";
    public const string SalesOrder = "SalesOrder";

    public static IReadOnlyDictionary<string, string> ReadPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Product] = CommercePermissions.ProductsRead,
        [Quote] = CommercePermissions.QuotesRead,
        [SalesOrder] = CommercePermissions.OrdersRead,
    };
}
