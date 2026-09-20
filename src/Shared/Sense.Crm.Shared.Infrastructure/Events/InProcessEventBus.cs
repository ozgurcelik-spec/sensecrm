using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;

namespace Sense.Crm.Shared.Infrastructure.Events;

/// <summary>
/// Faz 0 event bus: integration event'i aynı süreçte kayıtlı tüm handler'lara, kiracı bağlamını kurarak dağıtır.
/// Faz 4'te aynı arayüz RabbitMQ/MassTransit adaptörüyle değiştirilir.
/// <para>
/// M7 (plan/askı zorlaması, olay veri yolu): her handler için işleyicinin assembly'sinden modül türetilir; kiracının erişimi
/// <c>Full</c> değilse <b>veya</b> kapı modülü planda kapalıysa işleyici <b>atlanır</b> (günlük + sayaç, hata değil, olay yeniden denenmez) —
/// askıdaki/kapalı modüldeki yeni otomasyon çalışmaz. <see cref="EntitlementExemptAttribute"/> taşıyan sistem işleyicileri her zaman çalışır.
/// </para>
/// </summary>
public sealed partial class InProcessEventBus(IServiceScopeFactory scopeFactory, ILogger<InProcessEventBus> logger, TimeProvider clock) : IEventBus
{
    public const string MeterName = "Sense.Crm.Entitlements";

    private static readonly Meter EntitlementMeter = new(MeterName);
    private static readonly Counter<long> SkippedHandlers = EntitlementMeter.CreateCounter<long>("crm.event_handlers.skipped", description: "Integration event handlers skipped by plan/lifecycle enforcement.");

    public async Task Publish<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        using var scope = scopeFactory.CreateScope();
        var tenantSetter = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>();
        using var tenantScope = tenantSetter.BeginScope(integrationEvent.TenantId);
        using var userScope = CurrentUserAccessor.UseSystem();

        var handlers = scope.ServiceProvider.GetServices<IIntegrationEventHandler<TEvent>>().ToList();
        if (handlers.Count == 0)
        {
            Log.NoHandlers(logger, typeof(TEvent).Name);
            return;
        }

        var entitlements = scope.ServiceProvider.GetRequiredService<ITenantEntitlements>();
        EntitlementSnapshot? snapshot = null;

        foreach (var handler in handlers)
        {
            var handlerType = handler.GetType();
            if (!handlerType.IsDefined(typeof(EntitlementExemptAttribute), inherit: true))
            {
                snapshot ??= await entitlements.GetAsync(integrationEvent.TenantId, cancellationToken).ConfigureAwait(false);
                var module = ModuleUnitOfWorkResolver.ModuleOf(handlerType.Assembly);
                var (status, access) = snapshot.Evaluate(clock.GetUtcNow());
                if (access != AccessLevel.Full || !snapshot.IsModuleEnabled(module))
                {
                    Log.Skipped(logger, handlerType.Name, typeof(TEvent).Name, access != AccessLevel.Full ? status : "module_disabled");
                    SkippedHandlers.Add(1, new KeyValuePair<string, object?>("handler", handlerType.Name));
                    continue;
                }
            }

            try
            {
                await handler.Handle(integrationEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.HandlerFailed(logger, ex, handlerType.Name, typeof(TEvent).Name);
                throw;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1200, Level = LogLevel.Debug, Message = "No handlers registered for {EventType}")]
        public static partial void NoHandlers(ILogger logger, string eventType);

        [LoggerMessage(EventId = 1201, Level = LogLevel.Error, Message = "{Handler} failed while handling {EventType}")]
        public static partial void HandlerFailed(ILogger logger, Exception exception, string handler, string eventType);

        [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "{Handler} skipped for {EventType}: tenant not entitled ({Reason})")]
        public static partial void Skipped(ILogger logger, string handler, string eventType, string reason);
    }
}
