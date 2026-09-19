using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Activities.Contracts;

/// <summary>
/// Activities modülü izinleri. Anahtar dizgeleri Milestone 1'de Identity.Contracts'ta tanımlanmıştı ve <b>sözleşmedir</b>
/// (rollerde saklanır, istemci kullanır); burada yalnızca sahipliği modüle geçti. Grup istemcinin izin ekranında <c>crm</c> başlığıdır.
/// <c>crm.reports.read</c> (aktivite raporu dahil tüm raporlar) modüller arası ortak izin olarak Identity.Contracts'ta kalır.
/// </summary>
public static class ActivitiesPermissions
{
    public const string Module = "activities";
    public const string Group = "crm";

    public const string Read = "crm.activities.read";
    public const string Write = "crm.activities.write";

    /// <summary>All Activities module permissions.</summary>
    public static IReadOnlyList<Permission> All { get; } =
    [
        new(Read, Module, Group),
        new(Write, Module, Group),
    ];
}
