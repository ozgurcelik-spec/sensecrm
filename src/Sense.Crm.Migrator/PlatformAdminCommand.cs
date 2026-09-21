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
    public const string RevokeName = "revoke-platform-admin";

    private const string EmailVariable = "PLATFORM_ADMIN_EMAIL";
    private const string PasswordVariable = "PLATFORM_ADMIN_PASSWORD";
    private const string PasswordFileVariable = "PLATFORM_ADMIN_PASSWORD_FILE";
    private const string NameVariable = "PLATFORM_ADMIN_NAME";
    private const string OrganizationVariable = "PLATFORM_ORG_NAME";
    private const int InvalidInputExitCode = 2;
    private const int LastAdminExitCode = 4;
    private const string DeactivateVariable = "PLATFORM_ADMIN_DEACTIVATE";

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

        if (result.Outcome is PlatformAdminOutcome.Created or PlatformAdminOutcome.Promoted or PlatformAdminOutcome.Unchanged)
        {
            // M7: işletim organizasyonu için Platform hesabı doğrudan is_system + internal plan yazılır (Worker'ı beklemez).
            // C-SEC2 H1: Created için yeni işletim organizasyonu; Promoted/Unchanged için hesabın aktif üyeliği olan kiracılar (eski kurulumda bayrak eksik olabilir) da işaretlenir.
            var syncExit = await PlatformCommands.SyncPlansAsync(services, logger, cancellationToken).ConfigureAwait(false);
            if (syncExit != 0)
            {
                return syncExit;
            }

            var tenantIds = new List<Guid>();
            if (result.TenantId is { } operatingTenantId)
            {
                tenantIds.Add(operatingTenantId);
            }

            if (result.UserId is { } adminUserId)
            {
                tenantIds.AddRange(await scope.ServiceProvider.GetRequiredService<IPlatformAdminDirectory>().ListActiveTenantsOfUserAsync(adminUserId, cancellationToken).ConfigureAwait(false));
            }

            var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            var provisioner = scope.ServiceProvider.GetRequiredService<Sense.Crm.Modules.Platform.Application.Provisioning.AccountProvisioner>();
            foreach (var tenantId in tenantIds.Distinct())
            {
                if (await directory.FindAsync(tenantId, cancellationToken).ConfigureAwait(false) is { } info)
                {
                    await provisioner.EnsureSystemAsync(info, cancellationToken).ConfigureAwait(false);
                }
            }

            await scope.ServiceProvider.GetRequiredService<Sense.Crm.Modules.Platform.Application.IPlatformUnitOfWork>().SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        switch (result.Outcome)
        {
            case PlatformAdminOutcome.Created:
                logger.LogInformation("Platform admin created (with its own '{Organization}' organization). The account must change its password at the first login.", Environment.GetEnvironmentVariable(OrganizationVariable) ?? PlatformAdminBootstrapper.DefaultOrganizationName);
                ClearPasswordFile(logger);
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

    /// <summary>
    /// <c>revoke-platform-admin</c> (C-SEC2 M6): <c>PLATFORM_ADMIN_EMAIL</c> hesabının platform yöneticisi yetkisini geri alır ve tüm oturumlarını kapatır;
    /// <c>PLATFORM_ADMIN_DEACTIVATE=true</c> hesabı da pasifleştirir. Son aktif platform yöneticisi geri alınamaz (çıkış 4). Idempotent değildir: yönetici olmayan hesap için çıkış 2.
    /// </summary>
    public static async Task<int> RevokeAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var email = Environment.GetEnvironmentVariable(EmailVariable);
        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogError("revoke-platform-admin: PLATFORM_ADMIN_EMAIL is required.");
            return InvalidInputExitCode;
        }

        var user = await scope.ServiceProvider.GetRequiredService<Sense.Crm.Modules.Identity.Domain.IUserRepository>().GetByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            logger.LogError("revoke-platform-admin: no such account.");
            return InvalidInputExitCode;
        }

        var deactivate = string.Equals(Environment.GetEnvironmentVariable(DeactivateVariable), "true", StringComparison.OrdinalIgnoreCase);
        var outcome = await scope.ServiceProvider.GetRequiredService<IPlatformAdminManager>().RevokeAsync(user.Id, deactivate, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case PlatformAdminRevocation.Revoked:
                logger.LogInformation("Platform admin revoked (deactivated: {Deactivated}); all sessions of the account were closed.", deactivate);
                return 0;
            case PlatformAdminRevocation.LastActiveAdmin:
                logger.LogError("revoke-platform-admin: refused, this is the last active platform admin (create another one first).");
                return LastAdminExitCode;
            default:
                logger.LogError("revoke-platform-admin: the account is not a platform admin.");
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
    /// C-SEC2 M6: hesap parola <b>dosyasından</b> okunarak oluşturulduysa dosya başarıdan sonra boşaltılır (parola diskte kalmasın). Dosya salt-okunur bağlanmışsa (Docker secret)
    /// uyarı verilir ve elle boşaltılması istenir; komut başarısız sayılmaz (hesap zaten oluştu ve parola ilk girişte değiştirilmek zorundadır).
    /// </summary>
    private static void ClearPasswordFile(ILogger logger)
    {
        var file = Environment.GetEnvironmentVariable(PasswordFileVariable);
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file) || string.IsNullOrWhiteSpace(File.ReadAllText(file)))
        {
            return;
        }

        try
        {
            File.WriteAllText(file, string.Empty);
            logger.LogInformation("The initial password file was emptied.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The initial password file could not be emptied ({Reason}); empty it by hand (the password must also be changed at the first login).", ex.GetType().Name);
        }
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
