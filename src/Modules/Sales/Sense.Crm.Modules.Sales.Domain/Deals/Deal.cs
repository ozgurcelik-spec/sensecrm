using Sense.Crm.Modules.Sales.Domain.Pipelines;
using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Sales.Domain.Deals;

/// <summary>
/// Fırsat aşaması değişti (M4 workflow tetikleyicisi). <see cref="ToKind"/> kazanma/kaybetme geçişlerini ayırt eder.
/// Aynı transaction'da outbox'a yazılır.
/// </summary>
public sealed record DealStageChanged(
    Guid DealId,
    Guid PipelineId,
    Guid? FromStageId,
    Guid ToStageId,
    StageKind ToKind,
    decimal? Amount,
    string Currency) : DomainEvent;

/// <summary>
/// Fırsat (Zoho "Deal"): bir firmaya (ve isteğe bağlı kişiye) bağlı, bir hunide bir aşamada duran satış. Olasılık aşamadan
/// gelir (saklanmaz). Kazanılan/kaybedilen aşamaya geçince <see cref="ClosedAt"/> yazılır; kaybedilende
/// <see cref="LostReason"/> zorunludur. Aşama değişimi yalnız <see cref="MoveToStage"/> ile yapılır.
/// </summary>
public sealed class Deal : TenantAggregateRoot<Guid>, IAuditLogged, ISoftDelete
{
    public const string DefaultCurrency = "TRY";

    private Deal()
    {
    }

    private Deal(Guid id, Guid tenantId, string name, Guid accountId, Guid pipelineId, Guid ownerUserId) : base(id, tenantId)
    {
        Name = name;
        AccountId = accountId;
        PipelineId = pipelineId;
        OwnerUserId = ownerUserId;
    }

    public string Name { get; private set; } = string.Empty;

    public Guid AccountId { get; private set; }

    public Guid? ContactId { get; private set; }

    public Guid PipelineId { get; private set; }

    public Guid StageId { get; private set; }

    public decimal? Amount { get; private set; }

    public string Currency { get; private set; } = DefaultCurrency;

    public DateOnly? ClosingDate { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public string? LostReason { get; private set; }

    /// <summary>Kazanma/kaybetme anı; açık aşamada null.</summary>
    public DateTime? ClosedAt { get; private set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    /// <summary>
    /// Yeni fırsat: <paramref name="stage"/> verilmezse huninin ilk açık aşamasında başlar. Aşama bu hunide olmalıdır
    /// (<c>pipeline.stage_not_found</c>); kaybedilen aşamada başlarsa <paramref name="lostReason"/> zorunludur.
    /// </summary>
    public static Result<Deal> Create(
        Guid tenantId,
        string name,
        Guid accountId,
        Guid ownerUserId,
        Pipeline pipeline,
        PipelineStage? stage,
        Guid? contactId,
        decimal? amount,
        string? currency,
        DateOnly? closingDate,
        string? lostReason,
        DateTime nowUtc)
    {
        var start = stage ?? pipeline.FirstOpenStage;
        if (start is null || start.PipelineId != pipeline.Id)
        {
            return Error.NotFound(SalesErrors.StageNotFound);
        }

        var deal = new Deal(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(name), SalesLimits.NameMaxLength),
            Guard.NotDefault(accountId),
            pipeline.Id,
            Guard.NotDefault(ownerUserId));
        deal.Apply(contactId, amount, currency, closingDate);

        var placed = deal.Place(start, lostReason, nowUtc);
        return placed.IsFailure ? placed.Error : deal;
    }

    /// <summary>Aşama dışındaki alanları günceller. Kayıp nedeni yalnız zaten kaybedilmiş fırsatta değiştirilir.</summary>
    public void Update(string name, Guid accountId, Guid ownerUserId, Guid? contactId, decimal? amount, string? currency, DateOnly? closingDate, string? lostReason)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(name), SalesLimits.NameMaxLength);
        AccountId = Guard.NotDefault(accountId);
        OwnerUserId = Guard.NotDefault(ownerUserId);
        Apply(contactId, amount, currency, closingDate);

        if (LostReason is not null && Text.Clean(lostReason, SalesLimits.LostReasonMaxLength) is { } reason)
        {
            LostReason = reason;
        }
    }

    /// <summary>
    /// Aşama değiştirir. Aşama bu fırsatın hunisinde olmalı (<c>pipeline.stage_not_found</c>); kaybedilen aşamada neden
    /// zorunlu (<c>deal.lost_reason_required</c>). Kazanma/kaybetmede <see cref="ClosedAt"/> yazılır, açık aşamaya dönünce
    /// silinir. Aşama gerçekten değiştiyse <see cref="DealStageChanged"/> yükseltilir.
    /// </summary>
    public Result MoveToStage(PipelineStage stage, string? lostReason, DateTime nowUtc)
    {
        Guard.NotNull(stage);
        if (stage.PipelineId != PipelineId)
        {
            return Error.NotFound(SalesErrors.StageNotFound);
        }

        var previous = StageId;
        var placed = Place(stage, lostReason, nowUtc);
        if (placed.IsFailure)
        {
            return placed;
        }

        if (previous != stage.Id)
        {
            Raise(new DealStageChanged(Id, PipelineId, previous == Guid.Empty ? null : previous, stage.Id, stage.Kind, Amount, Currency));
        }

        return Result.Success();
    }

    private Result Place(PipelineStage stage, string? lostReason, DateTime nowUtc)
    {
        var reason = Text.Clean(lostReason, SalesLimits.LostReasonMaxLength);
        if (stage.Kind == StageKind.Lost && reason is null)
        {
            return Error.Validation(SalesErrors.LostReasonRequired);
        }

        var changed = StageId != stage.Id;
        StageId = stage.Id;
        LostReason = stage.Kind == StageKind.Lost ? reason : null;
        ClosedAt = stage.Kind == StageKind.Open ? null : changed || ClosedAt is null ? nowUtc : ClosedAt;
        return Result.Success();
    }

    private void Apply(Guid? contactId, decimal? amount, string? currency, DateOnly? closingDate)
    {
        ContactId = contactId;
        Amount = amount is null ? null : decimal.Round(Guard.NotNegative(amount.Value), Money4);
        Currency = string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : Guard.NotEmpty(currency).ToUpperInvariant();
        ClosingDate = closingDate;
    }

    private const int Money4 = 4;
}
