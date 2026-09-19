namespace Crm.Modules.Commerce.Domain;

/// <summary>Bir kalemin hesaplanan tutarları (hepsi 2 ondalığa yuvarlanmış).</summary>
public readonly record struct LineAmounts(decimal LineSubtotal, decimal LineDiscount, decimal LineTax, decimal LineTotal);

/// <summary>Belge toplamları: yuvarlanmış kalem değerlerinin toplamı. <c>GrandTotal = Σ LineTotal</c> değişmezdir.</summary>
public readonly record struct DocumentAmounts(decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal GrandTotal)
{
    public static DocumentAmounts Zero => default;
}

/// <summary>
/// Belge toplam hesabı (saf, sunucu belirleyici; docs/plan/m6a-ticaret.md). <see cref="MidpointRounding.AwayFromZero"/>, 2 ondalık,
/// <b>kalem bazında</b> yuvarlanır; belge toplamları yuvarlanmış kalem değerlerinin toplamıdır (ekranda satırlar toplama tam uyar).
/// <code>
/// lineSubtotal = Round(quantity × unitPrice)      lineDiscount = Round(lineSubtotal × discountPercent / 100)
/// net = lineSubtotal − lineDiscount               lineTax = Round(net × taxRate / 100)        lineTotal = net + lineTax
/// </code>
/// </summary>
public static class DocumentTotals
{
    private const int RoundingDigits = 2;
    private const decimal PercentBase = 100m;

    public static LineAmounts CalculateLine(decimal quantity, decimal unitPrice, decimal discountPercent, decimal taxRate)
    {
        var subtotal = Round(quantity * unitPrice);
        var discount = Round(subtotal * discountPercent / PercentBase);
        var net = subtotal - discount;
        var tax = Round(net * taxRate / PercentBase);
        return new LineAmounts(subtotal, discount, tax, net + tax);
    }

    public static DocumentAmounts Sum(IEnumerable<LineAmounts> lines)
    {
        decimal subtotal = 0m, discount = 0m, tax = 0m, total = 0m;
        foreach (var line in lines)
        {
            subtotal += line.LineSubtotal;
            discount += line.LineDiscount;
            tax += line.LineTax;
            total += line.LineTotal;
        }

        return new DocumentAmounts(subtotal, discount, tax, total);
    }

    public static decimal Round(decimal value) => decimal.Round(value, RoundingDigits, MidpointRounding.AwayFromZero);
}
