using Crm.Shared.Contracts.Security;

namespace Crm.Modules.Sales.Contracts;

/// <summary>
/// Sales modülü izinleri. Anahtar dizgeleri Milestone 1'de Identity.Contracts'ta tanımlanmıştı ve <b>sözleşmedir</b>
/// (rollerde saklanır, istemci kullanır); burada yalnızca sahipliği modüle geçti. Grup istemcinin izin ekranında
/// <c>crm</c> başlığıdır.
/// </summary>
public static class SalesPermissions
{
    public const string Module = "sales";
    public const string Group = "crm";

    public const string AccountsRead = "crm.accounts.read";
    public const string AccountsWrite = "crm.accounts.write";
    public const string ContactsRead = "crm.contacts.read";
    public const string ContactsWrite = "crm.contacts.write";
    public const string LeadsRead = "crm.leads.read";
    public const string LeadsWrite = "crm.leads.write";
    public const string DealsRead = "crm.deals.read";
    public const string DealsWrite = "crm.deals.write";

    /// <summary>All Sales module permissions.</summary>
    public static IReadOnlyList<Permission> All { get; } =
    [
        new(AccountsRead, Module, Group),
        new(AccountsWrite, Module, Group),
        new(ContactsRead, Module, Group),
        new(ContactsWrite, Module, Group),
        new(LeadsRead, Module, Group),
        new(LeadsWrite, Module, Group),
        new(DealsRead, Module, Group),
        new(DealsWrite, Module, Group),
    ];
}
