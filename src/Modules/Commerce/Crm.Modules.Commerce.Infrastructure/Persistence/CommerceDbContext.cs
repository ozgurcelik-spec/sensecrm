using Crm.Modules.Commerce.Domain;
using Crm.Modules.Commerce.Domain.Numbering;
using Crm.Modules.Commerce.Domain.Orders;
using Crm.Modules.Commerce.Domain.Products;
using Crm.Modules.Commerce.Domain.Quotes;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Modules.Commerce.Infrastructure.Persistence;

/// <summary>Commerce modülü DbContext'i (ADR 0003: modül başına bir DbContext, kendi PostgreSQL şeması <c>commerce</c>).</summary>
public sealed class CommerceDbContext(DbContextOptions<CommerceDbContext> options, ITenantContext tenantContext)
    : ModuleDbContext(options, tenantContext)
{
    public const string SchemaName = "commerce";

    public override string Schema => SchemaName;

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();

    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();

    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();

    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();
}

/// <summary>Tablo ve benzersiz indeks adları (ham SQL ve çakışma eşlemesi bunlara dayanır).</summary>
public static class CommerceTables
{
    public const string Products = "products";
    public const string Quotes = "quotes";
    public const string QuoteLines = "quote_lines";
    public const string SalesOrders = "sales_orders";
    public const string SalesOrderLines = "sales_order_lines";
    public const string DocumentCounters = "document_counters";

    public const string ProductCodeIndex = "ux_products_tenant_code";
    public const string QuoteNumberIndex = "ux_quotes_tenant_number";
    public const string OrderNumberIndex = "ux_sales_orders_tenant_number";
    public const string OrderQuoteIndex = "ux_sales_orders_tenant_quote";
}

// Tüm sorgu yolları TenantId ile başlayan bileşik indekslerle karşılanır (K3): durum, firma, fırsat, sahip, oluşturulma, geçerlilik.

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable(CommerceTables.Products);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(CommerceLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Code).HasMaxLength(CommerceLimits.CodeMaxLength);
        b.Property(x => x.CodeNormalized).HasMaxLength(CommerceLimits.CodeMaxLength);
        b.Property(x => x.Description).HasMaxLength(CommerceLimits.ProductDescriptionMaxLength);
        b.Property(x => x.UnitPrice).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.UnitPriceScale);
        b.Property(x => x.Currency).HasMaxLength(CommerceLimits.CurrencyLength).IsRequired();
        b.Property(x => x.TaxRate).HasPrecision(CommerceLimits.PercentPrecision, CommerceLimits.PercentScale);
        b.Property(x => x.Unit).HasMaxLength(CommerceLimits.UnitMaxLength);

        // Kod kiracıda büyük/küçük harf duyarsız benzersiz (silinmemişler arasında; silinen ürünün kodu yeniden kullanılabilir).
        b.HasIndex(x => new { x.TenantId, x.CodeNormalized })
            .IsUnique()
            .HasFilter("is_deleted = false AND code_normalized IS NOT NULL")
            .HasDatabaseName(CommerceTables.ProductCodeIndex);
        b.HasIndex(x => new { x.TenantId, x.Name });
        b.HasIndex(x => new { x.TenantId, x.IsActive });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> b)
    {
        b.ToTable(CommerceTables.Quotes);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Number).HasMaxLength(CommerceLimits.NumberMaxLength).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(CommerceLimits.SubjectMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(CommerceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(CommerceLimits.CurrencyLength).IsRequired();
        b.Property(x => x.Terms).HasMaxLength(CommerceLimits.TermsMaxLength);
        b.Property(x => x.Notes).HasMaxLength(CommerceLimits.NotesMaxLength);
        b.Property(x => x.RejectionReason).HasMaxLength(CommerceLimits.ReasonMaxLength);
        b.Property(x => x.ValidUntil).HasColumnType("date");
        b.Property(x => x.Subtotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.DiscountTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.TaxTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.GrandTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);

        // PostgreSQL xmin sistem kolonu eşzamanlılık belirteci (migration kolon üretmez): çakışma → DbUpdateConcurrencyException.
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique().HasDatabaseName(CommerceTables.QuoteNumberIndex);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.AccountId });
        b.HasIndex(x => new { x.TenantId, x.ContactId });
        b.HasIndex(x => new { x.TenantId, x.DealId });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.ValidUntil });
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class QuoteLineConfiguration : IEntityTypeConfiguration<QuoteLine>
{
    public void Configure(EntityTypeBuilder<QuoteLine> b)
    {
        b.ToTable(CommerceTables.QuoteLines);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        LineColumns.Configure(b);
        b.HasIndex(x => new { x.TenantId, x.QuoteId, x.Position });
        b.HasIndex(x => new { x.TenantId, x.ProductId });
    }
}

public sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> b)
    {
        b.ToTable(CommerceTables.SalesOrders);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Number).HasMaxLength(CommerceLimits.NumberMaxLength).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(CommerceLimits.SubjectMaxLength).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(CommerceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(CommerceLimits.CurrencyLength).IsRequired();
        b.Property(x => x.Terms).HasMaxLength(CommerceLimits.TermsMaxLength);
        b.Property(x => x.Notes).HasMaxLength(CommerceLimits.NotesMaxLength);
        b.Property(x => x.CancelReason).HasMaxLength(CommerceLimits.ReasonMaxLength);
        b.Property(x => x.OrderDate).HasColumnType("date");
        b.Property(x => x.Subtotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.DiscountTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.TaxTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.GrandTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique().HasDatabaseName(CommerceTables.OrderNumberIndex);

        // Çift dönüşüm engeli (kesin): bir teklif için silinmemiş en çok bir sipariş.
        b.HasIndex(x => new { x.TenantId, x.QuoteId })
            .IsUnique()
            .HasFilter("quote_id IS NOT NULL AND is_deleted = false")
            .HasDatabaseName(CommerceTables.OrderQuoteIndex);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.AccountId });
        b.HasIndex(x => new { x.TenantId, x.ContactId });
        b.HasIndex(x => new { x.TenantId, x.DealId });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.OrderDate });
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class SalesOrderLineConfiguration : IEntityTypeConfiguration<SalesOrderLine>
{
    public void Configure(EntityTypeBuilder<SalesOrderLine> b)
    {
        b.ToTable(CommerceTables.SalesOrderLines);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        LineColumns.Configure(b);
        b.HasIndex(x => new { x.TenantId, x.OrderId, x.Position });
        b.HasIndex(x => new { x.TenantId, x.ProductId });
    }
}

public sealed class DocumentCounterConfiguration : IEntityTypeConfiguration<DocumentCounter>
{
    public void Configure(EntityTypeBuilder<DocumentCounter> b)
    {
        b.ToTable(CommerceTables.DocumentCounters);
        b.HasKey(x => new { x.TenantId, x.Kind, x.Year });
        b.Property(x => x.Kind).HasMaxLength(CommerceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.LastValue).IsRequired();
    }
}

/// <summary>Teklif ve sipariş kalemlerinin ortak kolon ayarları (adet/fiyat <c>decimal(18,4)</c>, yüzdeler <c>decimal(5,2)</c>, tutarlar <c>decimal(18,2)</c>).</summary>
internal static class LineColumns
{
    public static void Configure<TLine>(EntityTypeBuilder<TLine> b)
        where TLine : Domain.Documents.DocumentLine
    {
        b.Property(x => x.Description).HasMaxLength(CommerceLimits.LineDescriptionMaxLength).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.QuantityScale);
        b.Property(x => x.UnitPrice).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.UnitPriceScale);
        b.Property(x => x.DiscountPercent).HasPrecision(CommerceLimits.PercentPrecision, CommerceLimits.PercentScale);
        b.Property(x => x.TaxRate).HasPrecision(CommerceLimits.PercentPrecision, CommerceLimits.PercentScale);
        b.Property(x => x.LineSubtotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.LineDiscount).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.LineTax).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.LineTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Ignore(x => x.Amounts);
    }
}
