using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Shared.Infrastructure.Persistence.Audit;

/// <summary>
/// Denetim kaydı yazıcısı (K14): <see cref="IAuditLogged"/> işaretli agregatlarda
/// Added/Modified/Deleted için aynı SaveChanges'ta (aynı transaction) bir <see cref="AuditLogEntry"/> üretir.
/// Değişiklikler <c>{ "alan": { "old": ..., "new": ... } }</c> biçiminde (camelCase alan adları) yazılır; denetim
/// kolonları ve kimlik alanları dışarıda kalır, <see cref="IAuditLogged.SensitiveFields"/> değerleri maskelenir.
/// <see cref="AuditTenantInterceptor"/>'dan SONRA çalışmalıdır: soft-delete dönüşümü (Deleted → Modified, IsDeleted=true)
/// orada yapıldığından silme, IsDeleted alanının bu kayıtta değişmesinden anlaşılır.
/// Kiracı: agregat <see cref="ITenantEntity"/> ise onun TenantId'si; değilse (ör. organizasyonun kendisi) aktif kiracı;
/// ikisi de yoksa kayıt üretilmez.
/// </summary>
public sealed class AuditLogInterceptor(ICurrentUser currentUser, ITenantContext tenant, TimeProvider clock) : SaveChangesInterceptor
{
    private const string MaskedValue = "***";

    // Enum alanları API ile aynı biçimde (camelCase string) yazılır: "converted", "coldCall".
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly HashSet<string> IgnoredProperties = new(StringComparer.Ordinal)
    {
        nameof(ITenantEntity.TenantId),
        nameof(IAuditable.CreatedAt),
        nameof(IAuditable.CreatedUserId),
        nameof(IAuditable.ModifiedDate),
        nameof(IAuditable.ModifiedUserId),
        nameof(ISoftDelete.IsDeleted),
        nameof(ISoftDelete.DeletedAt),
        nameof(ISoftDelete.DeletedUserId),
    };

    private static readonly ConcurrentDictionary<Type, IReadOnlySet<string>> SensitiveFieldsCache = new();

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
        if (context?.Model.FindEntityType(typeof(AuditLogEntry)) is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        List<AuditLogEntry>? entries = null;

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not IAuditLogged || entry.Entity is AuditLogEntry)
            {
                continue;
            }

            var action = DetermineAction(entry);
            if (action is null)
            {
                continue;
            }

            Guid? tenantId = entry.Entity is ITenantEntity te ? te.TenantId : tenant.IsResolved ? tenant.TenantId : null;
            if (tenantId is not { } tid || tid == Guid.Empty)
            {
                continue;
            }

            var changes = BuildChanges(entry, action);
            if (action == AuditActions.Updated && changes.Count == 0)
            {
                continue;
            }

            entries ??= [];
            entries.Add(new AuditLogEntry
            {
                Id = Guid.CreateVersion7(),
                TenantId = tid,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = KeyOf(entry),
                Action = action,
                UserId = currentUser.UserId,
                UserDisplayName = currentUser.UserId is null ? null : currentUser.DisplayName ?? currentUser.Email,
                Changes = changes.ToJsonString(JsonOptions),
                OccurredAt = now,
                CorrelationId = currentUser.CorrelationId,
            });
        }

        if (entries is { Count: > 0 })
        {
            context.Set<AuditLogEntry>().AddRange(entries);
        }
    }

    private static string? DetermineAction(EntityEntry entry)
    {
        if (entry.Entity is ISoftDelete { IsDeleted: true } && entry.Property(nameof(ISoftDelete.IsDeleted)).IsModified)
        {
            return AuditActions.Deleted;
        }

        return entry.State switch
        {
            EntityState.Added => AuditActions.Created,
            EntityState.Modified => AuditActions.Updated,
            EntityState.Deleted => AuditActions.Deleted,
            _ => null,
        };
    }

    private static JsonObject BuildChanges(EntityEntry entry, string action)
    {
        var sensitive = SensitiveFieldsOf(entry.Metadata.ClrType);
        var changes = new JsonObject();

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (property.Metadata.IsShadowProperty() || property.Metadata.IsPrimaryKey() || IgnoredProperties.Contains(name))
            {
                continue;
            }

            object? oldValue;
            object? newValue;
            switch (action)
            {
                case AuditActions.Created:
                    oldValue = null;
                    newValue = property.CurrentValue;
                    if (newValue is null)
                    {
                        continue;
                    }

                    break;

                case AuditActions.Deleted:
                    oldValue = property.OriginalValue;
                    newValue = null;
                    break;

                default:
                    if (!property.IsModified || ValuesEqual(property.OriginalValue, property.CurrentValue))
                    {
                        continue;
                    }

                    oldValue = property.OriginalValue;
                    newValue = property.CurrentValue;
                    break;
            }

            var masked = sensitive.Contains(name);
            changes[JsonNamingPolicy.CamelCase.ConvertName(name)] = new JsonObject
            {
                ["old"] = masked && oldValue is not null ? MaskedValue : ToNode(oldValue),
                ["new"] = masked && newValue is not null ? MaskedValue : ToNode(newValue),
            };
        }

        return changes;
    }

    private static JsonNode? ToNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), JsonOptions);

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is System.Collections.IEnumerable l and not string && right is System.Collections.IEnumerable r and not string)
        {
            return l.Cast<object?>().SequenceEqual(r.Cast<object?>());
        }

        return Equals(left, right);
    }

    private static string KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return string.Empty;
        }

        return string.Join(',', key.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? string.Empty));
    }

    /// <summary><see cref="IAuditLogged.SensitiveFields"/> static virtual üyesini CLR tipinden okur (tip başına bir kez).</summary>
    private static IReadOnlySet<string> SensitiveFieldsOf(Type entityType) =>
        SensitiveFieldsCache.GetOrAdd(entityType, static t =>
            t.GetProperty(nameof(IAuditLogged.SensitiveFields), BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IReadOnlySet<string>
            ?? new HashSet<string>(StringComparer.Ordinal));
}
