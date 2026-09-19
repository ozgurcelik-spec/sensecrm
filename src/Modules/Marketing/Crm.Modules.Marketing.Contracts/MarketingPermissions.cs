using Crm.Shared.Contracts.Security;

namespace Crm.Modules.Marketing.Contracts;

/// <summary>
/// Marketing modülü izinleri (docs/plan/m6c-pazarlama.md). Anahtar dizgeleri <b>sözleşmedir</b> (rollerde saklanır, istemci kullanır);
/// grup istemcinin izin ekranında <c>crm</c> başlığıdır. Pazarlama raporu ortak <c>crm.reports.read</c> iznini kullanır (Identity.Contracts).
/// </summary>
public static class MarketingPermissions
{
    public const string Module = "marketing";
    public const string Group = "crm";

    public const string Read = "crm.campaigns.read";
    public const string Write = "crm.campaigns.write";

    /// <summary>All Marketing module permissions.</summary>
    public static IReadOnlyList<Permission> All { get; } =
    [
        new(Read, Module, Group),
        new(Write, Module, Group),
    ];
}
