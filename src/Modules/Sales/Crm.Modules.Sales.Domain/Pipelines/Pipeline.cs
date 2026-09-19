using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;
using Crm.Shared.Kernel.Results;

namespace Crm.Modules.Sales.Domain.Pipelines;

/// <summary>Aşama türü: açık aşamalar, kazanıldı (won) ve kaybedildi (lost). Bir hunide tam bir won + bir lost aşama bulunur.</summary>
public enum StageKind
{
    Open,
    Won,
    Lost,
}

/// <summary>Aşama tanımı girdisi. <see cref="Id"/> dolu ise mevcut aşama güncellenir, boşsa yeni aşama açılır.</summary>
public sealed record StageDefinition(Guid? Id, string Name, int Probability, StageKind Kind);

/// <summary>
/// Satış hunisi (Zoho "Pipeline"): sıralı aşamalar. Organizasyonda tam bir varsayılan huni bulunur (yeni fırsatlar ve lead
/// dönüştürme buradan başlar). Aşamalar dizideki sıraya göre <see cref="PipelineStage.Order"/> alır.
/// </summary>
public sealed class Pipeline : TenantAggregateRoot<Guid>, IAuditLogged
{
    private readonly List<PipelineStage> _stages = [];

    private Pipeline()
    {
    }

    private Pipeline(Guid id, Guid tenantId, string name, bool isDefault) : base(id, tenantId)
    {
        Name = name;
        IsDefault = isDefault;
    }

    public string Name { get; private set; } = string.Empty;

    public bool IsDefault { get; private set; }

    public IReadOnlyList<PipelineStage> Stages => _stages;

    /// <summary>Aşamalar sıraya göre; fırsatların başlangıç aşaması ilk açık aşamadır.</summary>
    public PipelineStage? FirstOpenStage => _stages.Where(s => s.Kind == StageKind.Open).OrderBy(s => s.Order).FirstOrDefault();

    public static Result<Pipeline> Create(Guid tenantId, string name, bool isDefault, IReadOnlyList<StageDefinition> stages)
    {
        var validation = ValidateStages(stages);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var pipeline = new Pipeline(
            Guid.CreateVersion7(),
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(name), SalesLimits.NameMaxLength),
            isDefault);
        for (var i = 0; i < stages.Count; i++)
        {
            pipeline._stages.Add(PipelineStage.Create(pipeline.TenantId, pipeline.Id, stages[i], i));
        }

        return pipeline;
    }

    public void Rename(string name) => Name = Guard.MaxLength(Guard.NotEmpty(name), SalesLimits.NameMaxLength);

    public void SetDefault(bool isDefault) => IsDefault = isDefault;

    /// <summary>
    /// Aşama kümesini verilen tanıma göre yeniler: dizi sırası <c>Order</c> olur, kimliği olanlar güncellenir, kimliği
    /// olmayanlar eklenir, listede olmayanlar kaldırılır (kaldırılanlar döner; çağıran kalıcı olarak siler). Kullanımdaki
    /// (fırsat içeren) aşama silinemez: <c>pipeline.stage_in_use</c>.
    /// </summary>
    public Result<IReadOnlyList<PipelineStage>> ReplaceStages(IReadOnlyList<StageDefinition> definitions, IReadOnlySet<Guid> usedStageIds)
    {
        var validation = ValidateStages(definitions);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        var byId = _stages.ToDictionary(s => s.Id);
        if (definitions.Any(d => d.Id is { } id && !byId.ContainsKey(id)))
        {
            return Error.NotFound(SalesErrors.StageNotFound);
        }

        var kept = definitions.Where(d => d.Id is not null).Select(d => d.Id!.Value).ToHashSet();
        var removed = _stages.Where(s => !kept.Contains(s.Id)).ToList();
        if (removed.FirstOrDefault(s => usedStageIds.Contains(s.Id)) is { } inUse)
        {
            return Error.Conflict(SalesErrors.StageInUse, (SalesErrors.Args.StageName, inUse.Name));
        }

        foreach (var stage in removed)
        {
            _stages.Remove(stage);
        }

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (definition.Id is { } id)
            {
                byId[id].Update(definition, i);
            }
            else
            {
                _stages.Add(PipelineStage.Create(TenantId, Id, definition, i));
            }
        }

        _stages.Sort((a, b) => a.Order.CompareTo(b.Order));
        return removed;
    }

    /// <summary>Tam bir won + bir lost + en az bir open aşama; adlar/olasılıklar geçerli; kimlikler benzersiz.</summary>
    public static Result ValidateStages(IReadOnlyList<StageDefinition> stages)
    {
        if (stages.Count == 0 || stages.Count > SalesLimits.MaxStagesPerPipeline
            || stages.Count(s => s.Kind == StageKind.Won) != 1
            || stages.Count(s => s.Kind == StageKind.Lost) != 1
            || stages.All(s => s.Kind != StageKind.Open)
            || stages.Any(s => string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > SalesLimits.StageNameMaxLength)
            || stages.Any(s => s.Probability is < 0 or > SalesLimits.MaxProbability)
            || stages.Where(s => s.Id is not null).GroupBy(s => s.Id).Any(g => g.Count() > 1))
        {
            return Error.Validation(SalesErrors.InvalidStageSet);
        }

        return Result.Success();
    }
}

