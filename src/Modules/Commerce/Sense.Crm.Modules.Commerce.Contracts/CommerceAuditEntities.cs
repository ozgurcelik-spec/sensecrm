namespace Sense.Crm.Modules.Commerce.Contracts;

/// <summary>Commerce varlık türlerinin denetim kaydındaki adları (CLR tip adı) ve kayıt bazlı okuma izinleri.</summary>
public static class CommerceAuditEntities
{
    public const string Product = "Product";
    public const string Quote = "Quote";
    public const string SalesOrder = "SalesOrder";
    public const string Invoice = "Invoice";
    public const string PurchaseOrder = "PurchaseOrder";
    public const string Vendor = "Vendor";
    public const string PriceBook = "PriceBook";
    public const string PriceBookEntry = "PriceBookEntry";

    public static IReadOnlyDictionary<string, string> ReadPermissions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Product] = CommercePermissions.ProductsRead,
        [Quote] = CommercePermissions.QuotesRead,
        [SalesOrder] = CommercePermissions.OrdersRead,
        [Invoice] = CommercePermissions.InvoicesRead,
        [PurchaseOrder] = CommercePermissions.PurchaseOrdersRead,
        [Vendor] = CommercePermissions.VendorsRead,
        [PriceBook] = CommercePermissions.PriceBooksRead,
        [PriceBookEntry] = CommercePermissions.PriceBooksRead,
    };
}
