using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Domain.Audit;
using Sense.Crm.Modules.Platform.Domain.Deletion;
using Sense.Crm.Modules.Platform.Domain.Plans;
using Sense.Crm.Modules.Platform.Domain.Usage;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Platform.Infrastructure.Persistence;

/// <summary>
/// Platform modülü DbContext'i (şema <c>platform</c>). Tablolar <b>küresel</b>dir (<c>ITenantEntity</c> değil): kiracı filtresi ve
/// <c>IgnoreQueryFilters</c> gerekmez; çapraz kiracı okuma yalnız kendi tablolarından (<c>tenant_accounts</c>) ve
/// <c>ITenantContextSetter.BeginScope</c> ile yapılır.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext), IPlatformUnitOfWork
{
    public const string SchemaName = "platform";

    public override string Schema => SchemaName;

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<TenantAccount> TenantAccounts => Set<TenantAccount>();

    public DbSet<DeletionRequest> DeletionRequests => Set<DeletionRequest>();

    public DbSet<UsageSnapshot> UsageSnapshots => Set<UsageSnapshot>();

    public DbSet<PlatformAuditEntry> AuditEntries => Set<PlatformAuditEntry>();
}

/// <summary>Tablo/dizin adları.</summary>
public static class PlatformTables
{
    public const string Plans = "plans";
    public const string TenantAccounts = "tenant_accounts";
    public const string DeletionRequests = "deletion_requests";
    public const string UsageSnapshots = "usage_snapshots";
    public const string AuditEntries = "platform_audit_entries";

    public const string ActiveDeletionIndex = "ux_deletion_requests_active";
}

/// <summary>jsonb sütunları için değer dönüştürücüler (camelCase, null yazılır).</summary>
internal static class PlatformJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ValueConverter<T, string> Converter<T>()
        where T : class =>
        new(v => JsonSerializer.Serialize(v, Options), v => JsonSerializer.Deserialize<T>(v, Options)!);

    /// <summary>Plan limitleri: eksik ya da null maxRecords boş sözlük sayılır (kayıt limiti yok); elle eklenmiş ya da eski satırlar arama sırasında hata vermez.</summary>
    public static ValueConverter<PlanLimits, string> PlanLimitsConverter() =>
        new(v => JsonSerializer.Serialize(v, Options), v => Normalize(JsonSerializer.Deserialize<PlanLimits>(v, Options)));

    private static PlanLimits Normalize(PlanLimits? limits) => limits is null ? PlanLimits.Unlimited : limits.MaxRecords is null ? limits with { MaxRecords = new Dictionary<string, int?>() } : limits;

    /// <summary>Sözlük içeriğine göre karşılaştırma (yalnız değişiklik algılama için).</summary>
    public static ValueComparer<T> Comparer<T>()
        where T : class =>
        new(
            (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
            v => JsonSerializer.Serialize(v, Options).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!);
}

public sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable(PlatformTables.Plans);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("code").HasMaxLength(PlatformLimits.PlanCodeMaxLength).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(PlatformLimits.PlanNameMaxLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(PlatformLimits.DescriptionMaxLength);
        b.Property(x => x.Limits).HasColumnType(PersistenceDefaults.JsonbColumnType).HasConversion(PlatformJson.PlanLimitsConverter(), PlatformJson.Comparer<PlanLimits>()).IsRequired();
        b.Property(x => x.Modules).HasColumnType(PersistenceDefaults.JsonbColumnType).HasConversion(PlatformJson.Converter<IReadOnlyDictionary<string, bool>>(), PlatformJson.Comparer<IReadOnlyDictionary<string, bool>>()).IsRequired();
        b.Ignore(x => x.Code);
    }
}

