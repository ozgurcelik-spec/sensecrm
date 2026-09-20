using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sense.Crm.Modules.Commerce.Domain;
using Sense.Crm.Modules.Commerce.Domain.Documents;
using Sense.Crm.Modules.Commerce.Domain.Invoices;
using Sense.Crm.Modules.Commerce.Domain.Numbering;
using Sense.Crm.Modules.Commerce.Domain.Orders;
using Sense.Crm.Modules.Commerce.Domain.PriceBooks;
using Sense.Crm.Modules.Commerce.Domain.Products;
using Sense.Crm.Modules.Commerce.Domain.PurchaseOrders;
using Sense.Crm.Modules.Commerce.Domain.Quotes;
using Sense.Crm.Modules.Commerce.Domain.Vendors;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Commerce.Infrastructure.Persistence;

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

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();

    public DbSet<InvoicePayment> InvoicePayments => Set<InvoicePayment>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();

    public DbSet<Vendor> Vendors => Set<Vendor>();

    public DbSet<PriceBook> PriceBooks => Set<PriceBook>();

    public DbSet<PriceBookEntry> PriceBookEntries => Set<PriceBookEntry>();

    public DbSet<AccountPriceBook> AccountPriceBooks => Set<AccountPriceBook>();

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
    public const string Invoices = "invoices";
    public const string InvoiceLines = "invoice_lines";
    public const string InvoicePayments = "invoice_payments";
    public const string PurchaseOrders = "purchase_orders";
    public const string PurchaseOrderLines = "purchase_order_lines";
    public const string Vendors = "vendors";
    public const string PriceBooks = "price_books";
    public const string PriceBookEntries = "price_book_entries";
    public const string AccountPriceBooks = "account_price_books";
    public const string DocumentCounters = "document_counters";

    public const string ProductCodeIndex = "ux_products_tenant_code";
    public const string QuoteNumberIndex = "ux_quotes_tenant_number";
    public const string OrderNumberIndex = "ux_sales_orders_tenant_number";
    public const string OrderQuoteIndex = "ux_sales_orders_tenant_quote";
    public const string InvoiceNumberIndex = "ux_invoices_tenant_number";
    public const string InvoiceOrderIndex = "ux_invoices_tenant_order";
    public const string PurchaseOrderNumberIndex = "ux_purchase_orders_tenant_number";
    public const string PriceBookNameIndex = "ux_price_books_tenant_name";
    public const string PriceBookEntryIndex = "ux_price_book_entries_tenant_book_product";
    public const string AccountPriceBookIndex = "ux_account_price_books_tenant_account";
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
        b.Property(x => x.PurchasePrice).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.UnitPriceScale);
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
        b.HasIndex(x => new { x.TenantId, x.VendorId });
        b.Ignore(x => x.DomainEvents);
    }
}

/// <summary>
/// Belge tabanının (<see cref="CommerceDocument"/>) ortak kolon ayarları. <see cref="ConfigureExtras"/> M9C'nin eklediği kolonlar (nakliye, iki adres bloğu,
/// yuvarlama) içindir ve <b>her belge tablosuna</b> uygulanır; <see cref="ConfigureCommon"/> yeni belge tablolarının (fatura, satın alma emri) M6A'dan gelen ortak
/// kolonlarını ekler (teklif/sipariş bunları kendi yapılandırmasında zaten taşır).
/// </summary>
internal static class DocumentColumns
{
    public static void ConfigureCommon<T>(EntityTypeBuilder<T> b)
        where T : CommerceDocument
    {
        b.Property(x => x.Number).HasMaxLength(CommerceLimits.NumberMaxLength).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(CommerceLimits.SubjectMaxLength).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(CommerceLimits.CurrencyLength).IsRequired();
        b.Property(x => x.Terms).HasMaxLength(CommerceLimits.TermsMaxLength);
        b.Property(x => x.Notes).HasMaxLength(CommerceLimits.NotesMaxLength);
        b.Property(x => x.Subtotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.DiscountTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.TaxTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.GrandTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);

        // PostgreSQL xmin sistem kolonu eşzamanlılık belirteci (migration kolon üretmez): çakışma → DbUpdateConcurrencyException.
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }

