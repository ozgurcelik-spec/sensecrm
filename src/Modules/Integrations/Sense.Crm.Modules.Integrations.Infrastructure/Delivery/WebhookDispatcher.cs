using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.Security;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Modules.Integrations.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Infrastructure.Context;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Delivery;

/// <summary>
/// Kuyruk talep sorgusu (küresel tablo, kiracı-kör; ham SQL envanterinde listeli): vadesi gelmiş ve kirası boş/dolmuş satırlar, <b>kiracı başına en çok</b> <c>PerTenantBatch</c> (gürültücü kiracı diğerlerini aç
/// bırakmaz), toplam <c>BatchSize</c>; <c>FOR UPDATE SKIP LOCKED</c> (çift teslimat yok) ve <c>locked_until = now + LeaseSeconds</c> (çöken Worker'ın işi kira bitince yeniden alınır). Parametreler yalnız değer.
/// </summary>
public sealed class DeliveryQueueStore(IntegrationsDbContext db)
{
    public async Task<IReadOnlyList<DeliveryQueueItem>> ClaimAsync(DateTime nowUtc, int batchSize, int perTenantBatch, int leaseSeconds, CancellationToken ct)
    {
        var lockedUntil = nowUtc.AddSeconds(leaseSeconds);
        return await db.DeliveryQueue
            .FromSql(
                $"""
                WITH ranked AS (
                    SELECT delivery_id, due_at, ROW_NUMBER() OVER (PARTITION BY tenant_id ORDER BY due_at) AS rn
                    FROM integrations.delivery_queue
                    WHERE due_at <= {nowUtc} AND (locked_until IS NULL OR locked_until < {nowUtc})
                ), picked AS (
                    SELECT q.delivery_id
                    FROM integrations.delivery_queue q
                    JOIN ranked r ON r.delivery_id = q.delivery_id
                    WHERE r.rn <= {perTenantBatch}
                    ORDER BY r.due_at
                    LIMIT {batchSize}
                    FOR UPDATE OF q SKIP LOCKED
                )
                UPDATE integrations.delivery_queue AS dq
                SET locked_until = {lockedUntil}
                FROM picked
                WHERE dq.delivery_id = picked.delivery_id
                RETURNING dq.delivery_id, dq.tenant_id, dq.due_at, dq.locked_until, dq.attempt
                """)
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Eşzamanlılık ve hız sınırları (bellek içi, tek Worker varsayımı): genel, kiracı, ana bilgisayar başına uçuştaki teslimat + kiracı başına dakikalık jeton kovası.</summary>
public sealed class DispatchLimiter(TimeProvider clock, IOptions<IntegrationsOptions> options)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _perTenant = [];
    private readonly Dictionary<string, int> _perHost = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _minute = [];
    private int _global;

    /// <summary>Kota içindeyse uçuşa girer (true); değilse false (satır ertelenir, başarısız sayılmaz).</summary>
    public bool TryEnter(Guid tenantId, string host)
    {
        var w = options.Value.Webhooks;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_global >= w.MaxConcurrentGlobal
                || _perTenant.GetValueOrDefault(tenantId) >= w.MaxConcurrentPerTenant
                || _perHost.GetValueOrDefault(host) >= w.MaxConcurrentPerHost)
            {
                return false;
            }

            if (!_minute.TryGetValue(tenantId, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _minute[tenantId] = queue;
            }

            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= w.PerTenantPerMinute)
            {
                return false;
            }

            queue.Enqueue(now);
            _global++;
            _perTenant[tenantId] = _perTenant.GetValueOrDefault(tenantId) + 1;
            _perHost[host] = _perHost.GetValueOrDefault(host) + 1;
            return true;
        }
    }

    public void Exit(Guid tenantId, string host)
    {
        lock (_gate)
        {
            _global = Math.Max(_global - 1, 0);
            _perTenant[tenantId] = Math.Max(_perTenant.GetValueOrDefault(tenantId) - 1, 0);
            _perHost[host] = Math.Max(_perHost.GetValueOrDefault(host) - 1, 0);
        }
    }
}

