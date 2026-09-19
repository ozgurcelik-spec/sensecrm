using System.Globalization;
using System.Text.Json;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Events;
using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Kernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Shared.Infrastructure.Persistence.Outbox;

/// <summary>
/// Bir modülün outbox tablosunu işler: FOR UPDATE SKIP LOCKED ile batch alır, domain event handler'larını
/// (aynı modül, in-process) çalıştırır, integration event ise IEventBus'a yayınlar. Hata → üstel geri çekilme; MaxAttempts → dead.
/// Worker'da her modül context'i için <c>OutboxPollingService</c> tarafından periyodik çalıştırılır.
/// </summary>
public sealed partial class OutboxProcessor<TContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor<TContext>> logger,
    IOptions<OutboxOptions> options,
    TimeProvider clock)
    where TContext : ModuleDbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> ProcessAsync(CancellationToken cancellationToken = default)
    {
        var cfg = options.Value;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = clock.GetUtcNow().UtcDateTime;

        var processed = 0;

        // FOR UPDATE SKIP LOCKED kilitleri yalnız bir transaction içinde anlamlıdır: birden çok worker aynı satırı almaz.
        await db.ExecuteInTransactionAsync(async ct =>
        {
            processed = await ProcessBatchAsync(scope.ServiceProvider, db, cfg, now, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return processed;
    }

    private async Task<int> ProcessBatchAsync(IServiceProvider services, TContext db, OutboxOptions cfg, DateTime now, CancellationToken cancellationToken)
    {
        var messages = await db.OutboxMessages
            .FromSqlRaw(string.Format(CultureInfo.InvariantCulture, OutboxSql.SelectPending, db.Schema, cfg.BatchSize), now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            return 0;
        }

        var processed = 0;
        foreach (var message in messages)
        {
            try
            {
                await DispatchAsync(services, message, cancellationToken).ConfigureAwait(false);
                message.ProcessedAt = clock.GetUtcNow().UtcDateTime;
                message.Error = null;
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                var text = ex.ToString();
                message.Error = text.Length > OutboxDefaults.MaxErrorLength ? text[..OutboxDefaults.MaxErrorLength] : text;
                if (message.Attempts >= cfg.MaxAttempts)
                {
                    message.IsDead = true;
                    Log.DeadLettered(logger, ex, message.Id, message.Type, message.Attempts);
                }
                else
                {
                    var seconds = Math.Min(Math.Pow(2, message.Attempts) * cfg.BaseBackoffSeconds, cfg.MaxBackoffSeconds);
                    message.NextAttemptAt = clock.GetUtcNow().UtcDateTime.AddSeconds(seconds);
                    Log.Retry(logger, ex, message.Id, message.Type, seconds);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return processed;
    }

    private static async Task DispatchAsync(IServiceProvider services, OutboxMessage message, CancellationToken cancellationToken)
    {
        var type = EventTypeRegistry.Resolve(message.Type)
            ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, OutboxMessages.UnknownEventType, message.Type));

        var payload = JsonSerializer.Deserialize(message.Payload, type, JsonOptions)
            ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, OutboxMessages.PayloadDeserializationFailed, message.Type));

        var tenantSetter = services.GetRequiredService<ITenantContextSetter>();
        using var tenantScope = tenantSetter.BeginScope(message.TenantId);
        using var userScope = CurrentUserAccessor.UseSystem();

        if (payload is IIntegrationEvent integrationEvent)
        {
            var bus = services.GetRequiredService<IEventBus>();
            var publish = typeof(IEventBus).GetMethod(nameof(IEventBus.Publish))!.MakeGenericMethod(type);
            await ((Task)publish.Invoke(bus, [integrationEvent, cancellationToken])!).ConfigureAwait(false);
            return;
        }

        if (payload is IDomainEvent)
        {
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(type);
            foreach (var handler in services.GetServices(handlerType))
            {
                var handle = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.Handle))!;
                await ((Task)handle.Invoke(handler, [payload, cancellationToken])!).ConfigureAwait(false);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1100, Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) dead-lettered after {Attempts} attempts")]
        public static partial void DeadLettered(ILogger logger, Exception exception, Guid messageId, string eventType, int attempts);

        [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({EventType}) failed; retrying in {DelaySeconds}s")]
        public static partial void Retry(ILogger logger, Exception exception, Guid messageId, string eventType, double delaySeconds);
    }
}

internal static class OutboxSql
{
    public const string PendingIndexFilter = "processed_at IS NULL";
    /// <summary>{0}=schema, {1}=batch size, {{0}}=now parametresi.</summary>
    public const string SelectPending = """
        SELECT * FROM "{0}"."outbox_messages"
        WHERE processed_at IS NULL AND is_dead = FALSE AND (next_attempt_at IS NULL OR next_attempt_at <= {{0}})
        ORDER BY occurred_at
        LIMIT {1}
        FOR UPDATE SKIP LOCKED
        """;
}

internal static class OutboxMessages
{
    public const string UnknownEventType = "Event type could not be resolved: {0}";
    public const string PayloadDeserializationFailed = "Event payload could not be deserialized: {0}";
}

/// <summary>Aynı modül içinde domain event tüketicisi (outbox üzerinden, asenkron).</summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken cancellationToken);
}
