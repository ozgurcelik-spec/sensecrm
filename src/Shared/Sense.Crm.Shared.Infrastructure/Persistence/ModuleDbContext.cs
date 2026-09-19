using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Shared.Infrastructure.Persistence;

/// <summary>
/// Modül başına DbContext taban sınıfı (ADR 0003): kendi PostgreSQL şeması, snake_case adlandırma,
/// "Tenant" ve "SoftDelete" adlı global query filter'lar, outbox/inbox tabloları ve IUnitOfWork.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options, ITenantContext tenantContext) : DbContext(options), IModuleUnitOfWork
{
    public const string TenantFilter = PersistenceDefaults.TenantFilterName;
    public const string SoftDeleteFilter = PersistenceDefaults.SoftDeleteFilterName;

    protected ITenantContext TenantContext { get; } = tenantContext;

    /// <summary>PostgreSQL şeması: "identity", "org", "leave" …</summary>
    public abstract string Schema { get; }

    public string ModuleName => Schema;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>Ortak denetim tablosu (<c>audit.audit_log_entries</c>); kiracı filtresiyle okunur, şema sahibi AuditDbContext.</summary>
    public DbSet<Audit.AuditLogEntry> AuditLogEntries => Set<Audit.AuditLogEntry>();

    /// <summary>Global filter'da kullanılan, sorgu çevirisine gömülen kiracı kimliği.</summary>
    protected Guid CurrentTenantId => TenantContext.IsResolved ? TenantContext.TenantId : Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());

        // Denetim tablosu tüm modüllerde ortak; tabloyu yalnız AuditDbContext'in migration'ı oluşturur.
        modelBuilder.ApplyConfiguration(new Audit.AuditLogEntryConfiguration());
        modelBuilder.Entity<Audit.AuditLogEntry>().ToTable(Audit.AuditLogTables.TableName, Audit.AuditLogTables.Schema, t => t.ExcludeFromMigrations());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            if (entityType.IsOwned())
            {
                continue;
            }

            if (typeof(ITenantEntity).IsAssignableFrom(clr))
            {
                modelBuilder.Entity(clr).HasQueryFilter(TenantFilter, BuildTenantFilter(clr));
                modelBuilder.Entity(clr).HasIndex(nameof(ITenantEntity.TenantId));
            }

            if (typeof(ISoftDelete).IsAssignableFrom(clr))
            {
                modelBuilder.Entity(clr).HasQueryFilter(SoftDeleteFilter, BuildSoftDeleteFilter(clr));
            }

            if (typeof(IAuditable).IsAssignableFrom(clr))
            {
                modelBuilder.Entity(clr).Property(nameof(IAuditable.CreatedAt)).IsRequired();
            }
        }

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Tüm DateTime alanları UTC timestamptz; DateOnly → date; decimal(18,4) para varsayılanı.
        configurationBuilder.Properties<DateTime>().HaveColumnType(PersistenceDefaults.TimestampTzColumnType);
        configurationBuilder.Properties<decimal>().HavePrecision(PersistenceDefaults.DecimalPrecision, PersistenceDefaults.DecimalScale);
        configurationBuilder.Properties<string>().HaveMaxLength(PersistenceDefaults.DefaultStringLength);
        base.ConfigureConventions(configurationBuilder);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            await action(cancellationToken).ConfigureAwait(false);
            return;
        }

        var strategy = Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            await using IDbContextTransaction tx = await Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await action(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private LambdaExpression BuildTenantFilter(Type clr)
    {
        // e => e.TenantId == CurrentTenantId  (CurrentTenantId sorgu parametresi olarak çevrilir)
        var parameter = Expression.Parameter(clr, "e");
        var tenantProp = Expression.Property(parameter, nameof(ITenantEntity.TenantId));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        return Expression.Lambda(Expression.Equal(tenantProp, current), parameter);
    }

    private static LambdaExpression BuildSoftDeleteFilter(Type clr)
    {
        var parameter = Expression.Parameter(clr, "e");
        var deleted = Expression.Property(parameter, nameof(ISoftDelete.IsDeleted));
        return Expression.Lambda(Expression.Not(deleted), parameter);
    }
}

/// <summary>Kalıcılık katmanı sabitleri.</summary>
public static class PersistenceDefaults
{
    public const int DefaultStringLength = 500;
    public const int DecimalPrecision = 18;
    public const int DecimalScale = 4;
    public const string TimestampTzColumnType = "timestamp with time zone";
    public const string JsonbColumnType = "jsonb";
    public const string TenantFilterName = "Tenant";
    public const string SoftDeleteFilterName = "SoftDelete";
    public const string OutboxTable = "outbox_messages";
    public const string InboxTable = "inbox_messages";
}
