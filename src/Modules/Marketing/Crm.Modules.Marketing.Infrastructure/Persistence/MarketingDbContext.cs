using Crm.Modules.Marketing.Application;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Domain.Campaigns;
using Crm.Modules.Marketing.Domain.Members;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Modules.Marketing.Infrastructure.Persistence;

/// <summary>Marketing modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması).</summary>
public sealed class MarketingDbContext(DbContextOptions<MarketingDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext), IMarketingUnitOfWork
{
    public const string SchemaName = "marketing";

    public override string Schema => SchemaName;

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignMember> CampaignMembers => Set<CampaignMember>();
}

internal static class MarketingTables
{
    public const string Campaigns = "campaigns";
    public const string CampaignMembers = "campaign_members";
}

// Tüm sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3).

public sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> b)
    {
        b.ToTable(MarketingTables.Campaigns);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(MarketingLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(MarketingLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(MarketingLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(MarketingLimits.CurrencyLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(MarketingLimits.DescriptionMaxLength);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.Type });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.StartDate });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Ignore(x => x.AcceptsNewMembers);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class CampaignMemberConfiguration : IEntityTypeConfiguration<CampaignMember>
{
    public void Configure(EntityTypeBuilder<CampaignMember> b)
    {
        b.ToTable(MarketingTables.CampaignMembers);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.MemberType).HasConversion<string>().HasMaxLength(MarketingLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(MarketingLimits.EnumColumnMaxLength).IsRequired();

        // Marketing içi FK; Sales kaydına (memberId) yumuşak bağ: FK yok.
        b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.CampaignId, x.MemberType, x.MemberId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MemberType, x.MemberId });
        b.HasIndex(x => new { x.TenantId, x.CampaignId, x.Status });
        b.Ignore(x => x.IsLocked);
        b.Ignore(x => x.DomainEvents);
    }
}
