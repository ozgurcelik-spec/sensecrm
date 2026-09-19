using System.Text.Json;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Persistence.Outbox;
using Crm.Shared.Kernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Crm.Shared.Infrastructure.Persistence;

/// <summary>
/// SaveChanges öncesi tüm modül context'lerinde:
/// - Added: TenantId ata/doğrula, CreatedAt + CreatedUserId doldur
/// - Modified: ModifiedDate + ModifiedUserId doldur; Created*/TenantId değişimini engelle
/// - Deleted + ISoftDelete: silme yerine işaretle
/// - AggregateRoot domain event'lerini aynı transaction'da outbox'a yaz
/// </summary>
public sealed class AuditTenantInterceptor(ITenantContext tenant, ICurrentUser user, TimeProvider clock) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var userId = user.UserId;
        var outbox = new List<OutboxMessage>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    ApplyTenantOnAdd(entry);
                    if (entry.Entity is IAuditable added)
                    {
                        added.CreatedAt = now;
                        added.CreatedUserId = userId;
                        added.ModifiedDate = null;
                        added.ModifiedUserId = null;
                    }

                    break;

                case EntityState.Modified:
                    if (entry.Entity is IAuditable modified)
                    {
                        modified.ModifiedDate = now;
                        modified.ModifiedUserId = userId;
                        entry.Property(nameof(IAuditable.CreatedAt)).IsModified = false;
                        entry.Property(nameof(IAuditable.CreatedUserId)).IsModified = false;
                    }

                    if (entry.Entity is ITenantEntity)
                    {
                        var tenantProp = entry.Property(nameof(ITenantEntity.TenantId));
                        if (tenantProp.IsModified)
                        {
                            throw new TenantMismatchException(InterceptorMessages.TenantIdImmutable);
                        }
                    }

                    break;

                case EntityState.Deleted when entry.Entity is ISoftDelete soft:
                    entry.State = EntityState.Modified;
                    soft.IsDeleted = true;
                    soft.DeletedAt = now;
                    soft.DeletedUserId = userId;
                    if (entry.Entity is IAuditable softAudit)
                    {
                        softAudit.ModifiedDate = now;
                        softAudit.ModifiedUserId = userId;
                    }

                    break;
            }

            CollectDomainEvents(entry, outbox, now);
        }

        if (outbox.Count > 0)
        {
            context.Set<OutboxMessage>().AddRange(outbox);
        }
    }

    private void ApplyTenantOnAdd(EntityEntry entry)
    {
        if (entry.Entity is not ITenantEntity)
        {
            return;
        }

        var prop = entry.Property(nameof(ITenantEntity.TenantId));
        var current = (Guid)(prop.CurrentValue ?? Guid.Empty);

        if (!tenant.IsResolved)
        {
            if (current == Guid.Empty)
            {
                throw new TenantMismatchException(InterceptorMessages.TenantContextRequired);
            }

            return; // platform/migrator bağlamı: açıkça verilmiş TenantId kabul edilir
        }

        if (current == Guid.Empty)
        {
            prop.CurrentValue = tenant.TenantId;
        }
        else if (current != tenant.TenantId)
        {
            throw new TenantMismatchException(string.Format(System.Globalization.CultureInfo.InvariantCulture, InterceptorMessages.CrossTenantWrite, current, tenant.TenantId));
        }
    }

    private void CollectDomainEvents(EntityEntry entry, List<OutboxMessage> outbox, DateTime now)
    {
        if (entry.Entity is not { } entity)
        {
            return;
        }

        var eventsProp = entity.GetType().GetProperty(InterceptorMessages.DomainEventsProperty);
        if (eventsProp?.GetValue(entity) is not IReadOnlyCollection<IDomainEvent> events || events.Count == 0)
        {
            return;
        }

        var tenantId = entity is ITenantEntity te ? te.TenantId : (tenant.IsResolved ? tenant.TenantId : Guid.Empty);
        foreach (var domainEvent in events)
        {
            outbox.Add(new OutboxMessage
            {
                Id = domainEvent.EventId,
                TenantId = tenantId,
                Type = EventTypeRegistry.NameOf(domainEvent.GetType()),
                Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                OccurredAt = domainEvent.OccurredAt.UtcDateTime,
                CorrelationId = user.CorrelationId,
                ActorUserId = user.UserId,
                CreatedAt = now,
                CreatedUserId = user.UserId,
            });
        }

        entity.GetType().GetMethod(InterceptorMessages.ClearDomainEventsMethod)?.Invoke(entity, null);
    }
}

public sealed class TenantMismatchException(string message) : InvalidOperationException(message);

internal static class InterceptorMessages
{
    public const string TenantIdImmutable = "TenantId cannot be changed.";
    public const string TenantContextRequired = "Tenant data cannot be written without a resolved tenant context.";
    public const string CrossTenantWrite = "Entity targets tenant {0} while the active tenant is {1}.";
    public const string DomainEventsProperty = "DomainEvents";
    public const string ClearDomainEventsMethod = "ClearDomainEvents";
}
