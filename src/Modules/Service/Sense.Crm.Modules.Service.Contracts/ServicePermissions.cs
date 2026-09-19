using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Service.Contracts;

/// <summary>
/// Service (servis/destek) modülü izinleri: <c>crm.cases.read</c> / <c>crm.cases.write</c> (yazma izni okumayı içermez).
/// Grup istemcinin izin ekranında <c>crm</c> başlığıdır. SLA ayarı <c>org.settings.manage</c>, raporlar <c>crm.reports.read</c>
/// (Identity.Contracts'ta) izinleriyle korunur.
/// </summary>
public static class ServicePermissions
{
    public const string Module = "cases";
    public const string Group = "crm";

    public const string CasesRead = "crm.cases.read";
    public const string CasesWrite = "crm.cases.write";

    /// <summary>All Service module permissions.</summary>
    public static IReadOnlyList<Permission> All { get; } =
    [
        new(CasesRead, Module, Group),
        new(CasesWrite, Module, Group),
    ];
}
