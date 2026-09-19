using System.Globalization;
using System.Text.Json;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Events;
using Sense.Crm.Shared.Infrastructure.Messaging.Behaviours;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;

namespace Sense.Crm.Shared.Infrastructure.Events;

/// <summary>
/// <see cref="IIntegrationEventOutbox"/>: olayın tipinin assembly'sinden modülü (<see cref="IModuleUnitOfWorkResolver"/>)
/// bulur ve o modülün DbContext'ine outbox satırı ekler; UnitOfWorkBehaviour'ın SaveChanges'i satırı yazar.
/// </summary>
public sealed class IntegrationEventOutbox(IModuleUnitOfWorkResolver resolver, ICurrentUser user, TimeProvider clock) : IIntegrationEventOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var type = integrationEvent.GetType();
        if (resolver.Resolve(type) is not ModuleDbContext context)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, OutboxEnqueueMessages.ModuleNotRegistered, type.FullName));
        }

        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            TenantId = integrationEvent.TenantId,
            Type = EventTypeRegistry.NameOf(type),
            Payload = JsonSerializer.Serialize(integrationEvent, type, JsonOptions),
            OccurredAt = integrationEvent.OccurredAt.UtcDateTime,
            CorrelationId = user.CorrelationId,
            ActorUserId = integrationEvent.ActorUserId ?? user.UserId,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            CreatedUserId = user.UserId,
        });
    }
}

internal static class OutboxEnqueueMessages
{
    public const string ModuleNotRegistered =
        "No module unit of work owns integration event {0}; pass its Contracts assembly to AddModuleHandlers.";
}