/// <summary>
/// Teslimat hattı çekirdeği (Worker'daki <c>WebhookDispatcherService</c> periyodik çağırır; testler doğrudan çağırır). <b>Yalnız Worker dış çağrı yapar.</b> Her teslimat kiracı kapsamında (<c>BeginScope</c>, normal filtre) yüklenir.
/// Sıra: talep → hak denetimi (askıdaki kiracı teslim etmez, satır <c>+5 dk</c> bekler) → yaş sınırı → kota → URL yeniden doğrulama → <b>tek çözümleme</b> + sınıflandırma (biri bile engelliyse hepsi reddedilir) →
/// imza (her denemede yeni <c>t</c>) → taşıyıcı (doğrulanan IP'ye sabit, yönlendirme yok) → sonuç sınıflaması, deneme kaydı, kuyruk satırı, sağlık sayaçları, otomatik pasifleştirme.
/// </summary>
public sealed partial class WebhookDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<IntegrationsOptions> options,
    IWebhookTransport transport,
    IDnsResolver dns,
    IWebhookSecretProtector protector,
    IJitter jitter,
    DispatchLimiter limiter,
    TimeProvider clock,
    ILogger<WebhookDispatcher> logger)
{
    private const string UserAgent = "SenseCRM-Webhooks/1.0";

    /// <summary>Bir tur: vadesi gelmiş satırları talep eder ve işler; işlenen satır sayısını döner.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var w = options.Value.Webhooks;
        var now = clock.GetUtcNow().UtcDateTime;
        IReadOnlyList<DeliveryQueueItem> claimed;
        using (var scope = scopeFactory.CreateScope())
        {
            claimed = await scope.ServiceProvider.GetRequiredService<DeliveryQueueStore>()
                .ClaimAsync(now, w.BatchSize, w.PerTenantBatch, w.LeaseSeconds, ct).ConfigureAwait(false);
        }

        if (claimed.Count == 0)
        {
            return 0;
        }

        var access = new ConcurrentDictionary<Guid, Task<bool>>();
        await Task.WhenAll(claimed.Select(item => ProcessAsync(item, access, ct))).ConfigureAwait(false);
        return claimed.Count;
    }

    private async Task ProcessAsync(DeliveryQueueItem item, ConcurrentDictionary<Guid, Task<bool>> access, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        using var tenantScope = sp.GetRequiredService<ITenantContextSetter>().BeginScope(item.TenantId);
        using var userScope = CurrentUserAccessor.UseSystem();
        var db = sp.GetRequiredService<IntegrationsDbContext>();
        var w = options.Value.Webhooks;
        try
        {
            var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == item.DeliveryId, ct).ConfigureAwait(false);
            var subscription = delivery is null
                ? null
                : await db.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == delivery.SubscriptionId, ct).ConfigureAwait(false);
            if (delivery is null || subscription is null)
            {
                await DeleteQueueRowAsync(db, item.DeliveryId, ct).ConfigureAwait(false);
                return;
            }

            var now = clock.GetUtcNow().UtcDateTime;

            // Hak denetimi: askıdaki/plan dışı kiracı için deneme YOK; satır beklemede kalır ve askı kalkınca sürer.
            var entitled = await access.GetOrAdd(item.TenantId, _ => IsEntitledAsync(sp.GetRequiredService<ITenantEntitlements>(), item.TenantId, ct)).ConfigureAwait(false);
            if (!entitled)
            {
                await RescheduleAsync(db, item.DeliveryId, now.AddMinutes(w.SuspendedRecheckMinutes), ct).ConfigureAwait(false);
                return;
            }

            if (now - delivery.CreatedAt > TimeSpan.FromHours(w.MaxEventAgeHours))
            {
                delivery.FailWithoutAttempt(DeliveryFailureReasons.Expired, now);
                await CompleteAsync(db, delivery, null, subscription, countsForHealth: delivery.Kind != DeliveryKinds.Ping, now, ct).ConfigureAwait(false);
                return;
            }

            if (!limiter.TryEnter(item.TenantId, delivery.Host))
            {
                var defer = Random.Shared.Next(w.DeferMinSeconds, w.DeferMaxSeconds + 1);
                await RescheduleAsync(db, item.DeliveryId, now.AddSeconds(defer), ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await AttemptAsync(db, delivery, subscription, ct).ConfigureAwait(false);
            }
            finally
            {
                limiter.Exit(item.TenantId, delivery.Host);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnexpected(logger, ex, item.DeliveryId);
            try
            {
                await RescheduleAsync(db, item.DeliveryId, clock.GetUtcNow().UtcDateTime.AddMinutes(1), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                LogUnexpected(logger, inner, item.DeliveryId);
            }
        }
    }

    private async Task<bool> IsEntitledAsync(ITenantEntitlements entitlements, Guid tenantId, CancellationToken ct)
    {
        var snapshot = await entitlements.GetAsync(tenantId, ct).ConfigureAwait(false);
        var (_, accessLevel) = snapshot.Evaluate(clock.GetUtcNow());
        return accessLevel == AccessLevel.Full && snapshot.IsModuleEnabled(GatedModules.Integrations);
    }

    private async Task AttemptAsync(IntegrationsDbContext db, WebhookDelivery delivery, WebhookSubscription subscription, CancellationToken ct)
    {
        var w = options.Value.Webhooks;
        var started = clock.GetUtcNow().UtcDateTime;
        delivery.BeginAttempt(started);
        var attemptNo = delivery.Attempts;
        var policy = UrlPolicy.From(options.Value);
        DeliveryOutcome outcome;
        TransportResult? result = null;

        var url = SsrfGuard.Validate(subscription.Url, policy);
        if (!url.Ok)
        {
            outcome = DeliveryOutcomeClassifier.Blocked("url_policy:" + url.Reason);
        }
        else
        {
            var addresses = await dns.ResolveAsync(url.Host!, ct).ConfigureAwait(false);
            if (addresses.Count == 0)
            {
                outcome = DeliveryOutcomeClassifier.ClassifyDnsFailure(attemptNo, w, jitter);
            }
            else if (addresses.Any(a => IpClassifier.IsBlocked(a, policy.AllowedPrivateCidrs, policy.AllowLoopback)))
            {
                // Karışık kayıt saldırısı: biri bile engelliyse hepsi reddedilir.
                outcome = DeliveryOutcomeClassifier.Blocked("dns_result_blocked");
            }
            else
            {
                var request = BuildRequest(delivery, subscription, url.Uri!, addresses[0], started);
                result = await transport.SendAsync(request, ct).ConfigureAwait(false);
                outcome = DeliveryOutcomeClassifier.Classify(result, attemptNo, w, jitter);
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var durationMs = (int)Math.Min((result?.Duration ?? (now - started)).TotalMilliseconds, int.MaxValue);
        var snippet = result is null ? null : ResponseSnippet.Build(result.Body, w.ResponseSnippetBytes);
        var attempt = WebhookDeliveryAttempt.Create(delivery.TenantId, delivery.Id, attemptNo, started, durationMs, outcome.HttpStatus, outcome.Reason, outcome.Detail, snippet);
        var health = delivery.Kind != DeliveryKinds.Ping;

        switch (outcome.Kind)
        {
            case OutcomeKind.Success:
                delivery.Succeed(outcome.HttpStatus, durationMs, now);
                await CompleteAsync(db, delivery, attempt, subscription, countsForHealth: false, now, ct, success: health).ConfigureAwait(false);
                break;
            case OutcomeKind.Retry:
                var next = now + outcome.RetryDelay!.Value;
                delivery.ScheduleRetry(outcome.HttpStatus, durationMs, outcome.Reason!, next);
                await SaveRetryAsync(db, delivery, attempt, next, ct).ConfigureAwait(false);
                break;
            default:
                delivery.Fail(outcome.HttpStatus, durationMs, outcome.Reason!, now);
                await CompleteAsync(db, delivery, attempt, subscription, countsForHealth: health, now, ct).ConfigureAwait(false);
                break;
        }
    }

    private TransportRequest BuildRequest(WebhookDelivery delivery, WebhookSubscription subscription, Uri url, IPAddress address, DateTime nowUtc)
    {
        var w = options.Value.Webhooks;
        var body = Encoding.UTF8.GetBytes(delivery.Payload);

        // İmza: her denemede yeni t; yeni sır önce, grace içindeki önceki sır sonra (çift v1).
        var secrets = new List<string>
        {
            protector.Open(subscription.SecretEnc, subscription.SecretKeyId, subscription.TenantId, subscription.Id, subscription.SecretVersion),
        };
        if (subscription.HasActivePreviousSecret(nowUtc) && subscription.PreviousSecretEnc is not null && subscription.PreviousSecretKeyId is not null)
        {
            secrets.Add(protector.Open(subscription.PreviousSecretEnc, subscription.PreviousSecretKeyId, subscription.TenantId, subscription.Id, subscription.PreviousSecretVersion));
        }

        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["User-Agent"] = UserAgent,
            ["X-Crm-Event-Id"] = delivery.EventId.ToString("D"),
            ["X-Crm-Event-Type"] = delivery.EventType,
            ["X-Crm-Delivery-Id"] = delivery.Id.ToString("D"),
            ["X-Crm-Delivery-Attempt"] = delivery.Attempts.ToString(CultureInfo.InvariantCulture),
            [WebhookSignature.HeaderName] = WebhookSignature.BuildHeader(timestamp, body, secrets),
        };
        return new TransportRequest(url, address, body, headers, TimeSpan.FromSeconds(w.TimeoutSeconds), w.MaxResponseBytes);
    }

    private async Task SaveRetryAsync(IntegrationsDbContext db, WebhookDelivery delivery, WebhookDeliveryAttempt attempt, DateTime nextAttemptAt, CancellationToken ct)
    {
        await db.ExecuteInTransactionAsync(async token =>
        {
            db.WebhookDeliveryAttempts.Add(attempt);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            var id = delivery.Id;
            var attemptsMade = delivery.Attempts;
            await db.DeliveryQueue.Where(q => q.DeliveryId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.DueAt, nextAttemptAt).SetProperty(q => q.LockedUntil, (DateTime?)null).SetProperty(q => q.Attempt, attemptsMade), token)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Teslimat bitti (başarı/terminal/tükendi): satırı kaydet, kuyruk satırını sil, sağlık sayaçlarını güncelle (ping sayacı etkilemez).</summary>
    private async Task CompleteAsync(
        IntegrationsDbContext db,
        WebhookDelivery delivery,
        WebhookDeliveryAttempt? attempt,
        WebhookSubscription subscription,
        bool countsForHealth,
        DateTime now,
        CancellationToken ct,
        bool success = false)
    {
        await db.ExecuteInTransactionAsync(async token =>
        {
            if (attempt is not null)
            {
                db.WebhookDeliveryAttempts.Add(attempt);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await DeleteQueueRowAsync(db, delivery.Id, token).ConfigureAwait(false);
            var subscriptionId = subscription.Id;
            if (success)
            {
                await db.WebhookSubscriptions.Where(s => s.Id == subscriptionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsecutiveFailures, 0).SetProperty(x => x.LastSuccessAt, now), token).ConfigureAwait(false);
            }
            else if (countsForHealth)
            {
                await db.WebhookSubscriptions.Where(s => s.Id == subscriptionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsecutiveFailures, x => x.ConsecutiveFailures + 1).SetProperty(x => x.LastFailureAt, now), token).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        if (countsForHealth && !success)
        {
            await AutoDisableIfNeededAsync(db, subscription.Id, now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Art arda hata eşiği aşıldıysa abonelik pasifleşir (<c>failing</c>): izlenen (denetlenen) tek dispatcher yazması.</summary>
    private async Task AutoDisableIfNeededAsync(IntegrationsDbContext db, Guid subscriptionId, DateTime now, CancellationToken ct)
    {
        var threshold = options.Value.Webhooks.AutoDisableAfterFailures;
        db.ChangeTracker.Clear();
        var subscription = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct).ConfigureAwait(false);
        if (subscription is { Enabled: true } && subscription.ConsecutiveFailures >= threshold)
        {
            subscription.AutoDisable(now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            LogAutoDisabled(logger, subscriptionId, subscription.ConsecutiveFailures);
        }
    }

    private static Task<int> DeleteQueueRowAsync(IntegrationsDbContext db, Guid deliveryId, CancellationToken ct) =>
        db.DeliveryQueue.Where(q => q.DeliveryId == deliveryId).ExecuteDeleteAsync(ct);

    private static Task<int> RescheduleAsync(IntegrationsDbContext db, Guid deliveryId, DateTime dueAt, CancellationToken ct) =>
        db.DeliveryQueue.Where(q => q.DeliveryId == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.DueAt, dueAt).SetProperty(q => q.LockedUntil, (DateTime?)null), ct);

    [LoggerMessage(EventId = 8300, Level = LogLevel.Error, Message = "Webhook delivery {DeliveryId} failed unexpectedly; rescheduled")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, Guid deliveryId);

    [LoggerMessage(EventId = 8301, Level = LogLevel.Warning, Message = "Webhook subscription {SubscriptionId} disabled after {Failures} consecutive failures")]
    private static partial void LogAutoDisabled(ILogger logger, Guid subscriptionId, int failures);
}
