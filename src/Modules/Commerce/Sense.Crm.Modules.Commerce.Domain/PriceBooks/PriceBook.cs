using System.Linq.Expressions;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Domain.PriceBooks;

/// <summary>Fiyatlandırma modeli: <c>PerProduct</c> = ürün başına açık liste fiyatı; <c>Flat</c> = katalog fiyatına tek işaretli yüzde (negatif iskonto, pozitif zam).</summary>
public enum PricingModel
{
    PerProduct,
    Flat,
}

/// <summary>Çözülen fiyatın kaynağı: liste girdisi, flat yüzde veya ürün katalog fiyatı.</summary>
public enum PriceSource
{
    Entry,
    Flat,
    Catalog,
}

public readonly record struct ResolvedPrice(decimal UnitPrice, PriceSource Source);

/// <summary>
/// Fiyat çözümü (saf, sunucu belirleyici). <c>flat</c>: <c>Round4(catalogPrice × (1 + adjustmentPercent / 100))</c>, <see cref="MidpointRounding.AwayFromZero"/>;
/// <c>perProduct</c>: girdi varsa girdi, yoksa katalog. Liste para birimi ürününkinden farklıysa ürün katalog fiyatıyla döner (belge kuralı zaten
/// uyuşmayan ürünü reddeder). Yazma yolu ve <c>POST /pricebooks/{id}/resolve</c> aynı kodu kullanır.
/// </summary>
public static class PriceResolution
{
    private const int PriceDigits = 4;
    private const decimal PercentBase = 100m;

    public static ResolvedPrice Resolve(
        PricingModel model,
        decimal? adjustmentPercent,
        string bookCurrency,
        string productCurrency,
        decimal catalogPrice,
        decimal? entryPrice)
    {
        if (!string.Equals(bookCurrency, productCurrency, StringComparison.Ordinal))
        {
            return new ResolvedPrice(catalogPrice, PriceSource.Catalog);
        }

        return model switch
        {
            PricingModel.Flat when adjustmentPercent is { } percent => new ResolvedPrice(Round4(catalogPrice * (1m + (percent / PercentBase))), PriceSource.Flat),
            PricingModel.PerProduct when entryPrice is { } entry => new ResolvedPrice(entry, PriceSource.Entry),
            _ => new ResolvedPrice(catalogPrice, PriceSource.Catalog),
        };
    }

    public static decimal Round4(decimal value) => decimal.Round(value, PriceDigits, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Fiyat listesi (Zoho "Price Book"): kiracıda benzersiz adlı (büyük/küçük harf duyarsız, silinmemişler arasında), model ve para birimi oluşturmadan sonra
/// <b>değişmez</b>. Etkinlik türetilir: <c>isActive ∧ validFrom ≤ bugün ∧ validTo ≥ bugün</c> (boş uçlar sınırsız). Kişisel veri alanı yoktur.
/// </summary>
public sealed class PriceBook : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    public const string DefaultCurrency = "TRY";

    private PriceBook()
    {
    }

    private PriceBook(Guid id, Guid tenantId) : base(id, tenantId)
    {
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary><see cref="Name"/>'in kırpılmış <c>ToUpperInvariant</c> hâli (benzersizlik kolonu).</summary>
    public string NameNormalized { get; private set; } = string.Empty;

    public Guid OwnerUserId { get; private set; }

    public bool IsActive { get; private set; } = true;

    public PricingModel PricingModel { get; private set; }

    /// <summary>Yalnız <c>flat</c>: −99.99…1000.00, ≤2 ondalık.</summary>
    public decimal? AdjustmentPercent { get; private set; }

    public string Currency { get; private set; } = DefaultCurrency;

    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public string? Description { get; private set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

    /// <summary>Etkin mi (kiracı "bugün"ü ile).</summary>
    public bool IsEffective(DateOnly today) => IsActive && (ValidFrom is null || ValidFrom <= today) && (ValidTo is null || ValidTo >= today);

    /// <summary>Etkinlik filtresi (EF'e çevrilir; <see cref="IsEffective"/> ile aynı tanım).</summary>
    public static Expression<Func<PriceBook, bool>> EffectiveExpression(DateOnly today) =>
        b => b.IsActive && (b.ValidFrom == null || b.ValidFrom <= today) && (b.ValidTo == null || b.ValidTo >= today);

    public static PriceBook Create(
        Guid tenantId,
        string name,
        Guid ownerUserId,
        bool isActive,
        PricingModel model,
        decimal? adjustmentPercent,
        string? currency,
        DateOnly? validFrom,
        DateOnly? validTo,
        string? description)
    {
        var book = new PriceBook(Guid.CreateVersion7(), Guard.NotDefault(tenantId))
        {
            PricingModel = model,
            Currency = NormalizeCurrency(currency),
        };
        book.Apply(name, ownerUserId, isActive, adjustmentPercent, validFrom, validTo, description);
        return book;
    }

    /// <summary>
    /// Tam değiştirme (PUT): <c>pricingModel</c> ve <c>currency</c> farklı değerle gelirse <c>pricebook.model_immutable</c> 409 (<c>args.property</c>);
    /// <c>null</c> = değişmedi.
    /// </summary>
    public Result Update(
        string name,
        Guid ownerUserId,
        bool isActive,
        PricingModel? model,
        decimal? adjustmentPercent,
        string? currency,
        DateOnly? validFrom,
        DateOnly? validTo,
        string? description)
    {
        if (model is { } requestedModel && requestedModel != PricingModel)
        {
            return Error.Conflict(CommerceErrors.PriceBookModelImmutable, ("property", "pricingModel"));
        }

        if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(NormalizeCurrency(currency), Currency, StringComparison.Ordinal))
        {
            return Error.Conflict(CommerceErrors.PriceBookModelImmutable, ("property", "currency"));
        }

        Apply(name, ownerUserId, isActive, adjustmentPercent, validFrom, validTo, description);
        return Result.Success();
    }

    private void Apply(string name, Guid ownerUserId, bool isActive, decimal? adjustmentPercent, DateOnly? validFrom, DateOnly? validTo, string? description)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(name), CommerceLimits.NameMaxLength);
        NameNormalized = NormalizeName(Name);
        OwnerUserId = Guard.NotDefault(ownerUserId);
        IsActive = isActive;
        Guard.Against(PricingModel == PricingModel.Flat && adjustmentPercent is null, nameof(adjustmentPercent));
        Guard.Against(PricingModel == PricingModel.PerProduct && adjustmentPercent is not null, nameof(adjustmentPercent));
        AdjustmentPercent = adjustmentPercent is { } percent
            ? Guard.InRange(percent, CommerceLimits.MinAdjustmentPercent, CommerceLimits.MaxAdjustmentPercent)
            : null;
        Guard.Against(validFrom is { } from && validTo is { } to && to < from, nameof(validTo));
        ValidFrom = validFrom;
        ValidTo = validTo;
        var trimmed = description?.Trim();
        Description = string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, CommerceLimits.PriceBookDescriptionMaxLength);
    }

    private static string NormalizeCurrency(string? currency)
    {
        var cur = string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : currency.Trim().ToUpperInvariant();
        Guard.Against(cur.Length != CommerceLimits.CurrencyLength, nameof(currency));
        return cur;
    }

    /// <summary>Bu liste için bir ürünün fiyatını çözer (<see cref="PriceResolution"/>).</summary>
    public ResolvedPrice Resolve(string productCurrency, decimal catalogPrice, decimal? entryPrice) =>
        PriceResolution.Resolve(PricingModel, AdjustmentPercent, Currency, productCurrency, catalogPrice, entryPrice);
}

