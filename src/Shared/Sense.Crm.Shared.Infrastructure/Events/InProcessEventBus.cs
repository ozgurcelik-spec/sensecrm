using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Infrastructure.Context;

namespace Sense.Crm.Shared.Infrastructure.Events;

/// <summary>
/// Faz 0 event bus: integration event'i aynı süreçte kayıtlı tüm handler'lara, kiracı bağlamını kurarak dağıtır.
/// Faz 4'te aynı arayüz RabbitMQ/MassTransit adaptörüyle değiştirilir.
/// </summary>
public sealed partial class InProcessEventBus(IServiceScopeFactory scopeFactory, ILogger<InProcessEventBus> logger) : IEventBus
{
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

        foreach (var handler in handlers)
        {
            try
            {
                await handler.Handle(integrationEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.HandlerFailed(logger, ex, handler.GetType().Name, typeof(TEvent).Name);
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
    }
}