/// <summary>Hunideki bir aşama. Kaldırılınca yumuşak silinir (silinmiş fırsatlar bu aşamaya bağlı kalabilir).</summary>
public sealed class PipelineStage : TenantEntity<Guid>, IAuditLogged, ISoftDelete
{
    private PipelineStage()
    {
    }

    private PipelineStage(Guid id, Guid tenantId, Guid pipelineId) : base(id, tenantId) => PipelineId = pipelineId;

    public Guid PipelineId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int Order { get; private set; }

    /// <summary>Kazanma olasılığı (yüzde, 0-100); bu aşamadaki fırsatların olasılığı buradan gelir.</summary>
    public int Probability { get; private set; }

    public StageKind Kind { get; private set; }

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedUserId { get; set; }

    internal static PipelineStage Create(Guid tenantId, Guid pipelineId, StageDefinition definition, int order)
    {
        var stage = new PipelineStage(Guid.CreateVersion7(), tenantId, pipelineId);
        stage.Update(definition, order);
        return stage;
    }

    internal void Update(StageDefinition definition, int order)
    {
        Name = Guard.MaxLength(Guard.NotEmpty(definition.Name), SalesLimits.StageNameMaxLength);
        Probability = Guard.InRange(definition.Probability, 0, SalesLimits.MaxProbability);
        Kind = definition.Kind;
        Order = order;
    }
}

/// <summary>Yeni organizasyonlara tohumlanan varsayılan huni (dil: <c>tr</c> | <c>en</c>).</summary>
public static class DefaultPipelineTemplate
{
    public static string PipelineName(string locale) => IsEnglish(locale) ? "Sales Pipeline" : "Satış Hunisi";

    public static IReadOnlyList<StageDefinition> Stages(string locale)
    {
        var english = IsEnglish(locale);
        return
        [
            new(null, english ? "Qualification" : "Nitelendirme", 10, StageKind.Open),
            new(null, english ? "Needs Analysis" : "İhtiyaç Analizi", 20, StageKind.Open),
            new(null, english ? "Proposal" : "Teklif", 50, StageKind.Open),
            new(null, english ? "Negotiation" : "Pazarlık", 75, StageKind.Open),
            new(null, english ? "Closed Won" : "Kazanıldı", 100, StageKind.Won),
            new(null, english ? "Closed Lost" : "Kaybedildi", 0, StageKind.Lost),
        ];
    }

    private static bool IsEnglish(string locale) => locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);
}
