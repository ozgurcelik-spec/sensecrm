namespace Sense.Crm.Modules.Marketing.Domain.Metrics;

/// <summary>Bir kampanyanın (veya kampanya kümesinin) üye sayımları: tür ve durum kırılımı.</summary>
public readonly record struct MemberCounts(
    int LeadCount,
    int ContactCount,
    int Added,
    int Sent,
    int Responded,
    int Converted,
    int Unsubscribed)
{
    public int MemberCount => LeadCount + ContactCount;

    /// <summary>Ulaşılan: durumu <c>added</c> dışında olan üyeler.</summary>
    public int ContactedCount => Sent + Responded + Converted + Unsubscribed;

    /// <summary>Yanıt: <c>responded + converted</c>.</summary>
    public int ResponseCount => Responded + Converted;

    public static MemberCounts operator +(MemberCounts a, MemberCounts b) => new(
        a.LeadCount + b.LeadCount,
        a.ContactCount + b.ContactCount,
        a.Added + b.Added,
        a.Sent + b.Sent,
        a.Responded + b.Responded,
        a.Converted + b.Converted,
        a.Unsubscribed + b.Unsubscribed);
}

/// <summary>
/// Metrik formülleri (bağlayıcı tanımlar, docs/plan/m6c-pazarlama.md; saf hesap). Oranlar yüzde olarak 2 ondalığa yarıdan yukarı
/// yuvarlanır; payda 0 ise 0. Pay her zaman paydanın alt kümesidir (yanıt ⊆ ulaşılan, dönüşen ⊆ lead) → oran 0–100.
/// </summary>
public static class MarketingMetrics
{
    public const int Decimals = 2;

    /// <summary><c>responseCount / contactedCount × 100</c>.</summary>
    public static decimal ResponseRate(MemberCounts counts) => Percent(counts.ResponseCount, counts.ContactedCount);

    /// <summary><c>convertedCount / leadCount × 100</c> (yalnız lead'ler dönüşebilir).</summary>
    public static decimal ConversionRate(MemberCounts counts) => Percent(counts.Converted, counts.LeadCount);

    /// <summary><c>actualCost / leadCount</c>; maliyet boşsa veya lead yoksa null (alan yanıtta yazılmaz).</summary>
    public static decimal? CostPerLead(decimal? actualCost, int leadCount) =>
        actualCost is { } cost && leadCount > 0 ? Math.Round(cost / leadCount, Decimals, MidpointRounding.AwayFromZero) : null;

    public static decimal Percent(int numerator, int denominator) =>
        denominator <= 0 ? 0m : Math.Round((decimal)numerator * 100m / denominator, Decimals, MidpointRounding.AwayFromZero);
}
