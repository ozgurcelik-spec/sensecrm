using Sense.Crm.Shared.Kernel;
using Sense.Crm.Shared.Kernel.Domain;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Modules.Platform.Domain.Accounts;

/// <summary>Bir abonelik değişikliğinin kapsamı (denetim/olay için).</summary>
public sealed record SubscriptionChange(
    bool PlanChanged,
    bool TrialChanged,
    bool OverridesChanged,
    string OldPlanCode,
    DateOnly? OldTrialEndsOn,
    string? OldOverridesJson)
{
    public bool Changed => PlanChanged || TrialChanged || OverridesChanged;
}

/// <summary>
/// Kiracının platform tarafı hesabı: plan ataması, yaşam döngüsü durumu, deneme, istisna, askı, onboarding ilerlemesi. Küresel tablodur
/// (<see cref="ITenantEntity"/> DEĞİL); <c>Id</c> = <c>identity.tenants.id</c> (çapraz şema FK yok). Saklanan durum
/// <c>active | suspended | pending_deletion | deleted</c>; <b>etkin</b> durum (trial/trial_expired …) okuma anında
/// <c>TenantLifecycle.Evaluate</c> ile türetilir (zamanlanmış iş yok). Sistem kiracısı askıya alınamaz/silinemez/planı değiştirilemez.
/// Platform agregatları <c>IAuditLogged</c> değildir: platform denetimi ayrı tabloya açıkça yazılır.
/// </summary>
public sealed class TenantAccount : AggregateRoot<Guid>
{
    private TenantAccount()
    {
    }

    private TenantAccount(Guid tenantId, string name, string slug, string planCode, string source, bool isSystem, DateTime tenantCreatedAt) : base(tenantId)
    {
        Name = name;
        Slug = slug;
        PlanCode = planCode;
        Source = source;
        IsSystem = isSystem;
        TenantCreatedAt = tenantCreatedAt;
        Status = AccountStatuses.Active;
    }

    /// <summary><c>tenant_id</c>.</summary>
    public Guid TenantId => Id;

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public string PlanCode { get; private set; } = string.Empty;

    /// <summary>Saklanan durum: <c>active | suspended | pending_deletion | deleted</c>.</summary>
    public string Status { get; private set; } = string.Empty;

    public string Source { get; private set; } = string.Empty;

    public bool IsSystem { get; private set; }

    public DateOnly? TrialEndsOn { get; private set; }

    /// <summary>Deneme bitiş anı (UTC): <c>trialEndsOn</c>'dan sonraki günün başlangıcı (kiracı saat diliminde); <c>now &gt;= TrialEndsAt</c> ise bitmiştir.</summary>
    public DateTime? TrialEndsAt { get; private set; }

    /// <summary>Kiracıya özel istisna JSON'u (<see cref="TenantOverrides"/>); yoksa null.</summary>
    public string? Overrides { get; private set; }

    public string? SuspensionMode { get; private set; }

    public DateTime? SuspendedAt { get; private set; }

    public string? SuspendedReason { get; private set; }

    public DateTime? PlanChangedAt { get; private set; }

    public DateTime? OnboardingDismissedAt { get; private set; }

    /// <summary>Tamamlanan onboarding adımı anahtarları (kalıcı; kayıt silinse bile "tamamlandı" kalır).</summary>
    public List<string> OnboardingDone { get; private set; } = [];

    public DateTime TenantCreatedAt { get; private set; }

    public DateTime? DeletedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c> eşzamanlılık belirteci (EF doldurur).</summary>
    public uint Version { get; private set; }

    /// <summary>Yeni hesap (plan ataması çağıranın sorumluluğundadır; plan kodu katalogda doğrulanmış olmalı).</summary>
    public static TenantAccount Create(
        Guid tenantId,
        string name,
        string slug,
        string planCode,
        string source,
        bool isSystem,
        DateOnly? trialEndsOn,
        DateTime? trialEndsAt,
        DateTime tenantCreatedAt,
        DateTime? onboardingDismissedAt)
    {
        var account = new TenantAccount(
            Guard.NotDefault(tenantId),
            Guard.MaxLength(Guard.NotEmpty(name), PlatformLimits.TenantNameMaxLength),
            Guard.MaxLength(Guard.NotEmpty(slug), PlatformLimits.SlugMaxLength),
            Guard.MaxLength(Guard.NotEmpty(planCode), PlatformLimits.PlanCodeMaxLength),
            Guard.NotEmpty(source),
            isSystem,
            tenantCreatedAt)
        {
            TrialEndsOn = trialEndsOn,
            TrialEndsAt = trialEndsAt,
            OnboardingDismissedAt = onboardingDismissedAt,
        };
        return account;
    }

    /// <summary>
    /// Lazy satırı gerçek olayın planı/kaynağıyla düzeltir (yalnız <c>source = lazy</c> satır için çağrılır). Platform yöneticisi planı/denemeyi bu arada
    /// değiştirdiyse (<see cref="PlanChangedAt"/> dolu) geç gelen olay onu ezmez; yalnız kaynak/ad/slug düzeltilir.
    /// </summary>
    public void ApplyProvisioning(string planCode, string source, bool isSystem, DateOnly? trialEndsOn, DateTime? trialEndsAt, string name, string slug)
    {
        if (PlanChangedAt is null)
        {
            PlanCode = planCode;
            TrialEndsOn = trialEndsOn;
            TrialEndsAt = trialEndsAt;
        }

        Source = source;
        IsSystem = isSystem;
        Name = Guard.MaxLength(Guard.NotEmpty(name), PlatformLimits.TenantNameMaxLength);
        Slug = Guard.MaxLength(Guard.NotEmpty(slug), PlatformLimits.SlugMaxLength);
    }

