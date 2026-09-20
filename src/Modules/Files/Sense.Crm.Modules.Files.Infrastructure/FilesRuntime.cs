using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Modules.Files.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Files.Infrastructure;

/// <summary>
/// Files kullanım sayaçları (M7 entegrasyonu): <c>files.files</c> (adet: <c>ready|quarantined|missing</c>; yumuşak silinen ve başka kiracı <b>hariç</b>) ve
/// <c>files.storage_bytes</c>. <c>files.records</c> <b>yoktur</b> (kayıt limiti değil). <b>Kiracı kapsamında</b> çalışır (global kiracı filtresi altında). Depolama limiti
/// zorlaması <c>LimitGuard</c>'ın bu sayaçtan okuduğu <b>kesin</b> toplamdır (aynı bağlantı/işlem → kiracı başına istişari kilit altında).
/// </summary>
public sealed class FilesUsageReporter(FilesDbContext db) : IUsageReporter
{
    public string Module => FilesDbContext.SchemaName;

    public async Task<IReadOnlyList<UsageMetric>> ReportAsync(CancellationToken ct = default)
    {
        var live = db.Attachments.AsNoTracking().Where(a => a.State != FileState.Deleted);
        var count = await live.LongCountAsync(ct).ConfigureAwait(false);
        var bytes = await live.SumAsync(a => (long?)a.SizeBytes, ct).ConfigureAwait(false) ?? 0;
        return
        [
            new UsageMetric("files.files", count),
            new UsageMetric("files.storage_bytes", bytes),
        ];
    }
}

/// <summary>
/// KVKK imhası, sıra 95 (M7 uzantısı): <c>workflows-conductor</c> (90) sonrası, <c>module:files</c> (100) öncesi. <c>BeginScope(tenantId)</c> → <c>DeletePrefixAsync</c>
/// (listele + toplu sil; boşalana kadar, iterasyon üst sınırlı) → <b>doğrulama:</b> önek altında <c>ListAsync</c> 0 nesne değilse istisna (adım <c>failed</c>, M7 yeniden deneme).
/// Satırlar ve <c>file_access_log</c> <c>TenantDataEraser&lt;FilesDbContext&gt;</c> ile (sıra 100) gider; denetim <c>audit</c> adımında. Rapora <c>files.objects</c> sayısı girer.
/// </summary>
public sealed class FilesObjectEraser(IFileStorage storage, ITenantContextSetter tenantSetter) : ITenantDataEraser
{
    private const int MaxIterations = 1000;

    public string Name => "files-objects";

    public int Order => 95;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        using var scope = tenantSetter.BeginScope(tenantId);
        long deleted = 0;
        for (var i = 0; i < MaxIterations; i++)
        {
            var batch = await storage.DeletePrefixAsync(tenantId, ct).ConfigureAwait(false);
            deleted += batch;
            if (batch == 0)
            {
                break;
            }
        }

        await foreach (var _ in storage.ListAsync(tenantId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Object storage still holds objects under the tenant prefix after erasure.");
        }

        return new EraseReport(new Dictionary<string, long> { ["files.objects"] = deleted });
    }

    /// <summary>
    /// C-SEC2 M2 doğrulama kancası: tombstone yazılmadan önce nesne deposunda kiracı önekinin altında nesne kalmadığını bağımsız olarak yeniden denetler (adım bittikten sonra bir yarış ya da
    /// geri yüklenen nesne varsa talep <c>erasure.verification_failed</c> ile <c>failed</c> kalır). Kişisel veri döndürmez (yalnız sayı).
    /// </summary>
    public async Task<EraseVerification> VerifyErasedAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var scope = tenantSetter.BeginScope(tenantId);
        var remaining = 0;
        await foreach (var _ in storage.ListAsync(tenantId, ct).ConfigureAwait(false))
        {
            remaining++;
            if (remaining >= 1000)
            {
                break;
            }
        }

        return remaining == 0 ? EraseVerification.Clean : EraseVerification.Failed($"files.objects>={remaining}");
    }
}

/// <summary>
/// <c>/health</c> tam raporunun <c>storage</c> denetimi: erişim + kova + (gerekirse) varsayılan şifreleme. Depo kapalıyken <c>Degraded</c> — <b>ready etiketi yoktur</b>
/// (depo arızası CRM'nin geri kalanını hazır-değil yapmaz; dosya uçları <c>503 file.storage_unavailable</c> döner).
/// </summary>
public sealed class FilesStorageHealthCheck(IFileStorage storage, ILogger<FilesStorageHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await storage.CheckAsync(cancellationToken).ConfigureAwait(false);
            return health.Healthy ? HealthCheckResult.Healthy(health.Detail) : HealthCheckResult.Degraded(health.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Object storage health check failed");
            return HealthCheckResult.Degraded("object storage check failed");
        }
    }
}
