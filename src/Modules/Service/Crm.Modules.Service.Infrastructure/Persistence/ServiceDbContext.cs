using Crm.Modules.Service.Domain;
using Crm.Modules.Service.Domain.Cases;
using Crm.Modules.Service.Domain.Sla;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Modules.Service.Infrastructure.Persistence;

/// <summary>Service modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması).</summary>
public sealed class ServiceDbContext(DbContextOptions<ServiceDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "service";

    public override string Schema => SchemaName;

    public DbSet<Case> Cases => Set<Case>();

    public DbSet<CaseComment> CaseComments => Set<CaseComment>();

    public DbSet<CaseEvent> CaseEvents => Set<CaseEvent>();

    public DbSet<CaseCounter> CaseCounters => Set<CaseCounter>();

    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
}

internal static class ServiceTables
{
    public const string Cases = "cases";
    public const string CaseComments = "case_comments";
    public const string CaseEvents = "case_events";
    public const string CaseCounters = "case_counters";
    public const string SlaPolicies = "sla_policies";
}

// Tüm sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3).

public sealed class CaseConfiguration : IEntityTypeConfiguration<Case>
{
    public void Configure(EntityTypeBuilder<Case> b)
    {
        b.ToTable(ServiceTables.Cases);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Number).HasMaxLength(ServiceLimits.NumberMaxLength).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(ServiceLimits.SubjectMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(ServiceLimits.DescriptionMaxLength);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(ServiceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Priority).HasConversion<string>().HasMaxLength(ServiceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Channel).HasConversion<string>().HasMaxLength(ServiceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.ResolutionNote).HasMaxLength(ServiceLimits.ResolutionNoteMaxLength);

        // Eşzamanlılık: PostgreSQL xmin sistem sütunu (çakışma → DbUpdateConcurrencyException → general.concurrency_conflict, 409).
        b.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt }).IsDescending(false, false, true);
        b.HasIndex(x => new { x.TenantId, x.AssignedUserId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.AccountId });
        b.HasIndex(x => new { x.TenantId, x.ContactId });
        b.HasIndex(x => new { x.TenantId, x.DueAt });
        b.Ignore(x => x.IsActive);
        b.Ignore(x => x.ResolutionMinutes);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class CaseCommentConfiguration : IEntityTypeConfiguration<CaseComment>
{
    public void Configure(EntityTypeBuilder<CaseComment> b)
    {
        b.ToTable(ServiceTables.CaseComments);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(ServiceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Body).HasMaxLength(ServiceLimits.CommentBodyMaxLength).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.CaseId, x.CreatedAt });
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class CaseEventConfiguration : IEntityTypeConfiguration<CaseEvent>
{
    public void Configure(EntityTypeBuilder<CaseEvent> b)
    {
        b.ToTable(ServiceTables.CaseEvents);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(ServiceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.FromValue).HasMaxLength(ServiceLimits.EventValueMaxLength);
        b.Property(x => x.ToValue).HasMaxLength(ServiceLimits.EventValueMaxLength);
        b.Property(x => x.Note).HasMaxLength(ServiceLimits.ResolutionNoteMaxLength);
        b.HasIndex(x => new { x.TenantId, x.CaseId, x.CreatedAt });
    }
}

public sealed class CaseCounterConfiguration : IEntityTypeConfiguration<CaseCounter>
{
    public void Configure(EntityTypeBuilder<CaseCounter> b)
    {
        b.ToTable(ServiceTables.CaseCounters);
        b.HasKey(x => new { x.TenantId, x.Year });
        b.Property(x => x.LastValue).IsRequired();
    }
}

public sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> b)
    {
        b.ToTable(ServiceTables.SlaPolicies);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Priority).HasConversion<string>().HasMaxLength(ServiceLimits.EnumColumnMaxLength).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Priority }).IsUnique();
        b.Ignore(x => x.Minutes);
        b.Ignore(x => x.DomainEvents);
    }
}
