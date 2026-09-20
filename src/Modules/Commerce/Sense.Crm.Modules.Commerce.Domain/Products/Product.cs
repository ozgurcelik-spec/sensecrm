using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Commerce.Domain.Products;

/// <summary>
/// Ürün kataloğu kaydı. Belge kalemleri ürün değerlerini <b>anlık görüntü</b> olarak kopyalar (yumuşak bağ): ürün sonradan
/// değişse/silinse belgeler değişmez. <see cref="Code"/> (SKU) kiracıda büyük/küçük harf duyarsız benzersizdir (silinmemişler arasında);
/// karşılaştırma kolonu <see cref="CodeNormalized"/>'dır. Kişisel veri alanı yoktur (denetim kaydında maskelenecek alan yok).
/// </summary>
public sealed class Product : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    public const string DefaultCurrency = "TRY";

    private Product()
    {
    }

    private Product(Guid id, Guid tenantId) : base(id, tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Code { get; private set; }

    /// <summary><see cref="Code"/>'un <c>ToUpperInvariant</c> hâli (benzersizlik kolonu); kod yoksa null.</summary>
    public string? CodeNormalized { get; private set; }

    public string? Description { get; private set; }

    public decimal UnitPrice { get; private set; }

    public string Currency { get; private set; } = DefaultCurrency;

    public decimal TaxRate { get; private set; }

    public string? Unit { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>Birincil tedarikçi (yumuşak bağ; tedarikçi silinince aynı transaction'da temizlenir).</summary>
    public Guid? VendorId { get; private set; }

    /// <summary>Tedarikçi tarafı birim fiyat (ürün para biriminde; satın alma emri kalem fiyatını önerir).</summary>
    public decimal? PurchasePrice { get; private set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    /// <summary>Kod normalizasyonu: kırpılır; boşsa null; benzersizlik için büyük harfe çevrilir.</summary>
    public static string? NormalizeCode(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    public static Product Create(
        Guid tenantId,
        string name,
        string? code,
        string? description,
        decimal unitPrice,
        string? currency,
        decimal taxRate,
        string? unit,
        bool isActive,
        Guid? vendorId = null,
        decimal? purchasePrice = null)
    {
        var product = new Product(Guid.CreateVersion7(), Guard.NotDefault(tenantId));
        product.Apply(name, code, description, unitPrice, currency, taxRate, unit, isActive, vendorId, purchasePrice);
        return product;
    }

    /// <summary>Tam değiştirme (PUT): gönderilmeyen isteğe bağlı alan (<paramref name="vendorId"/>, <paramref name="purchasePrice"/> dahil) temizlenir.</summary>
    public void Update(
        string name,
        string? code,
        string? description,
        decimal unitPrice,
        string? currency,
        decimal taxRate,
        string? unit,
        bool isActive,
        Guid? vendorId = null,
        decimal? purchasePrice = null) => Apply(name, code, description, unitPrice, currency, taxRate, unit, isActive, vendorId, purchasePrice);

    /// <summary>Tedarikçi silindiğinde birincil tedarikçi bağı temizlenir.</summary>
    public void ClearVendor() => VendorId = null;

    private void Apply(string name, string? code, string? description, decimal unitPrice, string? currency, decimal taxRate, string? unit, bool isActive, Guid? vendorId, decimal? purchasePrice)
    {
        VendorId = vendorId is { } vendor ? Guard.NotDefault(vendor) : null;
        PurchasePrice = purchasePrice is { } price ? Guard.InRange(price, 0m, CommerceLimits.MaxUnitPrice) : null;
        Name = Guard.MaxLength(Guard.NotEmpty(name), CommerceLimits.NameMaxLength);
        var trimmedCode = code?.Trim();
        Code = string.IsNullOrEmpty(trimmedCode) ? null : Guard.MaxLength(trimmedCode, CommerceLimits.CodeMaxLength);
        CodeNormalized = NormalizeCode(Code);
        Description = Clean(description, CommerceLimits.ProductDescriptionMaxLength);
        UnitPrice = Guard.InRange(unitPrice, 0m, CommerceLimits.MaxUnitPrice);
        var cur = string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : currency.Trim().ToUpperInvariant();
        Guard.Against(cur.Length != CommerceLimits.CurrencyLength, nameof(currency));
        Currency = cur;
        TaxRate = Guard.InRange(taxRate, 0m, CommerceLimits.MaxPercent);
        Unit = Clean(unit, CommerceLimits.UnitMaxLength);
        IsActive = isActive;
    }

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
    }
}
