using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Contracts;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Contracts.Retention;

namespace Sense.Crm.Modules.Platform.Infrastructure.Jobs;

/// <summary>Silme işi sonucu.</summary>
public sealed record TenantErasureRun(int Completed, int Failed);

/// <summary>
/// KVKK imha işi (Worker <c>TenantErasureService</c> her <c>Platform:Deletion:PollMinutes</c> dakikada çağırır). Kuyruk: (a) <c>scheduled</c> ve
/// <c>scheduled_for &lt;= now</c>; (b) <c>running|failed</c> ve <c>attempts &lt; MaxAttempts</c> (üstel bekleme).
/// <para>
/// <b>Yıkıcı işlemden önce yeniden doğrulama (C-SEC2 M1):</b> her talep için <c>SELECT … FOR UPDATE SKIP LOCKED</c> ile satır kilitlenir (eşzamanlı iptal/başka Worker atlanır)
/// ve aynı işlemde şunlar doğrulanır: durum (<c>scheduled|running|failed</c>), <c>scheduled_for &lt;= now</c> (scheduled ise), hesap <c>pending_deletion</c>, hesap sistem kiracısı
/// <b>değil</b> ve aktif platform yöneticisi üyesi yok. Kiracı korunan hale geldiyse talep sistemce iptal edilir (hesap eski durumuna döner); diğer ön koşul bozuklukları
/// <c>erasure.precondition_failed</c> ile kalıcı başarısız yapılır. <c>DeletionRequest</c> <c>xmin</c> eşzamanlılık belirteci taşır: iptal ↔ başlatma yarışını kaybeden yazma 409 alır.
/// Adımlar (<see cref="ITenantDataEraser"/>) <c>Order</c> sırasıyla, her biri idempotent; tamamlanan adım <c>erased_steps</c>'e yazılır. Her adımdan önce koruma yeniden denetlenir.
/// </para>
/// <para>
/// <b>Tombstone öncesi doğrulama (M2):</b> kullanım anlık görüntüleri silinir, serbest metin gerekçeler redakte edilir (L1), sonra (a) her adımın <c>VerifyErasedAsync</c> kancası
/// ve (b) <c>information_schema</c> taraması (<see cref="TenantErasureVerifier"/>) kiracıya ait kalıntı arar; kalıntı varsa <c>erasure.verification_failed</c> ile adım başarısız olur,
/// talep <c>failed</c> kalır, tombstone yazılmaz. Başarıda hesap <c>deleted</c> + ad/slug/askı gerekçesi redakte, talep <c>completed</c> + rapor, <c>TenantErased</c> olayı,
/// <c>deletion.completed</c> denetimi.
/// </para>
/// </summary>
public sealed partial class TenantErasureJob(IServiceScopeFactory scopes, IOptions<PlatformOptions> options, TimeProvider clock, ILogger<TenantErasureJob> logger)
{
    public const string TombstoneStep = "platform-tombstone";

    private enum Claim
    {
        Skipped,
        Claimed,
        Aborted,
        Failed,
    }

    public async Task<TenantErasureRun> RunOnceAsync(CancellationToken ct)
    {
        var settings = options.Value.Deletion;
        var now = clock.GetUtcNow().UtcDateTime;

        List<Guid> due;
        using (var listScope = scopes.CreateScope())
        {
            var db = listScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var candidates = await db.DeletionRequests.AsNoTracking()
                .Where(r => (r.Status == DeletionStatuses.Scheduled && r.ScheduledFor <= now)
                    || ((r.Status == DeletionStatuses.Running || r.Status == DeletionStatuses.Failed) && r.Attempts < settings.MaxAttempts))
                .OrderBy(r => r.ScheduledFor)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            due = candidates.Where(r => r.Status != DeletionStatuses.Failed || BackoffElapsed(r.Attempts, r.ModifiedDate ?? r.CreatedAt, now)).Select(r => r.Id).ToList();
        }

        int completed = 0, failed = 0;
        foreach (var requestId in due)
        {
            if (await ProcessAsync(requestId, ct).ConfigureAwait(false))
            {
                completed++;
            }
            else
            {
                failed++;
            }
        }

        return new TenantErasureRun(completed, failed);
    }

