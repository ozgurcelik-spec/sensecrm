using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Platform.Infrastructure.Jobs;

namespace Sense.Crm.Migrator;

/// <summary>
/// Platform (M7) Migrator komutları:
/// <list type="bullet">
/// <item><c>sync-plans</c>: <c>Platform:Plans</c> → <c>platform.plans</c> idempotent upsert (geçersiz yapılandırma → çıkış 3, yayın durur; <c>migrate</c> da çağırır).</item>
/// <item><c>backfill</c>: satırı olmayan mevcut her kiracıya <c>internal</c> plan (<c>source = backfill</c>, denemesiz, onboarding kapalı); idempotent (<c>migrate</c> sonunda da çalışır).</item>
/// <item><c>erase-deleted-tenants</c>: tüm mezar taşları (<c>deleted</c>) için imhayı yeniden koşar — yedekten geri yükleme sonrası "imha edilmiş kiracı geri gelmesin" adımı (idempotent).</item>
/// </list>
/// </summary>
internal static class PlatformCommands
{
    public const string SyncPlansName = "sync-plans";
    public const string BackfillName = "backfill";
    public const string EraseDeletedTenantsName = "erase-deleted-tenants";
    private const int InvalidConfigurationExitCode = 3;

    public static async Task<int> SyncPlansAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<PlanSynchronizer>().SyncAsync(ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                logger.LogError("Platform plan catalog: {Error}", error);
            }

            return InvalidConfigurationExitCode;
        }

        logger.LogInformation("Platform plans synchronized: {Created} created, {Updated} updated, {Deactivated} deactivated.", result.Created, result.Updated, result.Deactivated);
        return 0;
    }

    public static async Task BackfillAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<AccountBackfill>().RunAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Platform account backfill: {Created} tenant(s) assigned to the internal plan.", created);
    }

    public static async Task EraseDeletedTenantsAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<DeletedTenantsReplay>().RunAsync(ct).ConfigureAwait(false);
        logger.LogInformation("erase-deleted-tenants: {Rows} row(s) erased for tombstoned tenants.", rows);
    }
}
