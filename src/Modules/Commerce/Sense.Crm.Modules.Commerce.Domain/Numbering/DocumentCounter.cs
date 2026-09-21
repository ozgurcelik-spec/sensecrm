using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Commerce.Domain.Numbering;

/// <summary>Numara sayacı türleri (<c>commerce.document_counters.kind</c>).</summary>
public static class DocumentKinds
{
    public const string Quote = "quote";
    public const string Order = "order";
    public const string Invoice = "invoice";
    public const string PurchaseOrder = "purchaseOrder";
}

/// <summary>
/// Belge numara sayacı satırı: <c>(tenant_id, kind, year)</c> başına son verilen sıra. Satırı yalnız ham SQL yazar
/// (<c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>, belge INSERT'iyle aynı transaction'da; bkz. Infrastructure
/// <c>DocumentNumberAllocator</c>); EF modeli yalnız şema/migration ve kiracı filtresi içindir.
/// </summary>
public sealed class DocumentCounter : ITenantEntity
{
    public Guid TenantId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public int Year { get; init; }

    public long LastValue { get; set; }
}

/// <summary>Numara biçimi: teklif <c>Q-{yıl}-{sıra:D4}</c>, sipariş <c>SO-{yıl}-{sıra:D4}</c> (sıra 9999'u aşarsa doğal uzar).</summary>
public static class DocumentNumberFormat
{
    public const string QuotePrefix = "Q";
    public const string OrderPrefix = "SO";
    public const string InvoicePrefix = "INV";
    public const string PurchasePrefix = "PO";

    public static string PrefixOf(string kind) => kind switch
    {
        DocumentKinds.Quote => QuotePrefix,
        DocumentKinds.Order => OrderPrefix,
        DocumentKinds.Invoice => InvoicePrefix,
        DocumentKinds.PurchaseOrder => PurchasePrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string Format(string kind, int year, long sequence) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{PrefixOf(kind)}-{year}-{sequence:D4}");
}