    /// <summary>Başarısız denemeden sonra üstel bekleme: 1 dk × 2^(attempts−1), en çok 30 dk.</summary>
    public static bool BackoffElapsed(int attempts, DateTime lastChangeUtc, DateTime nowUtc)
    {
        var minutes = Math.Min(Math.Pow(2, Math.Max(attempts - 1, 0)), 30);
        return nowUtc >= lastChangeUtc.AddMinutes(minutes);
    }

    /// <returns>false yalnızca bu turda başarısız bir adım/doğrulama olduysa; atlanan ya da sistemce iptal edilen talep başarısızlık sayılmaz.</returns>
    private async Task<bool> ProcessAsync(Guid requestId, CancellationToken ct)
    {
        var settings = options.Value.Deletion;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PlatformDbContext>();
        var audit = sp.GetRequiredService<IPlatformAudit>();
        var outbox = sp.GetRequiredService<IIntegrationEventOutbox>();
        var cache = sp.GetRequiredService<IEntitlementCache>();
        var admins = sp.GetRequiredService<IPlatformAdminDirectory>();

        // ---- 1) Talebi kilitle, ön koşulları yeniden doğrula, başlat (tek kısa işlem). --------------------------------------------------------------------------------
        Domain.Deletion.DeletionRequest? request = null;
        Domain.Accounts.TenantAccount? account = null;
        var claim = Claim.Skipped;
        await db.ExecuteInTransactionAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                request = null;
                account = null;
                claim = Claim.Skipped;
                var now = clock.GetUtcNow().UtcDateTime;

                if (!await TryLockAsync(db, requestId, token).ConfigureAwait(false))
                {
                    return; // eşzamanlı iptal/başka Worker bu satırı tutuyor (ya da silinmiş): bir sonraki turda
                }

                var locked = await db.DeletionRequests.FirstOrDefaultAsync(r => r.Id == requestId, token).ConfigureAwait(false);
                if (locked is null || !locked.IsRunnable(now, settings.MaxAttempts))
                {
                    return; // iptal edilmiş/tamamlanmış/vadesi gelmemiş
                }

                var tenantId = locked.TenantId;
                var lockedAccount = await db.TenantAccounts.FirstOrDefaultAsync(a => a.Id == tenantId, token).ConfigureAwait(false);
                if (lockedAccount is null)
                {
                    LogSkipped(logger, tenantId);
                    return;
                }

                // Sistem kiracısı: talep hiç oluşamaz (işleyici reddeder); doğrudan SQL ile zorlanmış talep bile işlenmez ve olduğu gibi bırakılır (davranış M7'den beri aynı).
                if (lockedAccount.IsSystem)
                {
                    LogSkipped(logger, tenantId);
                    return;
                }

                request = locked;
                account = lockedAccount;
                if (await admins.HasActivePlatformAdminAsync(tenantId, token).ConfigureAwait(false))
                {
                    AbortProtected(audit, outbox, locked, lockedAccount, now);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    claim = Claim.Aborted;
                    return;
                }

                if (lockedAccount.Status != AccountStatuses.PendingDeletion)
                {
                    locked.FailPermanently($"{TombstoneStep}: {PlatformErrors.ErasurePreconditionFailed}: account_status={lockedAccount.Status}", settings.MaxAttempts);
                    audit.Record(
                        PlatformAuditActions.DeletionFailed,
                        lockedAccount,
                        null,
                        new Dictionary<string, object?> { ["requestId"] = locked.Id, ["step"] = "precondition", ["attempts"] = locked.Attempts, ["code"] = PlatformErrors.ErasurePreconditionFailed });
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    claim = Claim.Failed;
                    return;
                }

                if (locked.Start(now).IsFailure)
                {
                    return;
                }

                await db.SaveChangesAsync(token).ConfigureAwait(false);
                claim = Claim.Claimed;
            },
            ct).ConfigureAwait(false);

        if (claim == Claim.Aborted && account is not null)
        {
            await cache.InvalidateAsync(account.TenantId, ct).ConfigureAwait(false);
            return true;
        }

        if (claim == Claim.Failed && account is not null)
        {
            LogPrecondition(logger, account.TenantId);
            return false;
        }

        if (claim != Claim.Claimed || request is null || account is null)
        {
            return true;
        }

        var tenant = account.TenantId;

        // ---- 2) Adımlar. ----------------------------------------------------------------------------------------------------------------------------------------------
        var erasers = TenantErasureSteps.Resolve(sp.GetServices<ITenantDataEraser>());
        foreach (var eraser in erasers)
        {
            if (request.HasCompleted(eraser.Name))
            {
                continue;
            }

            // Koruma her yıkıcı adımdan önce yeniden denetlenir (bu arada bir platform yöneticisi kiracıya üye olmuş olabilir).
            if (await admins.HasActivePlatformAdminAsync(tenant, ct).ConfigureAwait(false))
            {
                AbortProtected(audit, outbox, request, account, clock.GetUtcNow().UtcDateTime);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await cache.InvalidateAsync(tenant, ct).ConfigureAwait(false);
                return true;
            }

            try
            {
                var report = await eraser.EraseAsync(tenant, settings.ChunkSize, ct).ConfigureAwait(false);
                request.StepCompleted(eraser.Name, report.DeletedRows);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(db, audit, request, account, eraser.Name, ex, settings, ct).ConfigureAwait(false);
                return false;
            }
        }

        // ---- 3) Doğrulama + tombstone. --------------------------------------------------------------------------------------------------------------------------------
        try
        {
            await TombstoneAsync(sp, db, audit, outbox, admins, erasers, request, account, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var fresh = await db.DeletionRequests.FirstAsync(r => r.Id == requestId, ct).ConfigureAwait(false);
            var freshAccount = await db.TenantAccounts.FirstAsync(a => a.Id == tenant, ct).ConfigureAwait(false);
            await RecordFailureAsync(db, audit, fresh, freshAccount, TombstoneStep, ex, settings, ct).ConfigureAwait(false);
            return false;
        }

        await cache.InvalidateAsync(tenant, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary><c>SELECT … FOR UPDATE SKIP LOCKED</c>: satır alındıysa true; başka işlem tutuyorsa (iptal/başka Worker) ya da yoksa false. Geçerli işlemin bağlantısını kullanır.</summary>
    private static async Task<bool> TryLockAsync(PlatformDbContext db, Guid requestId, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException("FOR UPDATE requires an open transaction.");
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM platform.deletion_requests WHERE id = @id FOR UPDATE SKIP LOCKED";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = requestId;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }
    }

    /// <summary>Kiracı korunan hale geldi (sistem/aktif platform yöneticisi üyesi): talep sistemce iptal edilir, hesap talep öncesi durumuna döner, denetim + olay yazılır.</summary>
    private void AbortProtected(IPlatformAudit audit, IIntegrationEventOutbox outbox, Domain.Deletion.DeletionRequest request, Domain.Accounts.TenantAccount account, DateTime nowUtc)
    {
        var previous = request.PreviousStatus;
        _ = request.CancelBySystem(nowUtc, $"{PlatformErrors.ErasurePreconditionFailed}: protected_tenant");
        if (account.Status == AccountStatuses.PendingDeletion)
        {
            _ = account.RestoreAfterDeletionCancelled(previous);
        }

        audit.Record(
            PlatformAuditActions.DeletionCancelled,
            account,
            null,
            new Dictionary<string, object?> { ["status"] = new { old = AccountStatuses.PendingDeletion, @new = account.Status }, ["requestId"] = request.Id, ["by"] = "system", ["code"] = "protected_tenant" });
        outbox.Enqueue(new TenantDeletionCancelled(account.TenantId, null));
        LogAborted(logger, account.TenantId);
    }

    private async Task TombstoneAsync(
        IServiceProvider sp,
        PlatformDbContext db,
        IPlatformAudit audit,
        IIntegrationEventOutbox outbox,
        IPlatformAdminDirectory admins,
        IReadOnlyList<ITenantDataEraser> erasers,
        Domain.Deletion.DeletionRequest request,
        Domain.Accounts.TenantAccount account,
        CancellationToken ct)
    {
        var tenantId = account.TenantId;
        var requestId = request.Id;
        var now = clock.GetUtcNow();
        long snapshots = 0, redacted = 0, redactedRequests = 0;

        // Kullanım anlık görüntüleri silinir; serbest metin gerekçeler (denetim satırı details.reason, talep gerekçeleri) redakte edilir (L1). Denetim güncellemesi salt-eklemeli
        // tetikleyiciyi yalnız erasure işaretiyle ve hesap pending_deletion iken geçebilir; tek işlemde.
        await db.ExecuteInTransactionAsync(
            async token =>
            {
                await AuditMaintenance.SetLocalAsync(db, AuditMaintenance.Erasure, token).ConfigureAwait(false);
                snapshots = await db.UsageSnapshots.Where(s => s.TenantId == tenantId).ExecuteDeleteAsync(token).ConfigureAwait(false);
                redacted = await db.Database.ExecuteSqlAsync(
                    $"UPDATE platform.platform_audit_entries SET target_tenant_name = {PlatformLimits.DeletedNamePlaceholder}, details = details - 'reason' WHERE target_tenant_id = {tenantId}",
                    token).ConfigureAwait(false);
                redactedRequests = await db.Database.ExecuteSqlAsync(
                    $"UPDATE platform.deletion_requests SET reason = {PlatformLimits.RedactedReasonPlaceholder} WHERE tenant_id = {tenantId} AND reason <> {PlatformLimits.RedactedReasonPlaceholder}",
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        var freshRequest = await db.DeletionRequests.FirstAsync(r => r.Id == requestId, ct).ConfigureAwait(false);
        var freshAccount = await db.TenantAccounts.FirstAsync(a => a.Id == tenantId, ct).ConfigureAwait(false);

        // Doğrulama: her adımın kendi kancası + tüm şemalarda tenant_id kolonlu tabloların taraması. Kalıntı → tombstone yazılmaz.
        var problems = new List<string>();
        foreach (var eraser in erasers)
        {
            var verification = await eraser.VerifyErasedAsync(tenantId, ct).ConfigureAwait(false);
            problems.AddRange(verification.Problems.Select(p => $"{eraser.Name}:{p}"));
        }

        problems.AddRange(await sp.GetRequiredService<TenantErasureVerifier>().FindRemainingAsync(tenantId, ct).ConfigureAwait(false));
        if (problems.Count > 0)
        {
            throw new ErasureVerificationException(problems);
        }

        var deleted = freshAccount.MarkDeleted(now.UtcDateTime, await admins.HasActivePlatformAdminAsync(tenantId, ct).ConfigureAwait(false));
        if (deleted.IsFailure)
        {
            throw new InvalidOperationException(deleted.Error.Code);
        }

        freshRequest.RedactReason();
        freshRequest.StepCompleted(
            TombstoneStep,
            new Dictionary<string, long>
            {
                ["platform.usage_snapshots"] = snapshots,
                ["platform.platform_audit_entries(redacted)"] = redacted,
                ["platform.deletion_requests(redacted)"] = redactedRequests,
            });
        freshRequest.Complete(now.UtcDateTime);

        audit.Record(
            PlatformAuditActions.DeletionCompleted,
            freshAccount,
            null,
            new Dictionary<string, object?> { ["status"] = new { old = AccountStatuses.PendingDeletion, @new = AccountStatuses.Deleted }, ["requestId"] = freshRequest.Id, ["report"] = ParseReport(freshRequest.Report) });
        outbox.Enqueue(new TenantErased(tenantId, now));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RecordFailureAsync(
        PlatformDbContext db,
        IPlatformAudit audit,
        Domain.Deletion.DeletionRequest request,
        Domain.Accounts.TenantAccount account,
        string step,
        Exception exception,
        DeletionOptions settings,
        CancellationToken ct)
    {
        // Kişisel veri içermez: doğrulama hatası için kod + tablo/sayı listesi; diğerleri için istisna türü + kısaltılmış ileti (imha adımları kiracı kimliğiyle parametreli SQL çalıştırır).
        var message = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
        request.Fail(exception is ErasureVerificationException ? $"{step}: {message}" : $"{step}: {exception.GetType().Name}: {message}");
        if (request.Attempts >= settings.MaxAttempts)
        {
            audit.Record(
                PlatformAuditActions.DeletionFailed,
                account,
                null,
                new Dictionary<string, object?> { ["requestId"] = request.Id, ["step"] = step, ["attempts"] = request.Attempts });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        LogFailed(logger, exception, account.TenantId, step, request.Attempts);
    }

    private static JsonElement ParseReport(string? report)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(report) ? "{}" : report);
        return document.RootElement.Clone();
    }

    [LoggerMessage(EventId = 5010, Level = LogLevel.Error, Message = "Tenant erasure failed for {TenantId} at step {Step} (attempt {Attempts})")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid tenantId, string step, int attempts);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Warning, Message = "Tenant erasure skipped for {TenantId}: account missing or system tenant")]
    private static partial void LogSkipped(ILogger logger, Guid tenantId);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Warning, Message = "Tenant erasure cancelled by the system for {TenantId}: the tenant is protected (system tenant or active platform admin member)")]
    private static partial void LogAborted(ILogger logger, Guid tenantId);

    [LoggerMessage(EventId = 5013, Level = LogLevel.Error, Message = "Tenant erasure precondition failed for {TenantId}: the account is not pending deletion; automatic retries are stopped (operator retry required)")]
    private static partial void LogPrecondition(ILogger logger, Guid tenantId);
}

