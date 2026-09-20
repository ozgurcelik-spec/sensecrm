using Microsoft.EntityFrameworkCore;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Spikes.M9h.Core;

namespace Sense.Crm.Spikes.M9h.Model;

public sealed class SDeal : ITenantEntity, ISoftDelete
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }
}

public sealed class SAccount : ITenantEntity, ISoftDelete
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public List<SContact> Contacts { get; set; } = [];
}

public sealed class SContact : ITenantEntity, ISoftDelete
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid AccountId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }
}

/// <summary>Service <c>case</c> benzeri: atanan VEYA oluşturan sahip yolu + sahipsiz havuz.</summary>
public sealed class SCase : ITenantEntity, ISoftDelete
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? AssignedUserId { get; set; }

    public Guid CreatedUserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public List<SCaseComment> Comments { get; set; } = [];
}

/// <summary>Alt kayıt: kendi sahibi yok, üst kaydın (case) görünürlüğünü devralır.</summary>
public sealed class SCaseComment : ITenantEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CaseId { get; set; }

    public string Body { get; set; } = string.Empty;
}

/// <summary>Kapsamsız (kiracı geneli) katalog: yalnız Tenant filtresi.</summary>
public sealed class SProduct : ITenantEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// GERÇEK <see cref="ModuleDbContext"/> tabanı (gerçek "Tenant" + "SoftDelete" adlı filtreler) üzerine üçüncü "RecordScope" filtresini
/// taban <c>OnModelCreating</c>'in yaptığı gibi bir döngüde ekler. İki alt sınıf iki filtre stilini (A/B) ayrı model olarak dener.
/// </summary>
public abstract class SpikeDbContextBase(DbContextOptions options, ITenantContext tenant) : ModuleDbContext(options, tenant), ISpikeContext
{
    public override string Schema => "spike";

    public RecordScopeView RecordScope { get; } = new();

    protected abstract FilterStyle Style { get; }

    public DbSet<SDeal> Deals => Set<SDeal>();

    public DbSet<SAccount> Accounts => Set<SAccount>();

    public DbSet<SContact> Contacts => Set<SContact>();

    public DbSet<SCase> Cases => Set<SCase>();

    public DbSet<SCaseComment> CaseComments => Set<SCaseComment>();

    public DbSet<SProduct> Products => Set<SProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Kaynak yapılandırmaları (ApplyConfigurationsFromAssembly'nin yerini tutan satır içi eşdeğer): tek satırlık HasRecordScope.
        modelBuilder.Entity<SDeal>(b =>
        {
            b.ToTable("deals");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasRecordScope("deal", x => x.OwnerUserId);
        });
        modelBuilder.Entity<SAccount>(b =>
        {
            b.ToTable("accounts");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasMany(x => x.Contacts).WithOne().HasForeignKey(x => x.AccountId);
            b.HasRecordScope("account", x => x.OwnerUserId);
        });
        modelBuilder.Entity<SContact>(b =>
        {
            b.ToTable("contacts");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasRecordScope("contact", x => x.OwnerUserId);
        });
        modelBuilder.Entity<SCase>(b =>
        {
            b.ToTable("cases");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.AssignedUserId });
            b.HasIndex(x => new { x.TenantId, x.CreatedUserId });
            b.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.CaseId);
            b.HasRecordScope("case", x => x.AssignedUserId, x => x.CreatedUserId);
        });
        modelBuilder.Entity<SCaseComment>(b =>
        {
            b.ToTable("case_comments");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.CaseId });
            b.HasRecordScopeThroughParent<SCaseComment, SCase>(c => c.CaseId, p => p.Id);
        });
        modelBuilder.Entity<SProduct>(b =>
        {
            b.ToTable("products");
            b.HasKey(x => x.Id);
        });

        base.OnModelCreating(modelBuilder); // Tenant + SoftDelete adlı filtreler (gerçek kod)

        // --- Üçüncü adlı filtre: gerçek taban sınıfa eklenecek 6 satırlık döngünün spike eşdeğeri ---
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.FindAnnotation(RecordScopeModelExtensions.AnnotationName)?.Value is RecordScopeFilterFactory factory)
            {
                var ctxExpr = System.Linq.Expressions.Expression.Constant(this);
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(RecordScopeModelExtensions.FilterName, factory(Style, ctxExpr, this));
            }
        }
    }
}

public sealed class SpikeStaticDbContext(DbContextOptions<SpikeStaticDbContext> options, ITenantContext tenant) : SpikeDbContextBase(options, tenant)
{
    protected override FilterStyle Style => FilterStyle.StaticCalls;
}

public sealed class SpikeCtxArgDbContext(DbContextOptions<SpikeCtxArgDbContext> options, ITenantContext tenant) : SpikeDbContextBase(options, tenant)
{
    protected override FilterStyle Style => FilterStyle.StaticWithContextArg;
}

public sealed class SpikeMemberDbContext(DbContextOptions<SpikeMemberDbContext> options, ITenantContext tenant) : SpikeDbContextBase(options, tenant)
{
    protected override FilterStyle Style => FilterStyle.ContextMember;
}
