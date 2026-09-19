using Crm.Modules.Service.Domain.Cases;
using Crm.Shared.Kernel;
using Crm.Shared.Kernel.Domain;

namespace Crm.Modules.Service.Domain.Sla;

/// <summary>
/// SLA politikası: öncelik başına bir satır (kiracıda tam dört; <c>(TenantId, Priority)</c> benzersiz). Süreler duvar saati dakikasıdır
/// (iş saati/tatil yok). Değişiklik yalnız yeni hesaplamaları (oluşturma, öncelik değişimi, yeniden açma) etkiler — mevcut taleplerin
/// hedefleri anlık görüntü olarak saklıdır. Geçerlilik: <c>1 ≤ ilk yanıt ≤ çözüm ≤ 525600</c>.
/// </summary>
public sealed class SlaPolicy : TenantAggregateRoot<Guid>, IAuditLogged
{
    private SlaPolicy()
    {
    }

    private SlaPolicy(Guid id, Guid tenantId, CasePriority priority, int firstResponseMinutes, int resolutionMinutes) : base(id, tenantId)
    {
        Priority = priority;
        FirstResponseMinutes = firstResponseMinutes;
        ResolutionMinutes = resolutionMinutes;
    }

    public CasePriority Priority { get; private set; }

    public int FirstResponseMinutes { get; private set; }

    public int ResolutionMinutes { get; private set; }

    public SlaMinutes Minutes => new(FirstResponseMinutes, ResolutionMinutes);

    /// <summary>Verilen öncelik için varsayılan değerlerle politika (§3.4).</summary>
    public static SlaPolicy CreateDefault(Guid tenantId, CasePriority priority) => Create(tenantId, priority, SlaPolicyDefaults.For(priority));

    public static SlaPolicy Create(Guid tenantId, CasePriority priority, SlaMinutes minutes)
    {
        Validate(minutes);
        return new SlaPolicy(Guid.CreateVersion7(), Guard.NotDefault(tenantId), priority, minutes.FirstResponseMinutes, minutes.ResolutionMinutes);
    }

    public void Update(SlaMinutes minutes)
    {
        Validate(minutes);
        FirstResponseMinutes = minutes.FirstResponseMinutes;
        ResolutionMinutes = minutes.ResolutionMinutes;
    }

    /// <summary>Doğrulama kuralı (API doğrulayıcısıyla ortak): <c>1 ≤ first ≤ resolution ≤ 525600</c>.</summary>
    public static bool IsValid(SlaMinutes minutes) =>
        minutes.FirstResponseMinutes >= 1
        && minutes.FirstResponseMinutes <= minutes.ResolutionMinutes
        && minutes.ResolutionMinutes <= ServiceLimits.MaxSlaMinutes;

    private static void Validate(SlaMinutes minutes) =>
        Guard.Against(!IsValid(minutes), "SLA minutes must satisfy 1 <= firstResponse <= resolution <= 525600.");
}

/// <summary>Varsayılan SLA süreleri (dakika; ilk yanıt / çözüm): urgent 60/240 · high 240/1440 · normal 480/4320 · low 1440/10080.</summary>
public static class SlaPolicyDefaults
{
    /// <summary>Sözleşmedeki sıra: <c>low, normal, high, urgent</c> (enum sırası).</summary>
    public static IReadOnlyList<CasePriority> Priorities { get; } = [CasePriority.Low, CasePriority.Normal, CasePriority.High, CasePriority.Urgent];

    public static SlaMinutes For(CasePriority priority) => priority switch
    {
        CasePriority.Urgent => new SlaMinutes(60, 240),
        CasePriority.High => new SlaMinutes(240, 1440),
        CasePriority.Normal => new SlaMinutes(480, 4320),
        CasePriority.Low => new SlaMinutes(1440, 10080),
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null),
    };
}