public sealed class TenantAccountConfiguration : IEntityTypeConfiguration<TenantAccount>
{
    public void Configure(EntityTypeBuilder<TenantAccount> b)
    {
        b.ToTable(PlatformTables.TenantAccounts);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("tenant_id").ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(PlatformLimits.TenantNameMaxLength).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(PlatformLimits.SlugMaxLength).IsRequired();
        b.Property(x => x.PlanCode).HasMaxLength(PlatformLimits.PlanCodeMaxLength).IsRequired();
        b.Property(x => x.Status).HasMaxLength(PlatformLimits.StatusMaxLength).IsRequired();
        b.Property(x => x.Source).HasMaxLength(PlatformLimits.SourceMaxLength).IsRequired();
        b.Property(x => x.Overrides).HasColumnType(PersistenceDefaults.JsonbColumnType);
        b.Property(x => x.SuspensionMode).HasMaxLength(PlatformLimits.SuspensionModeMaxLength);
        b.Property(x => x.SuspendedReason).HasMaxLength(PlatformLimits.ReasonMaxLength);
        b.Property(x => x.OnboardingDone).HasColumnType("text[]").IsRequired();
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanCode).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.Slug).IsUnique();
        b.HasIndex(x => new { x.Status, x.PlanCode });
        b.HasIndex(x => x.PlanCode);
        b.Ignore(x => x.TenantId);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class DeletionRequestConfiguration : IEntityTypeConfiguration<DeletionRequest>
{
    public void Configure(EntityTypeBuilder<DeletionRequest> b)
    {
        b.ToTable(PlatformTables.DeletionRequests);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Reason).HasMaxLength(PlatformLimits.ReasonMaxLength).IsRequired();
        b.Property(x => x.Status).HasMaxLength(PlatformLimits.StatusMaxLength).IsRequired();
        b.Property(x => x.PreviousStatus).HasMaxLength(PlatformLimits.StatusMaxLength).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(PlatformLimits.LastErrorMaxLength);
        b.Property(x => x.ErasedSteps).HasColumnType("text[]").IsRequired();
        b.Property(x => x.Report).HasColumnType(PersistenceDefaults.JsonbColumnType);

        b.HasIndex(x => new { x.TenantId, x.RequestedAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.Status, x.ScheduledFor });

        // Kiracı başına tek etkin talep.
        b.HasIndex(x => x.TenantId).IsUnique().HasFilter("status IN ('scheduled','running','failed')").HasDatabaseName(PlatformTables.ActiveDeletionIndex);
    }
}

public sealed class UsageSnapshotConfiguration : IEntityTypeConfiguration<UsageSnapshot>
{
    public void Configure(EntityTypeBuilder<UsageSnapshot> b)
    {
        b.ToTable(PlatformTables.UsageSnapshots);
        b.HasKey(x => new { x.TenantId, x.Day });
        b.Property(x => x.Metrics).HasColumnType(PersistenceDefaults.JsonbColumnType).HasConversion(PlatformJson.Converter<IReadOnlyDictionary<string, long>>(), PlatformJson.Comparer<IReadOnlyDictionary<string, long>>()).IsRequired();
        b.HasIndex(x => x.Day);
    }
}

public sealed class PlatformAuditEntryConfiguration : IEntityTypeConfiguration<PlatformAuditEntry>
{
    public void Configure(EntityTypeBuilder<PlatformAuditEntry> b)
    {
        b.ToTable(PlatformTables.AuditEntries);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ActorEmail).HasMaxLength(PlatformLimits.EmailMaxLength);
        b.Property(x => x.Action).HasMaxLength(PlatformLimits.ActionMaxLength).IsRequired();
        b.Property(x => x.TargetTenantName).HasMaxLength(PlatformLimits.TenantNameMaxLength);
        b.Property(x => x.Details).HasColumnType(PersistenceDefaults.JsonbColumnType).IsRequired();
        b.Property(x => x.Ip).HasMaxLength(PlatformLimits.IpMaxLength);
        b.Property(x => x.CorrelationId).HasMaxLength(PlatformLimits.CorrelationIdMaxLength);

        b.HasIndex(x => x.OccurredAt).IsDescending();
        b.HasIndex(x => new { x.TargetTenantId, x.OccurredAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.Action, x.OccurredAt }).IsDescending(false, true);
    }
}
