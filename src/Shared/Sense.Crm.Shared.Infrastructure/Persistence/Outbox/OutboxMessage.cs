using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Shared.Infrastructure.Persistence.Outbox;

/// <summary>Domain/integration event'lerin aynı transaction'da yazıldığı kuyruk (transactional outbox).</summary>
public sealed class OutboxMessage : AuditableRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>EventTypeRegistry kısa adı (assembly-qualified değil).</summary>
    public required string Type { get; set; }

    public required string Payload { get; set; }

    public DateTime OccurredAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public string? Error { get; set; }

    public string? CorrelationId { get; set; }

    public Guid? ActorUserId { get; set; }

    public bool IsDead { get; set; }
}

/// <summary>Tüketici tarafında idempotent işleme için işlenmiş mesaj kaydı.</summary>
public sealed class InboxMessage : AuditableRecord
{
    public Guid MessageId { get; set; }

    public required string Handler { get; set; }

    public DateTime ProcessedAt { get; set; }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable(PersistenceDefaults.OutboxTable);
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasMaxLength(200).IsRequired();
        b.Property(x => x.Payload).HasColumnType(PersistenceDefaults.JsonbColumnType).IsRequired();
        b.Property(x => x.Error).HasMaxLength(4000);
        b.Property(x => x.CorrelationId).HasMaxLength(100);
        b.HasIndex(x => new { x.ProcessedAt, x.NextAttemptAt }).HasFilter(OutboxSql.PendingIndexFilter);
        b.HasIndex(x => x.TenantId);
    }
}

public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> b)
    {
        b.ToTable(PersistenceDefaults.InboxTable);
        b.HasKey(x => new { x.MessageId, x.Handler });
        b.Property(x => x.Handler).HasMaxLength(300);
    }
}

/// <summary>Event tipleri için kısa, kararlı ad kaydı: "Leave.LeaveApproved" ↔ CLR tipi.</summary>
public static class EventTypeRegistry
{
    private static readonly ConcurrentDictionary<string, Type> ByName = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Type, string> ByType = new();

    public static void Register(Type type, string? name = null)
    {
        var n = name ?? DefaultName(type);
        ByName[n] = type;
        ByType[type] = n;
    }

    public static void RegisterAll(IEnumerable<System.Reflection.Assembly> assemblies, Type markerInterface)
    {
        foreach (var asm in assemblies)
        {
            foreach (var t in asm.GetTypes().Where(t => !t.IsAbstract && markerInterface.IsAssignableFrom(t)))
            {
                Register(t);
            }
        }
    }

    public static string NameOf(Type type) => ByType.TryGetValue(type, out var n) ? n : DefaultName(type);

    public static Type? Resolve(string name) => ByName.TryGetValue(name, out var t) ? t : null;

    private static string DefaultName(Type type)
    {
        // Sense.Crm.Modules.Leave.Contracts.Events.LeaveApproved → Leave.LeaveApproved
        var ns = type.Namespace ?? string.Empty;
        var parts = ns.Split('.');
        var moduleIdx = Array.IndexOf(parts, "Modules");
        var module = moduleIdx >= 0 && moduleIdx + 1 < parts.Length ? parts[moduleIdx + 1] : "Shared";
        return $"{module}.{type.Name}";
    }
}
