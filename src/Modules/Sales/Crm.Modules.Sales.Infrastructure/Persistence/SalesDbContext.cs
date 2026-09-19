using Crm.Modules.Sales.Domain;
using Crm.Modules.Sales.Domain.Accounts;
using Crm.Modules.Sales.Domain.Contacts;
using Crm.Modules.Sales.Domain.Deals;
using Crm.Modules.Sales.Domain.Leads;
using Crm.Modules.Sales.Domain.Pipelines;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Modules.Sales.Infrastructure.Persistence;

/// <summary>Sales modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması).</summary>
public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "sales";

    public override string Schema => SchemaName;

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<Lead> Leads => Set<Lead>();

    public DbSet<Pipeline> Pipelines => Set<Pipeline>();

    public DbSet<PipelineStage> PipelineStages => Set<PipelineStage>();

    public DbSet<Deal> Deals => Set<Deal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Raporlarda UTC anı kiracı saat diliminde yerel güne indirmek için: PostgreSQL timezone(zone, timestamptz) → timestamp.
        modelBuilder.HasDbFunction(typeof(SalesDbFunctions).GetMethod(nameof(SalesDbFunctions.ToLocalTimestamp), [typeof(string), typeof(DateTime)])!)
            .HasName("timezone")
            .HasStoreType("timestamp without time zone")
            .IsBuiltIn();
    }
}

/// <summary>Yalnız sorgu çevirisi için: veritabanı işlevleri (istemcide çalıştırılamaz).</summary>
public static class SalesDbFunctions
{
    /// <summary>UTC anı (<c>timestamptz</c>) verilen IANA saat dilimindeki duvar saatine çevirir (<c>timezone(zone, ts)</c>).</summary>
    public static DateTime ToLocalTimestamp(string timeZone, DateTime utc) =>
        throw new NotSupportedException("Yalnızca EF Core sorgularında kullanılabilir.");
}

internal static class SalesTables
{
    public const string Accounts = "accounts";
    public const string Contacts = "contacts";
    public const string Leads = "leads";
    public const string Pipelines = "pipelines";
    public const string PipelineStages = "pipeline_stages";
    public const string Deals = "deals";
}

// Tüm sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3): sahip, durum/aşama, firma, oluşturulma.

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable(SalesTables.Accounts);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(SalesLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Industry).HasMaxLength(SalesLimits.IndustryMaxLength);
        b.Property(x => x.Website).HasMaxLength(SalesLimits.WebsiteMaxLength);
        b.Property(x => x.Phone).HasMaxLength(SalesLimits.PhoneMaxLength);
        b.Property(x => x.Email).HasMaxLength(SalesLimits.EmailMaxLength);
        b.Property(x => x.Description).HasMaxLength(SalesLimits.DescriptionMaxLength);
        b.Property(x => x.BillingStreet).HasMaxLength(SalesLimits.StreetMaxLength);
        b.Property(x => x.BillingCity).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.Property(x => x.BillingState).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.Property(x => x.BillingPostalCode).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.Property(x => x.BillingCountry).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.Name });
        b.Ignore(x => x.BillingAddress);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> b)
    {
        b.ToTable(SalesTables.Contacts);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FirstName).HasMaxLength(SalesLimits.PersonNameMaxLength);
        b.Property(x => x.LastName).HasMaxLength(SalesLimits.PersonNameMaxLength).IsRequired();
        b.Property(x => x.Email).HasMaxLength(SalesLimits.EmailMaxLength);
        b.Property(x => x.Phone).HasMaxLength(SalesLimits.PhoneMaxLength);
        b.Property(x => x.Mobile).HasMaxLength(SalesLimits.PhoneMaxLength);
        b.Property(x => x.Title).HasMaxLength(SalesLimits.TitleMaxLength);
        b.Property(x => x.MailingStreet).HasMaxLength(SalesLimits.StreetMaxLength);
        b.Property(x => x.MailingCity).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.Property(x => x.MailingState).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.Property(x => x.MailingPostalCode).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.Property(x => x.MailingCountry).HasMaxLength(SalesLimits.AddressPartMaxLength);
        b.HasIndex(x => new { x.TenantId, x.AccountId });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.LastName });
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.FullName);
        b.Ignore(x => x.MailingAddress);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> b)
    {
        b.ToTable(SalesTables.Leads);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FirstName).HasMaxLength(SalesLimits.PersonNameMaxLength);
        b.Property(x => x.LastName).HasMaxLength(SalesLimits.PersonNameMaxLength).IsRequired();
        b.Property(x => x.Company).HasMaxLength(SalesLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Email).HasMaxLength(SalesLimits.EmailMaxLength);
        b.Property(x => x.Phone).HasMaxLength(SalesLimits.PhoneMaxLength);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(SalesLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(SalesLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Rating).HasConversion<string>().HasMaxLength(SalesLimits.EnumColumnMaxLength);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.Source });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Ignore(x => x.FullName);
        b.Ignore(x => x.IsConverted);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class PipelineConfiguration : IEntityTypeConfiguration<Pipeline>
{
    public void Configure(EntityTypeBuilder<Pipeline> b)
    {
        b.ToTable(SalesTables.Pipelines);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(SalesLimits.NameMaxLength).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.IsDefault });
        b.HasMany(x => x.Stages).WithOne().HasForeignKey(s => s.PipelineId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Stages).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(x => x.FirstOpenStage);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class PipelineStageConfiguration : IEntityTypeConfiguration<PipelineStage>
{
    public void Configure(EntityTypeBuilder<PipelineStage> b)
    {
        b.ToTable(SalesTables.PipelineStages);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(SalesLimits.StageNameMaxLength).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(SalesLimits.EnumColumnMaxLength).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.PipelineId, x.Order });
    }
}

public sealed class DealConfiguration : IEntityTypeConfiguration<Deal>
{
    public void Configure(EntityTypeBuilder<Deal> b)
    {
        b.ToTable(SalesTables.Deals);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(SalesLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(SalesLimits.CurrencyLength).IsRequired();
        b.Property(x => x.LostReason).HasMaxLength(SalesLimits.LostReasonMaxLength);
        b.Property(x => x.ClosingDate).HasColumnType("date");
        b.HasIndex(x => new { x.TenantId, x.PipelineId, x.StageId });
        b.HasIndex(x => new { x.TenantId, x.StageId });
        b.HasIndex(x => new { x.TenantId, x.AccountId });
        b.HasIndex(x => new { x.TenantId, x.ContactId });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Pipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PipelineStage>().WithMany().HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.DomainEvents);
    }
}
