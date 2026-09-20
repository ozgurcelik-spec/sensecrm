using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Commerce.Domain.Documents;

/// <summary>
/// Kalem girdisi: <b>çözülmüş</b> değerler (birim fiyat dahil). Hesaplanan alanlar yoktur, sunucu <see cref="DocumentTotals"/> ile belirler.
/// İstekteki isteğe bağlı birim fiyat, yazma anında bir kez çözülüp buraya taşınır (bkz. m9c-envanter.md "Kalem fiyatı çözümü").
/// </summary>
public sealed record LineInput(
    Guid? ProductId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal TaxRate);

/// <summary>
/// Satış belgesi (teklif, sipariş, fatura) başlığı girdisi. M9C alanları (<see cref="Carrier"/>, <see cref="Adjustment"/>, adresler,
/// <see cref="PriceBookId"/>) sona eklenen isteğe bağlı parametrelerdir; verilmezse tam değiştirme kuralıyla temizlenir / 0 olur.
/// </summary>
public sealed record DocumentHeader(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    Guid OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier = null,
    decimal Adjustment = 0m,
    DocumentAddress? BillingAddress = null,
    DocumentAddress? ShippingAddress = null,
    Guid? PriceBookId = null);

/// <summary>Satın alma emri başlığı girdisi (firma/fırsat/fiyat listesi yok; tedarikçi zorunlu).</summary>
public sealed record PurchaseOrderHeader(
    string Subject,
    Guid VendorId,
    Guid? ContactId,
    Guid OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes,
    string? Carrier = null,
    decimal Adjustment = 0m,
    DocumentAddress? BillingAddress = null,
    DocumentAddress? ShippingAddress = null);

/// <summary>Belge kalemi (teklif, sipariş, fatura, satın alma emri için ortak alanlar; ayrı tablolardır). Kalem başlığın çocuğudur, denetim kaydı yoktur.</summary>
public abstract class DocumentLine : TenantEntity<Guid>
{
    protected DocumentLine()
    {
    }

    protected DocumentLine(Guid tenantId, int position, LineInput input, LineAmounts amounts) : base(Guid.CreateVersion7(), tenantId)
    {
        Position = position;
        ProductId = input.ProductId;
        Description = Guard.MaxLength(Guard.NotEmpty(input.Description), CommerceLimits.LineDescriptionMaxLength);
        Quantity = input.Quantity;
        UnitPrice = input.UnitPrice;
        DiscountPercent = input.DiscountPercent;
        TaxRate = input.TaxRate;
        LineSubtotal = amounts.LineSubtotal;
        LineDiscount = amounts.LineDiscount;
        LineTax = amounts.LineTax;
        LineTotal = amounts.LineTotal;
    }

    public int Position { get; protected set; }

    /// <summary>Yumuşak bağ: ürün kaydı sonradan silinse/değişse kalem etkilenmez.</summary>
    public Guid? ProductId { get; protected set; }

    public string Description { get; protected set; } = string.Empty;

    public decimal Quantity { get; protected set; }

    public decimal UnitPrice { get; protected set; }

    public decimal DiscountPercent { get; protected set; }

    public decimal TaxRate { get; protected set; }

    public decimal LineSubtotal { get; protected set; }

    public decimal LineDiscount { get; protected set; }

    public decimal LineTax { get; protected set; }

    public decimal LineTotal { get; protected set; }

    public LineInput ToInput() => new(ProductId, Description, Quantity, UnitPrice, DiscountPercent, TaxRate);

    public LineAmounts Amounts => new(LineSubtotal, LineDiscount, LineTax, LineTotal);
}

