using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Modules.Platform.Domain.Plans;

/// <summary>
/// Plan kataloğu satırı: yapılandırma (<c>Platform:Plans</c>) güdümlüdür, Migrator idempotent upsert eder, API yazmaz (D3). Limitler kiracıya
/// kopyalanmaz; plan satırından okunur (plan değişikliği tüm abonelere yansır). Yapılandırmadan kalkan plan <c>is_active = false</c> olur
/// (mevcut atamalar çalışır, yeni atama <c>platform.plan_not_found</c>). Küresel tablodur.
/// </summary>
public sealed class Plan : Entity<string>
{
    private Plan()
    {
    }

    private Plan(string code) : base(code)
    {
    }

    /// <summary>Plan kodu (PK, <c>code</c> kolonu).</summary>
    public string Code => Id;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    public int SortOrder { get; private set; }

    public int? TrialDays { get; private set; }

    public PlanLimits Limits { get; private set; } = PlanLimits.Unlimited;

    /// <summary>Kapı modülü bayrakları (eksik anahtar = kapalı).</summary>
    public IReadOnlyDictionary<string, bool> Modules { get; private set; } = new Dictionary<string, bool>();

    public static Plan Create(string code, string name, string? description, int sortOrder, int? trialDays, PlanLimits limits, IReadOnlyDictionary<string, bool> modules)
    {
        var plan = new Plan(Guard.MaxLength(Guard.NotEmpty(code), PlatformLimits.PlanCodeMaxLength));
        plan.Apply(name, description, sortOrder, trialDays, limits, modules);
        plan.IsActive = true;
        return plan;
    }

    /// <summary>Yapılandırmadaki tanımı uygular (değişen var mı: true).</summary>
    public bool Apply(string name, string? description, int sortOrder, int? trialDays, PlanLimits limits, IReadOnlyDictionary<string, bool> modules)
    {
        var cleanName = Guard.MaxLength(Guard.NotEmpty(name), PlatformLimits.PlanNameMaxLength);
        var cleanDescription = string.IsNullOrWhiteSpace(description) ? null : Guard.MaxLength(description.Trim(), PlatformLimits.DescriptionMaxLength);

        var changed = Name != cleanName
            || Description != cleanDescription
            || SortOrder != sortOrder
            || TrialDays != trialDays
            || !PlanLimitsEqual(Limits, limits)
            || !ModulesEqual(Modules, modules)
            || !IsActive;

        Name = cleanName;
        Description = cleanDescription;
        SortOrder = sortOrder;
        TrialDays = trialDays;
        Limits = limits;
        Modules = modules;
        IsActive = true;
        return changed;
    }

    /// <summary>Yapılandırmadan kalkan plan (mevcut atamalar çalışır, yeni atama yapılamaz).</summary>
    public bool Deactivate()
    {
        if (!IsActive)
        {
            return false;
        }

        IsActive = false;
        return true;
    }

    private static bool PlanLimitsEqual(PlanLimits a, PlanLimits b) =>
        a.MaxUsers == b.MaxUsers
        && a.MaxWebhooks == b.MaxWebhooks
        && a.MaxApiKeys == b.MaxApiKeys
        && a.MaxRecords.Count == b.MaxRecords.Count
        && a.MaxRecords.All(kv => b.MaxRecords.TryGetValue(kv.Key, out var other) && other == kv.Value);

    private static bool ModulesEqual(IReadOnlyDictionary<string, bool> a, IReadOnlyDictionary<string, bool> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other == kv.Value);
}