    /// <summary>Platform işletim organizasyonu işareti (askıya alınamaz/silinemez/planı değiştirilemez).</summary>
    public void MarkSystem() => IsSystem = true;

    /// <summary>Okuma kopyası ad eşitleme (<c>OrganizationUpdated</c>).</summary>
    public void SyncName(string name)
    {
        if (Status != AccountStatuses.Deleted)
        {
            Name = Guard.MaxLength(Guard.NotEmpty(name), PlatformLimits.TenantNameMaxLength);
        }
    }

    /// <summary>
    /// Plan/deneme/istisna tam değiştirme. Sistem kiracısı <c>platform.system_tenant_protected</c> (422); <c>pending_deletion|deleted</c>
    /// <c>platform.invalid_transition</c> (409). Değişiklik yoksa <see cref="SubscriptionChange.Changed"/> false döner ve hiçbir alan değişmez.
    /// </summary>
    public Result<SubscriptionChange> ChangeSubscription(string planCode, DateOnly? trialEndsOn, DateTime? trialEndsAt, TenantOverrides overrides, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (IsSystem)
        {
            return Error.Rule(PlatformErrors.SystemTenantProtected);
        }

        if (Status is AccountStatuses.PendingDeletion or AccountStatuses.Deleted)
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", Status));
        }

        var newOverrides = overrides.IsEmpty ? null : overrides.ToJson();
        var change = new SubscriptionChange(
            !string.Equals(PlanCode, planCode, StringComparison.Ordinal),
            TrialEndsOn != trialEndsOn,
            !TenantOverrides.FromJson(Overrides).SameAs(overrides),
            PlanCode,
            TrialEndsOn,
            Overrides);
        if (!change.Changed)
        {
            return change;
        }

        PlanCode = planCode;
        TrialEndsOn = trialEndsOn;
        TrialEndsAt = trialEndsAt;
        Overrides = newOverrides;
        PlanChangedAt = nowUtc;
        return change;
    }

    /// <summary><c>active → suspended</c>. Sistem kiracısı 422; <c>active</c> değilse <c>platform.invalid_transition</c> (409).</summary>
    public Result Suspend(string reason, string mode, DateTime nowUtc)
    {
        if (IsSystem)
        {
            return Error.Rule(PlatformErrors.SystemTenantProtected);
        }

        if (Status != AccountStatuses.Active)
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", AccountStatuses.Suspended));
        }

        Status = AccountStatuses.Suspended;
        SuspensionMode = mode;
        SuspendedReason = Guard.MaxLength(Guard.NotEmpty(reason), PlatformLimits.ReasonMaxLength);
        SuspendedAt = nowUtc;
        return Result.Success();
    }

    /// <summary><c>suspended → active</c> (deneme bitmişse etkin durum <c>trial_expired</c> kalır; plan/deneme ayrıca düzeltilir).</summary>
    public Result Reactivate()
    {
        if (Status != AccountStatuses.Suspended)
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", AccountStatuses.Active));
        }

        Status = AccountStatuses.Active;
        SuspensionMode = null;
        SuspendedReason = null;
        SuspendedAt = null;
        return Result.Success();
    }

    /// <summary><c>active|suspended → pending_deletion</c>; önceki durumu döner (silme talebi saklar). Sistem kiracısı 422.</summary>
    public Result<string> MarkPendingDeletion()
    {
        if (IsSystem)
        {
            return Error.Rule(PlatformErrors.SystemTenantProtected);
        }

        if (Status is not (AccountStatuses.Active or AccountStatuses.Suspended))
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", AccountStatuses.PendingDeletion));
        }

        var previous = Status;
        Status = AccountStatuses.PendingDeletion;
        return previous;
    }

    /// <summary><c>pending_deletion → önceki durum</c> (yalnız bekleme süresinde iptal).</summary>
    public Result RestoreAfterDeletionCancelled(string previousStatus)
    {
        if (Status != AccountStatuses.PendingDeletion)
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", previousStatus));
        }

        Status = previousStatus;
        return Result.Success();
    }

    /// <summary><c>pending_deletion → deleted</c> (yalnız imha işi): mezar taşı — ad/slug redakte, istisna temizlenir.</summary>
    public Result MarkDeleted(DateTime nowUtc)
    {
        if (IsSystem)
        {
            return Error.Rule(PlatformErrors.SystemTenantProtected);
        }

        if (Status is not (AccountStatuses.PendingDeletion or AccountStatuses.Deleted))
        {
            return Error.Conflict(PlatformErrors.InvalidTransition, ("from", Status), ("to", AccountStatuses.Deleted));
        }

        Status = AccountStatuses.Deleted;
        DeletedAt ??= nowUtc;
        Name = PlatformLimits.DeletedNamePlaceholder;

        // Kimlikler UUIDv7'dir (zamana göre sıralı): ilk 8 hex ~65 sn'lik pencerede aynıdır ve slug benzersiz indeksini çakıştırır; son 8 hex rastgele bitlerdendir.
        Slug = PlatformLimits.DeletedSlugPrefix + TenantId.ToString("N")[^8..];
        Overrides = null;
        return Result.Success();
    }

    /// <summary>Tamamlanan onboarding adımlarını kalıcılaştırır (yeni eklenen varsa true).</summary>
    public bool MarkOnboardingDone(IEnumerable<string> keys)
    {
        var added = false;
        foreach (var key in keys)
        {
            if (!OnboardingDone.Contains(key, StringComparer.Ordinal))
            {
                OnboardingDone.Add(key);
                added = true;
            }
        }

        return added;
    }

    /// <summary>Onboarding kartını kapatır (idempotent).</summary>
    public void DismissOnboarding(DateTime nowUtc) => OnboardingDismissedAt ??= nowUtc;
}
