using Crm.Shared.Contracts.Security;

namespace Crm.Modules.Commerce.Contracts;

/// <summary>
/// Commerce modülü izinleri (docs/plan/m6a-ticaret.md). Anahtar dizgeleri sözleşmedir (rollerde saklanır, istemci kullanır).
/// Grup istemcinin izin ekranında <c>crm</c> başlığıdır. Standard sistem rolü tüm <c>crm.*</c> izinlerini prefix ile otomatik alır.
/// </summary>
public static class CommercePermissions
{
    public const string Module = "commerce";
    public const string Group = "crm";

    public const string ProductsRead = "crm.products.read";
    public const string ProductsWrite = "crm.products.write";
    public const string QuotesRead = "crm.quotes.read";
    public const string QuotesWrite = "crm.quotes.write";
    public const string OrdersRead = "crm.orders.read";
    public const string OrdersWrite = "crm.orders.write";

    /// <summary>All Commerce module permissions.</summary>
    public static IReadOnlyList<Permission> All { get; } =
    [
        new(ProductsRead, Module, Group),
        new(ProductsWrite, Module, Group),
        new(QuotesRead, Module, Group),
        new(QuotesWrite, Module, Group),
        new(OrdersRead, Module, Group),
        new(OrdersWrite, Module, Group),
    ];
}
