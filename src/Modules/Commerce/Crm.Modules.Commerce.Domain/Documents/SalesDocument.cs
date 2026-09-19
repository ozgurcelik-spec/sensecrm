using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Commerce.Domain.Documents;

/// <summary>Kalem girdisi (istek): hesaplanan alanlar yoktur, sunucu <see cref="DocumentTotals"/> ile belirler.</summary>
public sealed record LineInput(
    Guid? ProductId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal TaxRate);

/// <summary>Belge başlığı girdisi (teklif ve sipariş ortak alanları).</summary>
public sealed record DocumentHeader(
    string Subject,
    Guid AccountId,
    Guid? ContactId,
    Guid? DealId,
    Guid OwnerUserId,
    string? Currency,
    string? Terms,
    string? Notes);

/// <summary>Belge kalemi (teklif ve sipariş için ortak alanlar; ayrı tablolardır). Kalem başlığın çocuğudur, denetim kaydı yoktur.</summary>
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
/// Teklif ve satış siparişinin ortak başlığı: numara, konu, bağlı kayıtlar, sahip, para birimi, şartlar/notlar ve sunucu tarafından
/// hesaplanan toplamlar. <see cref="Version"/> Npgsql <c>xmin</c> eşzamanlılık belirtecidir (çakışma → 409).
/// Kişisel veri alanı yoktur (denetim kaydında maskelenecek alan yok).
/// </summary>
public abstract class SalesDocument : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    public const string DefaultCurrency = "TRY";

    protected SalesDocument()
    {
    }

    protected SalesDocument(Guid id, Guid tenantId, string number) : base(id, tenantId)
    {
        Number = Guard.MaxLength(Guard.NotEmpty(number), CommerceLimits.NumberMaxLength);
    }

    /// <summary>Kiracı + yıl bazında ardışık numara (<c>Q-2026-0001</c> / <c>SO-2026-0001</c>); değişmez.</summary>
    public string Number { get; protected set; } = string.Empty;

    public string Subject { get; protected set; } = string.Empty;

    public Guid AccountId { get; protected set; }

    public Guid? ContactId { get; protected set; }

    public Guid? DealId { get; protected set; }

    public Guid OwnerUserId { get; protected set; }

    public string Currency { get; protected set; } = DefaultCurrency;

    public string? Terms { get; protected set; }

    public string? Notes { get; protected set; }

    /// <summary>Brüt (iskonto öncesi, KDV hariç) toplam.</summary>
    public decimal Subtotal { get; protected set; }

    public decimal DiscountTotal { get; protected set; }

    public decimal TaxTotal { get; protected set; }

    public decimal GrandTotal { get; protected set; }

    /// <summary>PostgreSQL <c>xmin</c> sistem kolonu (eşzamanlılık belirteci); EF doldurur.</summary>
    public uint Version { get; protected set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    protected void ApplyHeader(DocumentHeader header)
    {
        Subject = Guard.MaxLength(Guard.NotEmpty(header.Subject), CommerceLimits.SubjectMaxLength);
        AccountId = Guard.NotDefault(header.AccountId);
        ContactId = header.ContactId is { } contact ? Guard.NotDefault(contact) : null;
        DealId = header.DealId is { } deal ? Guard.NotDefault(deal) : null;
        OwnerUserId = Guard.NotDefault(header.OwnerUserId);
        var currency = string.IsNullOrWhiteSpace(header.Currency) ? DefaultCurrency : header.Currency.Trim().ToUpperInvariant();
        Guard.Against(currency.Length != CommerceLimits.CurrencyLength, nameof(header.Currency));
        Currency = currency;
        Terms = Clean(header.Terms, CommerceLimits.TermsMaxLength);
        Notes = Clean(header.Notes, CommerceLimits.NotesMaxLength);
    }

    /// <summary>
    /// Kalemleri hesaplar (<see cref="DocumentTotals"/>) ve toplamları yazar. En çok <see cref="CommerceLimits.MaxLines"/> kalem;
    /// toplam saklanabilir sınırı aşarsa <c>commerce.total_too_large</c> (değişiklik yapılmaz).
    /// </summary>
    protected Result<IReadOnlyList<(LineInput Input, LineAmounts Amounts)>> CalculateLines(IReadOnlyList<LineInput> inputs)
    {
        Guard.Against(inputs.Count > CommerceLimits.MaxLines, CommerceErrors.TooManyLines);
        var calculated = inputs.Select(i => (Input: i, Amounts: DocumentTotals.CalculateLine(i.Quantity, i.UnitPrice, i.DiscountPercent, i.TaxRate))).ToList();
        var totals = DocumentTotals.Sum(calculated.Select(c => c.Amounts));
        if (!Fits(totals))
        {
            return Error.Validation(CommerceErrors.TotalTooLarge);
        }

        Subtotal = totals.Subtotal;
        DiscountTotal = totals.DiscountTotal;
        TaxTotal = totals.TaxTotal;
        GrandTotal = totals.GrandTotal;
        return calculated;
    }

    /// <summary>
    /// Kalemlerin toplamı saklanabilir sınırı aşıyor mu (yazmadan önce, ör. numara ayırmadan önce denetlemek için).
    /// Aşarsa <c>commerce.total_too_large</c> (400).
    /// </summary>
    public static Result EnsureTotalsFit(IReadOnlyList<LineInput> inputs) =>
        Fits(DocumentTotals.Sum(inputs.Select(i => DocumentTotals.CalculateLine(i.Quantity, i.UnitPrice, i.DiscountPercent, i.TaxRate))))
            ? Result.Success()
            : Error.Validation(CommerceErrors.TotalTooLarge);

    private static bool Fits(DocumentAmounts totals) =>
        totals.Subtotal <= CommerceLimits.MaxDocumentTotal && totals.GrandTotal <= CommerceLimits.MaxDocumentTotal;

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
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