/// <summary>
/// <c>perProduct</c> liste girdisi (<c>commerce.price_book_entries</c>): ürün başına açık liste fiyatı (<c>decimal(18,4)</c>). Fiyat değişimi denetlenir
/// (<c>IAuditLogged</c>); silme fizikseldir (denetim satırı kalır). Ürün silinince girdiler aynı transaction'da silinir.
/// </summary>
public sealed class PriceBookEntry : TenantEntity<Guid>, IAuditLogged
{
    private PriceBookEntry()
    {
    }

    private PriceBookEntry(Guid tenantId, Guid priceBookId, Guid productId, decimal unitPrice) : base(Guid.CreateVersion7(), tenantId)
    {
        PriceBookId = priceBookId;
        ProductId = productId;
        SetPrice(unitPrice);
    }

    public Guid PriceBookId { get; private set; }

    public Guid ProductId { get; private set; }

    public decimal UnitPrice { get; private set; }

    public static PriceBookEntry Create(Guid tenantId, Guid priceBookId, Guid productId, decimal unitPrice) =>
        new(Guard.NotDefault(tenantId), Guard.NotDefault(priceBookId), Guard.NotDefault(productId), unitPrice);

    public void SetPrice(decimal unitPrice) => UnitPrice = Guard.InRange(unitPrice, 0m, CommerceLimits.MaxUnitPrice);
}

/// <summary>
/// Firma varsayılan fiyat listesi (<c>commerce.account_price_books</c>; kiracı+firma başına en çok bir satır). Sunucu belgeye otomatik uygulamaz;
/// web belge editöründe firma seçilince etkin varsayılanı önerir.
/// </summary>
public sealed class AccountPriceBook : TenantEntity<Guid>
{
    private AccountPriceBook()
    {
    }

    private AccountPriceBook(Guid tenantId, Guid accountId, Guid priceBookId) : base(Guid.CreateVersion7(), tenantId)
    {
        AccountId = accountId;
        PriceBookId = priceBookId;
    }

    public Guid AccountId { get; private set; }

    public Guid PriceBookId { get; private set; }

    public static AccountPriceBook Create(Guid tenantId, Guid accountId, Guid priceBookId) =>
        new(Guard.NotDefault(tenantId), Guard.NotDefault(accountId), Guard.NotDefault(priceBookId));

    public void SetPriceBook(Guid priceBookId) => PriceBookId = Guard.NotDefault(priceBookId);
}
