using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Files.Infrastructure.Persistence;

/// <summary>Files modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması <c>files</c>).</summary>
public sealed class FilesDbContext(DbContextOptions<FilesDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext), IFilesUnitOfWork
{
    public const string SchemaName = "files";

    public override string Schema => SchemaName;

    public DbSet<FileAttachment> Attachments => Set<FileAttachment>();

    public DbSet<FileAccessLogEntry> AccessLog => Set<FileAccessLogEntry>();
}

internal static class FilesTables
{
    public const string Attachments = "attachments";
    public const string AccessLog = "file_access_log";
}

/// <summary>Enum tel değerleri küçük harf saklanır (kısmi indeks süzgeçleri <c>state &lt;&gt; 'deleted'</c> ham metindir).</summary>
internal static class FileConverters
{
    public static readonly ValueConverter<FileState, string> State = new(v => FileWire.Of(v), v => FileWire.ParseState(v));

    public static readonly ValueConverter<ScanStatus, string> Scan = new(v => FileWire.Of(v), v => FileWire.ParseScan(v));

    public static readonly ValueConverter<FileAccessAction, string> Action = new(v => FileWire.Of(v), v => FileWire.ParseAction(v));
}

// Tüm sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3). storage_key benzersiz indeksinin öneki kiracıdır.

public sealed class FileAttachmentConfiguration : IEntityTypeConfiguration<FileAttachment>
{
    public void Configure(EntityTypeBuilder<FileAttachment> b)
    {
        b.ToTable(FilesTables.Attachments);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.RecordType).HasMaxLength(FilesLimits.RecordTypeMaxLength).IsRequired();
        b.Property(x => x.Name).HasMaxLength(FilesLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Extension).HasMaxLength(FilesLimits.ExtensionMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(FilesLimits.ContentTypeMaxLength).IsRequired();
        b.Property(x => x.Sha256).HasColumnType("char(64)").HasMaxLength(FilesLimits.Sha256Length).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(FilesLimits.StorageKeyMaxLength).IsRequired();
        b.Property(x => x.State).HasConversion(FileConverters.State).HasMaxLength(FilesLimits.StateMaxLength).IsRequired();
        b.Property(x => x.ScanStatus).HasConversion(FileConverters.Scan).HasMaxLength(FilesLimits.ScanStatusMaxLength).IsRequired();

        b.HasIndex(x => new { x.TenantId, x.RecordType, x.RecordId, x.UploadedAt })
            .IsDescending(false, false, false, true)
            .HasFilter("state <> 'deleted'")
            .HasDatabaseName("ix_attachments_record_list");
        b.HasIndex(x => x.StorageKey).IsUnique().HasDatabaseName("ux_attachments_storage_key");
        b.HasIndex(x => x.TenantId)
            .IncludeProperties(x => x.SizeBytes)
            .HasFilter("state IN ('ready','quarantined','missing')")
            .HasDatabaseName("ix_attachments_tenant_usage");
        b.HasIndex(x => new { x.TenantId, x.DeletedAt })
            .HasFilter("state = 'deleted'")
            .HasDatabaseName("ix_attachments_tenant_deleted");
        b.HasIndex(x => new { x.TenantId, x.RecordMissingSince })
            .HasFilter("record_missing_since IS NOT NULL")
            .HasDatabaseName("ix_attachments_tenant_record_missing");
        b.Ignore(x => x.CountsTowardQuota);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class FileAccessLogEntryConfiguration : IEntityTypeConfiguration<FileAccessLogEntry>
{
    public void Configure(EntityTypeBuilder<FileAccessLogEntry> b)
    {
        b.ToTable(FilesTables.AccessLog);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Action).HasConversion(FileConverters.Action).HasMaxLength(FilesLimits.ActionMaxLength).IsRequired();

        b.HasIndex(x => new { x.TenantId, x.OccurredAt }).IsDescending(false, true).HasDatabaseName("ix_file_access_log_tenant_time");
        b.HasIndex(x => new { x.TenantId, x.FileId, x.OccurredAt }).IsDescending(false, false, true).HasDatabaseName("ix_file_access_log_tenant_file_time");
    }
}
