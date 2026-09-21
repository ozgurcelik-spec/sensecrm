using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Files.Infrastructure.Jobs;

namespace Sense.Crm.Migrator;

/// <summary>
/// Files (M8C) Migrator komutu: <c>files-reconcile [--dry-run] [--tenant &lt;id&gt;]</c> — nesne deposu ↔ veritabanı uzlaştırması (Worker <c>FilesReconciliationService</c> ile aynı kod).
/// Yedekten geri yükleme sonrası: önce <c>erase-deleted-tenants</c>, sonra <c>files-reconcile --dry-run</c> çıktısı gözden geçirilip gerçek koşu. Yetim silme güvenlik supabı
/// (<c>MaxOrphanDeletePerRun</c>/<c>MaxOrphanDeleteFraction</c>) yanlış geri yüklemede toplu silmeyi durdurur. Çıktıda yalnız sayılar vardır (dosya adı yok).
/// </summary>
internal static class FilesCommands
{
    public const string ReconcileName = "files-reconcile";

    public static async Task<int> ReconcileAsync(string[] args, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        Guid? tenantId = null;
        var index = Array.FindIndex(args, a => string.Equals(a, "--tenant", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (index + 1 >= args.Length || !Guid.TryParse(args[index + 1], out var parsed))
            {
                logger.LogError("files-reconcile: --tenant requires a tenant id (GUID).");
                return 2;
            }

            tenantId = parsed;
        }

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<FilesReconciliationJob>()
            .RunAsync(new ReconcileRunOptions(dryRun, tenantId), ct).ConfigureAwait(false);

        foreach (var t in result.Tenants)
        {
            logger.LogInformation(
                "files-reconcile{Mode} tenant {TenantId}: objects={Objects} rows={Rows} missing={Missing} restored={Restored} sizeMismatch={Mismatch} orphans={Orphans} orphansDeleted={Deleted} guardTripped={Guard} foreign={Foreign} recordMissing+={RecordMissing} recordMissing-={RecordRestored} swept={Swept}",
                dryRun ? " (dry-run)" : string.Empty,
                t.TenantId,
                t.Objects,
                t.Rows,
                t.MarkedMissing,
                t.RestoredReady,
                t.SizeMismatch,
                t.OrphansFound,
                t.OrphansDeleted,
                t.OrphanGuardTripped,
                t.ForeignObjectsSkipped,
                t.RecordMissingMarked,
                t.RecordMissingCleared,
                t.SweptDeleted);
        }

        logger.LogInformation(
            "files-reconcile{Mode}: {Tenants} tenant(s) processed, {Failed} failed.",
            dryRun ? " (dry-run)" : string.Empty,
            result.Tenants.Count.ToString(CultureInfo.InvariantCulture),
            result.Failed.ToString(CultureInfo.InvariantCulture));
        return result.Failed > 0 ? 1 : 0;
    }
}
