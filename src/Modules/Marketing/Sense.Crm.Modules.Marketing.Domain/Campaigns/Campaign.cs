using System.Text.RegularExpressions;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Marketing.Domain.Campaigns;

public enum CampaignType
{
    Email,
    Event,
    Webinar,
    Advertising,
    Other,
}

public enum CampaignStatus
{
    Planned,
    Active,
    Completed,
    Cancelled,
}

/// <summary>
/// Kampanya (Zoho "Campaign"): tür, tarih aralığı, bütçe ve maliyet bilgisi olan pazarlama çalışması. Üyeler
/// (<c>CampaignMember</c>) ayrı agregattır. Kurallar: <c>endDate &gt;= startDate</c>; durum yalnız geçiş tablosuyla
/// (<see cref="CanTransition"/>) değişir; tutarlar ≥ 0 ve tek para birimindedir; <c>completed</c>/<c>cancelled</c> kampanyaya üye eklenmez.
/// Kişisel veri alanı yoktur (denetim kaydında maskelenecek alan yok).
/// </summary>
public sealed partial class Campaign : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    private Campaign()
    {
    }

    private Campaign(Guid id, Guid tenantId, string name, CampaignType type, Guid ownerUserId) : base(id, tenantId)
    {
        Name = name;
        Type = type;
        OwnerUserId = ownerUserId;
    }

    public string Name { get; private set; } = string.Empty;

    public CampaignType Type { get; private set; }

    public CampaignStatus Status { get; private set; }

    public DateOnly? StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    /// <summary><see cref="Budget"/>, <see cref="ExpectedRevenue"/> ve <see cref="ActualCost"/> için tek ISO-4217 para birimi.</summary>
    public string Currency { get; private set; } = MarketingLimits.DefaultCurrency;

    public decimal? Budget { get; private set; }

    public decimal? ExpectedRevenue { get; private set; }

    public decimal? ActualCost { get; private set; }

    public string? Description { get; private set; }

    public Guid OwnerUserId { get; private set; }

    /// <summary>Yeni üye eklenebilir mi (<c>planned</c>/<c>active</c>); kapalı kampanyada durum güncelleme/çıkarma yine çalışır.</summary>
    public bool AcceptsNewMembers => Status is CampaignStatus.Planned or CampaignStatus.Active;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    /// <summary>
    /// Yeni kampanya. <paramref name="status"/> verilmezse <c>planned</c>; yalnız <c>planned</c>/<c>active</c> olabilir
    /// (<c>validation.campaign_create_status</c>). <c>endDate &lt; startDate</c> → <c>campaign.invalid_date_range</c>;
    /// negatif tutar veya geçersiz para birimi → <c>validation</c>.
    /// </summary>
    public static Result<Campaign> Create(
        Guid tenantId,
        string name,
        CampaignType type,
        CampaignStatus? status,
        DateOnly? startDate,
        DateOnly? endDate,
        string? currency,
        decimal? budget,
        decimal? expectedRevenue,
        decimal? actualCost,
        string? description,
        Guid ownerUserId)
    {
        var initial = status ?? CampaignStatus.Planned;
        if (initial is not (CampaignStatus.Planned or CampaignStatus.Active))
        {
            return Error.Validation(MarketingErrors.InvalidCreateStatus);
        }

        var campaign = new Campaign(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(name), MarketingLimits.NameMaxLength),
            type,
            Guard.NotDefault(ownerUserId))
        {
            Status = initial,
        };

        var applied = campaign.Apply(startDate, endDate, currency, budget, expectedRevenue, actualCost, description);
        return applied.IsFailure ? applied.Error : campaign;
    }

    /// <summary>
    /// Tam değiştirme (PUT; <c>status</c> hariç). Gönderilmeyen isteğe bağlı alan temizlenir; <paramref name="currency"/>
    /// verilmezse <c>TRY</c>. Her durumda çalışır (tamamlanan kampanyanın gerçekleşen maliyeti sonradan girilir).
    /// </summary>
    public Result Update(
        string name,
        CampaignType type,
        DateOnly? startDate,
        DateOnly? endDate,
        string? currency,
        decimal? budget,
        decimal? expectedRevenue,
        decimal? actualCost,
        string? description,
        Guid ownerUserId)
    {
        var cleanName = Guard.MaxLength(Guard.NotEmpty(name), MarketingLimits.NameMaxLength);
        var owner = Guard.NotDefault(ownerUserId);

        var applied = Apply(startDate, endDate, currency, budget, expectedRevenue, actualCost, description);
        if (applied.IsFailure)
        {
            return applied;
        }

        Name = cleanName;
        Type = type;
        OwnerUserId = owner;
        return Result.Success();
    }

    /// <summary>
    /// Durum geçişi (bağlayıcı tablo: <see cref="CanTransition"/>). Aynı duruma geçiş idempotent no-op'tur; tabloda olmayan geçiş
    /// <c>campaign.invalid_status_transition</c> (409; <c>from</c>/<c>to</c> argümanlarıyla).
    /// </summary>
    public Result ChangeStatus(CampaignStatus target)
    {
        if (target == Status)
        {
            return Result.Success();
        }

        if (!CanTransition(Status, target))
        {
            return Error.Conflict(
                MarketingErrors.InvalidStatusTransition,
                ("from", CamelCase(Status)),
                ("to", CamelCase(target)));
        }

        Status = target;
        return Result.Success();
    }

    /// <summary>Durum geçiş tablosu: planned→active|cancelled, active→completed|cancelled, completed→active, cancelled→planned.</summary>
    public static bool CanTransition(CampaignStatus from, CampaignStatus to) => (from, to) switch
    {
        (CampaignStatus.Planned, CampaignStatus.Active) => true,
        (CampaignStatus.Planned, CampaignStatus.Cancelled) => true,
        (CampaignStatus.Active, CampaignStatus.Completed) => true,
        (CampaignStatus.Active, CampaignStatus.Cancelled) => true,
        (CampaignStatus.Completed, CampaignStatus.Active) => true,
        (CampaignStatus.Cancelled, CampaignStatus.Planned) => true,
        _ => false,
    };

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    private Result Apply(
        DateOnly? startDate,
        DateOnly? endDate,
        string? currency,
        decimal? budget,
        decimal? expectedRevenue,
        decimal? actualCost,
        string? description)
    {
        if (startDate is { } start && endDate is { } end && end < start)
        {
            return Error.Validation(MarketingErrors.InvalidDateRange);
        }

        var code = string.IsNullOrWhiteSpace(currency) ? MarketingLimits.DefaultCurrency : currency.Trim().ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(code))
        {
            return Error.Validation(MarketingErrors.InvalidCurrency);
        }

        if (!IsValidAmount(budget) || !IsValidAmount(expectedRevenue) || !IsValidAmount(actualCost))
        {
            return Error.Validation(MarketingErrors.InvalidAmount);
        }

        StartDate = startDate;
        EndDate = endDate;
        Currency = code;
        Budget = budget;
        ExpectedRevenue = expectedRevenue;
        ActualCost = actualCost;
        Description = Clean(description, MarketingLimits.DescriptionMaxLength);
        return Result.Success();
    }

    private static bool IsValidAmount(decimal? value) => value is null or (>= 0 and <= MarketingLimits.MaxAmount);

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : Guard.MaxLength(trimmed, maxLength);
    }

    private static string CamelCase<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