/// <summary>
/// Ticaret belgelerinin (teklif, sipariş, fatura, satın alma emri) ortak tabanı (m9c-envanter.md D2): numara, konu, sahip, kişi, para birimi,
/// şartlar/notlar, nakliye etiketi, iki adres bloğu, yuvarlama ve sunucu tarafından hesaplanan toplamlar.
/// <see cref="Version"/> Npgsql <c>xmin</c> eşzamanlılık belirtecidir (çakışma → 409).
/// Kişisel veri alanı yoktur (denetim kaydında maskelenecek alan yok).
/// </summary>
public abstract class CommerceDocument : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    public const string DefaultCurrency = "TRY";

    protected CommerceDocument()
    {
    }

    protected CommerceDocument(Guid id, Guid tenantId, string number) : base(id, tenantId)
    {
        Number = Guard.MaxLength(Guard.NotEmpty(number), CommerceLimits.NumberMaxLength);
    }

    /// <summary>Kiracı + yıl bazında ardışık numara (<c>Q-2026-0001</c> / <c>SO-…</c> / <c>INV-…</c> / <c>PO-…</c>); değişmez.</summary>
    public string Number { get; protected set; } = string.Empty;

    public string Subject { get; protected set; } = string.Empty;

    public Guid? ContactId { get; protected set; }

    public Guid OwnerUserId { get; protected set; }

    public string Currency { get; protected set; } = DefaultCurrency;

    public string? Terms { get; protected set; }

    public string? Notes { get; protected set; }

    /// <summary>Nakliye etiketi (serbest metin; kargo bedeli yoktur, D4).</summary>
    public string? Carrier { get; protected set; }

    public string? BillingStreet { get; protected set; }

    public string? BillingBuilding { get; protected set; }

    public string? BillingCity { get; protected set; }

    public string? BillingState { get; protected set; }

    public string? BillingPostalCode { get; protected set; }

    public string? BillingCountry { get; protected set; }

    public string? ShippingStreet { get; protected set; }

    public string? ShippingBuilding { get; protected set; }

    public string? ShippingCity { get; protected set; }

    public string? ShippingState { get; protected set; }

    public string? ShippingPostalCode { get; protected set; }

    public string? ShippingCountry { get; protected set; }

    public DocumentAddress? BillingAddress =>
        DocumentAddress.Normalize(new DocumentAddress(BillingStreet, BillingBuilding, BillingCity, BillingState, BillingPostalCode, BillingCountry));

    public DocumentAddress? ShippingAddress =>
        DocumentAddress.Normalize(new DocumentAddress(ShippingStreet, ShippingBuilding, ShippingCity, ShippingState, ShippingPostalCode, ShippingCountry));

    /// <summary>Brüt (iskonto öncesi, KDV hariç) toplam.</summary>
    public decimal Subtotal { get; protected set; }

    public decimal DiscountTotal { get; protected set; }

    public decimal TaxTotal { get; protected set; }

    /// <summary>Belge düzeyi yuvarlama (işaretli, KDV sonrası toplama eklenir; D3).</summary>
    public decimal Adjustment { get; protected set; }

    /// <summary><c>Σ lineTotal + adjustment</c>.</summary>
    public decimal GrandTotal { get; protected set; }

    /// <summary>PostgreSQL <c>xmin</c> sistem kolonu (eşzamanlılık belirteci); EF doldurur.</summary>
    public uint Version { get; protected set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    /// <summary>Ortak başlık alanlarını yazar (konu, kişi, sahip, para birimi, şartlar, notlar, nakliye, adresler).</summary>
    protected void ApplyCommon(
        string subject,
        Guid? contactId,
        Guid ownerUserId,
        string? currency,
        string? terms,
        string? notes,
        string? carrier,
        DocumentAddress? billing,
        DocumentAddress? shipping)
    {
        Subject = Guard.MaxLength(Guard.NotEmpty(subject), CommerceLimits.SubjectMaxLength);
        ContactId = contactId is { } contact ? Guard.NotDefault(contact) : null;
        OwnerUserId = Guard.NotDefault(ownerUserId);
        var cur = string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : currency.Trim().ToUpperInvariant();
        Guard.Against(cur.Length != CommerceLimits.CurrencyLength, nameof(currency));
        Currency = cur;
        Terms = Clean(terms, CommerceLimits.TermsMaxLength);
        Notes = Clean(notes, CommerceLimits.NotesMaxLength);
        Carrier = Clean(carrier, CommerceLimits.CarrierMaxLength);

        var b = DocumentAddress.Normalize(billing);
        BillingStreet = b?.Street;
        BillingBuilding = b?.Building;
        BillingCity = b?.City;
        BillingState = b?.State;
        BillingPostalCode = b?.PostalCode;
        BillingCountry = b?.Country;

        var s = DocumentAddress.Normalize(shipping);
        ShippingStreet = s?.Street;
        ShippingBuilding = s?.Building;
        ShippingCity = s?.City;
        ShippingState = s?.State;
        ShippingPostalCode = s?.PostalCode;
        ShippingCountry = s?.Country;
    }

    /// <summary>
    /// Kalemleri hesaplar (<see cref="DocumentTotals"/>) ve toplamları yazar. En çok <see cref="CommerceLimits.MaxLines"/> kalem.
    /// Kurallar (değişiklik yapılmaz): kalemsiz belgede yuvarlama ≠ 0 olamaz (<c>validation.adjustment_requires_lines</c>), toplam negatif olamaz
    /// (<c>validation.adjustment_negative_total</c>), toplam saklanabilir sınırı aşamaz (<c>commerce.total_too_large</c>).
    /// </summary>
    protected Result<IReadOnlyList<(LineInput Input, LineAmounts Amounts)>> CalculateLines(IReadOnlyList<LineInput> inputs, decimal adjustment)
    {
        Guard.Against(inputs.Count > CommerceLimits.MaxLines, CommerceErrors.TooManyLines);
        var evaluated = Evaluate(inputs, adjustment);
        if (evaluated.IsFailure)
        {
            return evaluated.Error;
        }

        var totals = evaluated.Value.Totals;
        Subtotal = totals.Subtotal;
        DiscountTotal = totals.DiscountTotal;
        TaxTotal = totals.TaxTotal;
        Adjustment = totals.Adjustment;
        GrandTotal = totals.GrandTotal;
        return Result.Success(evaluated.Value.Lines);
    }

    /// <summary>
    /// Kalemlerin ve yuvarlamanın toplamı geçerli mi (yazmadan önce, ör. numara ayırmadan önce denetlemek için): kalemsiz belgede yuvarlama ≠ 0,
    /// negatif toplam veya saklanabilir sınırı aşan toplam reddedilir.
    /// </summary>
    public static Result EnsureTotalsFit(IReadOnlyList<LineInput> inputs, decimal adjustment = 0m)
    {
        var evaluated = Evaluate(inputs, adjustment);
        return evaluated.IsFailure ? evaluated.Error : Result.Success();
    }

    private static Result<(IReadOnlyList<(LineInput Input, LineAmounts Amounts)> Lines, DocumentAmounts Totals)> Evaluate(IReadOnlyList<LineInput> inputs, decimal adjustment)
    {
        Guard.Against(decimal.Round(adjustment, CommerceLimits.AmountScale) != adjustment || Math.Abs(adjustment) > CommerceLimits.MaxAdjustment, nameof(adjustment));
        if (inputs.Count == 0 && adjustment != 0m)
        {
            return Error.Validation(CommerceErrors.AdjustmentRequiresLines);
        }

        IReadOnlyList<(LineInput Input, LineAmounts Amounts)> calculated =
            inputs.Select(i => (Input: i, Amounts: DocumentTotals.CalculateLine(i.Quantity, i.UnitPrice, i.DiscountPercent, i.TaxRate))).ToList();
        var totals = DocumentTotals.Sum(calculated.Select(c => c.Amounts), adjustment);
        if (totals.GrandTotal < 0m)
        {
            return Error.Validation(CommerceErrors.AdjustmentNegativeTotal);
        }

        if (totals.Subtotal > CommerceLimits.MaxDocumentTotal || totals.GrandTotal > CommerceLimits.MaxDocumentTotal)
        {
            return Error.Validation(CommerceErrors.TotalTooLarge);
        }

        return (calculated, totals);
    }

    protected static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
    }
}

