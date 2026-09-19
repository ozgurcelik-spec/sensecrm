using Crm.Modules.Activities.Domain;
using Crm.Modules.Activities.Domain.Activities;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Modules.Activities.Infrastructure.Persistence;

/// <summary>Activities modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması).</summary>
public sealed class ActivitiesDbContext(DbContextOptions<ActivitiesDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "activities";

    public override string Schema => SchemaName;

    public DbSet<Activity> Activities => Set<Activity>();
}

internal static class ActivitiesTables
{
    public const string Activities = "activities";
}

// Tüm sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3): atanan+durum, son tarih, durum, ilişkili kayıt.

public sealed class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> b)
    {
        b.ToTable(ActivitiesTables.Activities);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(ActivityLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(ActivityLimits.SubjectMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(ActivityLimits.DescriptionMaxLength);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(ActivityLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Priority).HasConversion<string>().HasMaxLength(ActivityLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.RelatedType).HasConversion<string>().HasMaxLength(ActivityLimits.EnumColumnMaxLength);
        b.HasIndex(x => new { x.TenantId, x.AssignedUserId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.DueAt });
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.RelatedType, x.RelatedId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Ignore(x => x.IsNote);
        b.Ignore(x => x.DomainEvents);
    }
}
