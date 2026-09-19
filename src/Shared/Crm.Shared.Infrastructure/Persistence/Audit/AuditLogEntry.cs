using Crm.Shared.Kernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Shared.Infrastructure.Persistence.Audit;

/// <summary>
/// Denetim kaydı (K14): kim, neyi, ne zaman, önce/sonra. Tüm modüller için tek tablo: <c>audit.audit_log_entries</c>.
/// Her modülün DbContext'i bu tipi aynı tabloya eşler (migration'dan hariç, bkz. <see cref="ModuleDbContext"/>) ve
/// <see cref="AuditLogInterceptor"/> kaydı iş verisiyle aynı SaveChanges/transaction'da yazar. Tablonun şema sahibi
/// <see cref="AuditDbContext"/>'tir. Append-only: güncellenmez, silinmez.
/// </summary>
public sealed class AuditLogEntry : ITenantEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Değişen agregat türü (CLR tip adı, ör. "Role", "Membership").</summary>
    public required string EntityType { get; set; }

    public required string EntityId { get; set; }

    /// <summary><see cref="AuditActions"/> sabitlerinden biri.</summary>
    public required string Action { get; set; }

    /// <summary>İşlemi yapan kullanıcı; sistem/anonim (kayıt) kaynaklı kayıtlarda null.</summary>
    public Guid? UserId { get; set; }

    /// <summary>İşlem anındaki görünen ad (kullanıcı sonradan değişse/silinse de okunur kalsın diye ayrıca saklanır).</summary>
    public string? UserDisplayName { get; set; }

    /// <summary>jsonb: <c>{ "alan": { "old": ..., "new": ... } }</c>. Hassas alanların değeri maskelenir.</summary>
    public required string Changes { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? CorrelationId { get; set; }
}

/// <summary>Sözleşmede sabitlenen eylem adları - web istemcisi bu tam string'leri bekler.</summary>
public static class AuditActions
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
}

public static class AuditLogTables
{
    public const string Schema = "audit";
    public const string TableName = "audit_log_entries";
    public const int EntityTypeMaxLength = 100;
    public const int EntityIdMaxLength = 64;
    public const int ActionMaxLength = 20;
    public const int UserDisplayNameMaxLength = 256;
    public const int CorrelationIdMaxLength = 128;
}

public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> b)
    {
        b.ToTable(AuditLogTables.TableName, AuditLogTables.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(AuditLogTables.EntityTypeMaxLength).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(AuditLogTables.EntityIdMaxLength).IsRequired();
        b.Property(x => x.Action).HasMaxLength(AuditLogTables.ActionMaxLength).IsRequired();
        b.Property(x => x.UserDisplayName).HasMaxLength(AuditLogTables.UserDisplayNameMaxLength);
        b.Property(x => x.Changes).HasColumnType(PersistenceDefaults.JsonbColumnType).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(AuditLogTables.CorrelationIdMaxLength);

        // K3: kiracı indeksleri TenantId ile başlar; "en yeni önce" listeleme tek indeksle karşılanır.
        b.HasIndex(x => new { x.TenantId, x.OccurredAt }).IsDescending(false, true);
        b.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
    }
}

/// <summary>
/// <c>audit</c> şemasının sahibi: yalnız tabloyu oluşturan migration'ı taşır (Migrator uygular). Kiracı filtresi
/// yoktur - uygulama kodu bu context'i kullanmaz; okuma/yazma modül context'leri üzerinden, kiracı filtresiyle yapılır.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string SchemaName = AuditLogTables.Schema;

    public DbSet<AuditLogEntry> Entries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration());
        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<string>().HaveMaxLength(PersistenceDefaults.DefaultStringLength);
        base.ConfigureConventions(configurationBuilder);
    }
}