    public static void ConfigureExtras<T>(EntityTypeBuilder<T> b)
        where T : CommerceDocument
    {
        b.Property(x => x.Carrier).HasMaxLength(CommerceLimits.CarrierMaxLength);
        b.Property(x => x.Adjustment).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale).HasDefaultValue(0m);
        b.Property(x => x.BillingStreet).HasMaxLength(CommerceLimits.StreetMaxLength);
        b.Property(x => x.BillingBuilding).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.BillingCity).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.BillingState).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.BillingPostalCode).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.BillingCountry).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.ShippingStreet).HasMaxLength(CommerceLimits.StreetMaxLength);
        b.Property(x => x.ShippingBuilding).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.ShippingCity).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.ShippingState).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.ShippingPostalCode).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.ShippingCountry).HasMaxLength(CommerceLimits.AddressPartMaxLength);

        // Adres değer nesnesi düz kolonlardan türetilir (denetim alan bazında fark üretsin diye).
        b.Ignore(x => x.BillingAddress);
        b.Ignore(x => x.ShippingAddress);
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
        DocumentColumns.ConfigureExtras(b);

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
        b.HasIndex(x => new { x.TenantId, x.PriceBookId });
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
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
        b.Property(x => x.DueDate).HasColumnType("date");
        b.Property(x => x.CustomerPoNumber).HasMaxLength(CommerceLimits.CustomerPoNumberMaxLength);
        b.Property(x => x.ExciseTax).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.SalesCommission).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.Pending).HasMaxLength(CommerceLimits.PendingMaxLength);
        b.Property(x => x.Subtotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.DiscountTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.TaxTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.GrandTotal).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        DocumentColumns.ConfigureExtras(b);
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
        b.HasIndex(x => new { x.TenantId, x.PriceBookId });
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
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

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable(CommerceTables.Invoices);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        DocumentColumns.ConfigureCommon(b);
        DocumentColumns.ConfigureExtras(b);

        // Saklanan durum yalnız Draft/Sent/Cancelled (etkin durum türetilir).
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(CommerceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.InvoiceDate).HasColumnType("date");
        b.Property(x => x.DueDate).HasColumnType("date");
        b.Property(x => x.CustomerPoNumber).HasMaxLength(CommerceLimits.CustomerPoNumberMaxLength);
        b.Property(x => x.ExciseTax).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.SalesCommission).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.PaidAmount).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale).HasDefaultValue(0m);
        b.Property(x => x.CancelReason).HasMaxLength(CommerceLimits.ReasonMaxLength);
        b.Ignore(x => x.BalanceAmount);

        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique().HasDatabaseName(CommerceTables.InvoiceNumberIndex);

        // Çift dönüşüm engeli (kesin): bir sipariş için iptal edilmemiş ve silinmemiş en çok bir fatura (saklanan enum adı "Cancelled").
        b.HasIndex(x => new { x.TenantId, x.OrderId })
            .IsUnique()
            .HasFilter("order_id IS NOT NULL AND is_deleted = false AND status <> 'Cancelled'")
            .HasDatabaseName(CommerceTables.InvoiceOrderIndex);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.AccountId });
        b.HasIndex(x => new { x.TenantId, x.DealId });
        b.HasIndex(x => new { x.TenantId, x.InvoiceDate });
        b.HasIndex(x => new { x.TenantId, x.DueDate });
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ToTable(CommerceTables.InvoiceLines);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        LineColumns.Configure(b);
        b.HasIndex(x => new { x.TenantId, x.InvoiceId, x.Position });
        b.HasIndex(x => new { x.TenantId, x.ProductId });
    }
}

public sealed class InvoicePaymentConfiguration : IEntityTypeConfiguration<InvoicePayment>
{
    public void Configure(EntityTypeBuilder<InvoicePayment> b)
    {
        b.ToTable(CommerceTables.InvoicePayments);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Amount).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.PaidOn).HasColumnType("date");
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(CommerceLimits.PaymentMethodColumnMaxLength);
        b.Property(x => x.Reference).HasMaxLength(CommerceLimits.PaymentReferenceMaxLength);
        b.Property(x => x.Notes).HasMaxLength(CommerceLimits.PaymentNotesMaxLength);
        b.HasIndex(x => new { x.TenantId, x.InvoiceId, x.PaidOn });
    }
}

public sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.ToTable(CommerceTables.PurchaseOrders);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        DocumentColumns.ConfigureCommon(b);
        DocumentColumns.ConfigureExtras(b);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(CommerceLimits.EnumColumnMaxLength).IsRequired();
        b.Property(x => x.PoDate).HasColumnType("date");
        b.Property(x => x.DueDate).HasColumnType("date");
        b.Property(x => x.ExciseTax).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.SalesCommission).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.AmountScale);
        b.Property(x => x.CancelReason).HasMaxLength(CommerceLimits.ReasonMaxLength);

        b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique().HasDatabaseName(CommerceTables.PurchaseOrderNumberIndex);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.VendorId });
        b.HasIndex(x => new { x.TenantId, x.PoDate });
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable(CommerceTables.PurchaseOrderLines);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        LineColumns.Configure(b);
        b.HasIndex(x => new { x.TenantId, x.PurchaseOrderId, x.Position });
        b.HasIndex(x => new { x.TenantId, x.ProductId });
    }
}

public sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> b)
    {
        b.ToTable(CommerceTables.Vendors);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(CommerceLimits.NameMaxLength).IsRequired();
        b.Property(x => x.Phone).HasMaxLength(CommerceLimits.VendorPhoneMaxLength);
        b.Property(x => x.Email).HasMaxLength(CommerceLimits.VendorEmailMaxLength);
        b.Property(x => x.Website).HasMaxLength(CommerceLimits.VendorWebsiteMaxLength);
        b.Property(x => x.Category).HasMaxLength(CommerceLimits.VendorTextMaxLength);
        b.Property(x => x.GlAccount).HasMaxLength(CommerceLimits.VendorTextMaxLength);
        b.Property(x => x.Description).HasMaxLength(CommerceLimits.VendorDescriptionMaxLength);
        b.Property(x => x.AddressStreet).HasMaxLength(CommerceLimits.StreetMaxLength);
        b.Property(x => x.AddressBuilding).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.AddressCity).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.AddressState).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.AddressPostalCode).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.Property(x => x.AddressCountry).HasMaxLength(CommerceLimits.AddressPartMaxLength);
        b.HasIndex(x => new { x.TenantId, x.Name });
        b.HasIndex(x => new { x.TenantId, x.Category });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Ignore(x => x.Address);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class PriceBookConfiguration : IEntityTypeConfiguration<PriceBook>
{
    public void Configure(EntityTypeBuilder<PriceBook> b)
    {
        b.ToTable(CommerceTables.PriceBooks);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(CommerceLimits.NameMaxLength).IsRequired();
        b.Property(x => x.NameNormalized).HasMaxLength(CommerceLimits.NameMaxLength).IsRequired();
        b.Property(x => x.PricingModel).HasConversion<string>().HasMaxLength(CommerceLimits.PricingModelColumnMaxLength).IsRequired();
        b.Property(x => x.AdjustmentPercent).HasPrecision(CommerceLimits.AdjustmentPercentPrecision, CommerceLimits.PercentScale);
        b.Property(x => x.Currency).HasMaxLength(CommerceLimits.CurrencyLength).IsRequired();
        b.Property(x => x.ValidFrom).HasColumnType("date");
        b.Property(x => x.ValidTo).HasColumnType("date");
        b.Property(x => x.Description).HasMaxLength(CommerceLimits.PriceBookDescriptionMaxLength);

        // Ad kiracıda büyük/küçük harf duyarsız benzersiz (silinmemişler arasında; silinen listenin adı yeniden kullanılabilir).
        b.HasIndex(x => new { x.TenantId, x.NameNormalized })
            .IsUnique()
            .HasFilter("is_deleted = false")
            .HasDatabaseName(CommerceTables.PriceBookNameIndex);
        b.HasIndex(x => new { x.TenantId, x.IsActive });
        b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class PriceBookEntryConfiguration : IEntityTypeConfiguration<PriceBookEntry>
{
    public void Configure(EntityTypeBuilder<PriceBookEntry> b)
    {
        b.ToTable(CommerceTables.PriceBookEntries);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.UnitPrice).HasPrecision(CommerceLimits.AmountPrecision, CommerceLimits.UnitPriceScale);
        b.HasIndex(x => new { x.TenantId, x.PriceBookId, x.ProductId }).IsUnique().HasDatabaseName(CommerceTables.PriceBookEntryIndex);
        b.HasIndex(x => new { x.TenantId, x.ProductId });
        b.HasOne<PriceBook>().WithMany().HasForeignKey(x => x.PriceBookId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AccountPriceBookConfiguration : IEntityTypeConfiguration<AccountPriceBook>
{
    public void Configure(EntityTypeBuilder<AccountPriceBook> b)
    {
        b.ToTable(CommerceTables.AccountPriceBooks);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => new { x.TenantId, x.AccountId }).IsUnique().HasDatabaseName(CommerceTables.AccountPriceBookIndex);
        b.HasIndex(x => new { x.TenantId, x.PriceBookId });
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

/// <summary>Belge kalemlerinin ortak kolon ayarları (adet/fiyat <c>decimal(18,4)</c>, yüzdeler <c>decimal(5,2)</c>, tutarlar <c>decimal(18,2)</c>).</summary>
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