/// <summary>Firmaya bağlı satış belgeleri (teklif, sipariş, fatura) ortak tabanı: firma, fırsat, fiyat listesi (yumuşak bağ).</summary>
public abstract class SalesDocument : CommerceDocument
{
    protected SalesDocument()
    {
    }

    protected SalesDocument(Guid id, Guid tenantId, string number) : base(id, tenantId, number)
    {
    }

    public Guid AccountId { get; protected set; }

    public Guid? DealId { get; protected set; }

    /// <summary>Fiyat listesi (yumuşak bağ): liste sonradan silinse belge etkilenmez.</summary>
    public Guid? PriceBookId { get; protected set; }

    protected void ApplyHeader(DocumentHeader header)
    {
        ApplyCommon(header.Subject, header.ContactId, header.OwnerUserId, header.Currency, header.Terms, header.Notes, header.Carrier, header.BillingAddress, header.ShippingAddress);
        AccountId = Guard.NotDefault(header.AccountId);
        DealId = header.DealId is { } deal ? Guard.NotDefault(deal) : null;
        PriceBookId = header.PriceBookId is { } book ? Guard.NotDefault(book) : null;
    }
}

/// <summary>Enum değerlerinin tel/hata argümanı biçimi (camelCase).</summary>
public static class CommerceNames
{
    public static string Camel(Enum value)
    {
        var name = value.ToString();
        return string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1));
    }
}