/// <summary>
/// <c>audit.audit_log_entries WHERE tenant_id = @t</c> (kişisel veri barındıran <c>changes</c> dahil) parça parça siler (sıra 900). Kiracı kimliği parametredir. Tablo salt-eklemeli
/// tetikleyiciyle korunur (C-SEC2 M3): her parça kendi işleminde <c>erasure</c> işaretiyle silinir; tetikleyici işareti yalnız kiracı <c>pending_deletion|deleted</c> iken kabul eder.
/// </summary>
public sealed class AuditTenantDataEraser(PlatformDbContext db) : ITenantDataEraser
{
    public string Name => "audit";

    public int Order => 900;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        var chunk = Math.Max(chunkSize, 1);
        long total = 0;
        while (true)
        {
            var affected = 0;
            await db.ExecuteInTransactionAsync(
                async token =>
                {
                    await AuditMaintenance.SetLocalAsync(db, AuditMaintenance.Erasure, token).ConfigureAwait(false);
                    affected = await db.Database.ExecuteSqlAsync(
                        $"DELETE FROM audit.audit_log_entries WHERE ctid IN (SELECT ctid FROM audit.audit_log_entries WHERE tenant_id = {tenantId} LIMIT {chunk})",
                        token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
            total += affected;
            if (affected < chunk)
            {
                return new EraseReport(new Dictionary<string, long> { ["audit.audit_log_entries"] = total });
            }
        }
    }
}
