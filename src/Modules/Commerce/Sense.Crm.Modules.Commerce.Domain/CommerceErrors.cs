namespace Sense.Crm.Modules.Commerce.Domain;

/// <summary>
/// Commerce hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m6a-ticaret.md).
/// Ortak kodlar (validation, forbidden, not_found) <c>Sense.Crm.Shared.Kernel.Results.ErrorCodes</c>'tadır.
/// </summary>
public static class CommerceErrors
{
    /// <summary>Firma/kişi/fırsat aktif organizasyonda yok veya silinmiş → 404.</summary>
    public const string RelatedNotFound = "commerce.related_not_found";

    /// <summary>Kişinin firması belgenin firmasıyla uyuşmuyor → 400.</summary>
    public const string ContactAccountMismatch = "commerce.contact_account_mismatch";

    /// <summary>Fırsatın firması belgenin firmasıyla uyuşmuyor → 400.</summary>
    public const string DealAccountMismatch = "commerce.deal_account_mismatch";

    /// <summary>Eşzamanlı güncelleme (xmin belirteci) → 409.</summary>
    public const string ConcurrentUpdate = "commerce.concurrent_update";

    /// <summary>Belge toplamı saklanabilir üst sınırı aşıyor → 400 (plan sessiz; karar: kayıt reddedilir).</summary>
    public const string TotalTooLarge = "commerce.total_too_large";

    public const string ProductCodeTaken = "product.code_taken";

    public const string QuoteNotEditable = "quote.not_editable";
    public const string QuoteInvalidTransition = "quote.invalid_transition";
    public const string QuoteExpired = "quote.expired";
    public const string QuoteNoLines = "quote.no_lines";
    public const string QuoteNotAccepted = "quote.not_accepted";
    public const string QuoteAlreadyConverted = "quote.already_converted";

    public const string OrderNotEditable = "order.not_editable";
    public const string OrderInvalidTransition = "order.invalid_transition";
    public const string OrderNoLines = "order.no_lines";

    /// <summary>Kayıt sahibi organizasyonun aktif üyesi değil → 400 (Sales ile aynı anahtar).</summary>
    public const string OwnerNotMember = "owner.not_member";

    // Doğrulama mesajı anahtarları (errors sözlüğünde yerelleştirilir).
    public const string ValidUntilPast = "validation.valid_until_past";
    public const string LineProductNotFound = "validation.line_product_not_found";
    public const string LineProductInactive = "validation.line_product_inactive";
    public const string LineProductCurrencyMismatch = "validation.line_product_currency";
    public const string TooManyLines = "validation.too_many_lines";
    public const string Decimals = "validation.decimals";
    public const string CurrencyInvalid = "validation.currency";
}

/// <summary>Domain sınırları (uzunluklar, aralıklar). Ayar değil, veri modeli kısıtı.</summary>
public static class CommerceLimits
{
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 64;
    public const int ProductDescriptionMaxLength = 2000;
    public const int UnitMaxLength = 32;
    public const int SubjectMaxLength = 200;
    public const int TermsMaxLength = 4000;
    public const int NotesMaxLength = 2000;
    public const int LineDescriptionMaxLength = 500;
    public const int ReasonMaxLength = 1000;
    public const int NumberMaxLength = 32;
    public const int EnumColumnMaxLength = 20;
    public const int CurrencyLength = 3;

    public const int MaxLines = 100;
    public const decimal MaxQuantity = 1_000_000m;
    public const decimal MaxUnitPrice = 1_000_000_000m;
    public const decimal MaxPercent = 100m;
    public const int QuantityScale = 4;
    public const int UnitPriceScale = 4;
    public const int PercentScale = 2;

    /// <summary>Belge toplamları <c>decimal(18,2)</c> saklanır; bunu aşan belge reddedilir.</summary>
    public const decimal MaxDocumentTotal = 9_999_999_999_999_999.99m;

    public const int AmountPrecision = 18;
    public const int AmountScale = 2;
    public const int PercentPrecision = 5;
}
