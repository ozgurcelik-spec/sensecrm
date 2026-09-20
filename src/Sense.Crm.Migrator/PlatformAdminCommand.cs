using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Activities.Contracts;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Application.Provisioning;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Workflows.Contracts;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Migrator;

/// <summary>
/// <c>create-platform-admin</c>: ilk platform yöneticisini ortam değişkenlerinden oluşturur (idempotent). Parola komut satırına
/// yazılmaz: <c>PLATFORM_ADMIN_PASSWORD</c> veya <c>PLATFORM_ADMIN_PASSWORD_FILE</c> (Docker secret). İsteğe bağlı:
/// <c>PLATFORM_ADMIN_NAME</c>, <c>PLATFORM_ORG_NAME</c> (varsayılan "Platform").
/// </summary>
internal static class PlatformAdminCommand
{
    public const string Name = "create-platform-admin";

    private const string EmailVariable = "PLATFORM_ADMIN_EMAIL";
    private const string PasswordVariable = "PLATFORM_ADMIN_PASSWORD";
    private const string PasswordFileVariable = "PLATFORM_ADMIN_PASSWORD_FILE";
    private const string NameVariable = "PLATFORM_ADMIN_NAME";
    private const string OrganizationVariable = "PLATFORM_ORG_NAME";
    private const int InvalidInputExitCode = 2;

    public static async Task<int> RunAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapper>();

        var result = await bootstrapper.EnsureAsync(
            Environment.GetEnvironmentVariable(EmailVariable),
            ReadPassword(),
            Environment.GetEnvironmentVariable(NameVariable),
            Environment.GetEnvironmentVariable(OrganizationVariable),
            cancellationToken).ConfigureAwait(false);

        if (result.Outcome == PlatformAdminOutcome.Created && result.TenantId is { } operatingTenantId)
        {
            // M7: işletim organizasyonu için Platform hesabı doğrudan is_system + internal plan yazılır (Worker'ı beklemez).
            var syncExit = await PlatformCommands.SyncPlansAsync(services, logger, cancellationToken).ConfigureAwait(false);
            if (syncExit != 0)
            {
                return syncExit;
            }

            var info = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindAsync(operatingTenantId, cancellationToken).ConfigureAwait(false);
            if (info is not null)
            {
                await scope.ServiceProvider.GetRequiredService<Sense.Crm.Modules.Platform.Application.Provisioning.AccountProvisioner>().EnsureSystemAsync(info, cancellationToken).ConfigureAwait(false);
                await scope.ServiceProvider.GetRequiredService<Sense.Crm.Modules.Platform.Application.IPlatformUnitOfWork>().SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        switch (result.Outcome)
        {
            case PlatformAdminOutcome.Created:
                logger.LogInformation("Platform admin created (with its own '{Organization}' organization).", Environment.GetEnvironmentVariable(OrganizationVariable) ?? PlatformAdminBootstrapper.DefaultOrganizationName);
                return 0;
            case PlatformAdminOutcome.Promoted:
                logger.LogInformation("Existing account promoted to platform admin (password unchanged).");
                return 0;
            case PlatformAdminOutcome.Unchanged:
                logger.LogInformation("Platform admin already exists; nothing to do.");
                return 0;
            default:
                logger.LogError("create-platform-admin: {Problem}", result.Problem);
                return InvalidInputExitCode;
        }
    }

    private static string? ReadPassword()
    {
        // Dosya (Docker secret) önceliklidir; boşsa (bootstrap sonrası boşaltılmış) ortam değişkenine düşülür. Değer asla loglanmaz.
        var file = Environment.GetEnvironmentVariable(PasswordFileVariable);
        var fromFile = !string.IsNullOrWhiteSpace(file) && File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        return string.IsNullOrEmpty(fromFile) ? Environment.GetEnvironmentVariable(PasswordVariable) : fromFile;
    }

    /// <summary>
    /// Migrator, API'nin modül kataloğunu yüklemez; yeni organizasyonun Administrator rolü için tüm modüllerin izin listesi buradan
    /// verilir. Yeni modül eklendiğinde listeye eklenmesi gerekir; eklenmese bile API her açılışta sistem rollerini katalogla eşitler
    /// (<c>SystemRolePermissionSynchronizer</c>).
    /// </summary>
    internal sealed class StaticPermissionCatalog : IPermissionCatalog
    {
        private readonly IReadOnlyList<Permission> _all = OrgPermissions.All
            .Concat(CrmPermissions.All)
            .Concat(SalesPermissions.All)
            .Concat(ActivitiesPermissions.All)
            .Concat(WorkflowsPermissions.All)
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(p => p.Group, StringComparer.Ordinal)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        public IReadOnlyList<Permission> All => _all;

        public bool Exists(string permission) => _all.Any(p => string.Equals(p.Key, permission, StringComparison.Ordinal));
    }
}
