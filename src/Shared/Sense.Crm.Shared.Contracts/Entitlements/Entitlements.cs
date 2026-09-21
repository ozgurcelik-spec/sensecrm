using Sense.Crm.Shared.Kernel.Results;
using Sense.Crm.Shared.Kernel.Time;

namespace Sense.Crm.Shared.Contracts.Entitlements;

/// <summary>Plan bayrağıyla kapatılabilen kapı modülleri (<c>identity</c>, <c>sales</c>, <c>activities</c> çekirdektir, kapatılamaz).</summary>
public static class GatedModules
{
    public const string Workflows = "workflows";
    public const string Commerce = "commerce";
    public const string Service = "service";
    public const string Marketing = "marketing";
    public const string Integrations = "integrations";

    public static IReadOnlyList<string> All { get; } = [Workflows, Commerce, Service, Marketing, Integrations];

    public static bool IsGated(string? module) => module is not null && All.Contains(module, StringComparer.Ordinal);
}

/// <summary>Limit anahtarları. <c>storage</c> (M8C): dosya eki depolama kotası (sert, kesin; <c>LimitDemand.Delta</c> = bayt).</summary>
public static class LimitKeys
{
    public const string Users = "users";
    public const string Records = "records";
    public const string Webhooks = "webhooks";
    public const string ApiKeys = "api_keys";
    public const string Storage = "storage";

    /// <summary><c>storage</c> limitinin modülü (M8C): <c>IModule.Name</c> = <c>LimitDemand.Module</c> = <c>OverLimitDto.module</c>.</summary>
    public const string StorageModule = "files";
}

public enum AccessLevel
{
    Full,
    ReadOnly,
    None,
}

/// <summary>Saklanan (kalıcı) yaşam döngüsü durumları (<c>platform.tenant_accounts.status</c>).</summary>
public static class StoredTenantStatuses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string PendingDeletion = "pending_deletion";
    public const string Deleted = "deleted";
}

/// <summary>Etkin durum tel değerleri (saklanan durum + deneme bitişi + askı kipinden okuma anında türetilir).</summary>
public static class TenantStatuses
{
    public const string Trial = "trial";
    public const string Active = "active";
    public const string TrialExpired = "trial_expired";
    public const string Suspended = "suspended";
    public const string PendingDeletion = "pending_deletion";
    public const string Deleted = "deleted";

    public static IReadOnlyList<string> All { get; } = [Trial, Active, TrialExpired, Suspended, PendingDeletion, Deleted];
}

/// <summary>Askı kipleri (<c>readOnly</c> varsayılan, <c>blocked</c> tam engel).</summary>
public static class SuspensionModes
{
    public const string ReadOnly = "readOnly";
    public const string Blocked = "blocked";

    public static bool IsValid(string? value) => value is ReadOnly or Blocked;
}

/// <summary>Zorlama hata kodları (metinler <c>SharedResource{,.en}.resx</c>).</summary>
public static class EntitlementErrors
{
    public const string TenantSuspended = "tenant.suspended";
    public const string ModuleDisabled = "plan.module_disabled";
    public const string LimitExceeded = "plan.limit_exceeded";

    public const string ReasonArg = "reason";
    public const string ModuleArg = "module";
    public const string LimitArg = "limit";
    public const string MaxArg = "max";
    public const string UsedArg = "used";

    public static Error Suspended(string status) => Error.Forbidden(TenantSuspended, (ReasonArg, status));

    public static Error DisabledModule(string module) => Error.Forbidden(ModuleDisabled, (ModuleArg, module));

    public static Error Exceeded(string limit, string? module, long max, long used) =>
        module is null
            ? Error.Payment(LimitExceeded, (LimitArg, limit), (MaxArg, max), (UsedArg, used))
            : Error.Payment(LimitExceeded, (LimitArg, limit), (ModuleArg, module), (MaxArg, max), (UsedArg, used));
}

/// <summary>
/// Yaşam döngüsü değerlendirmesi (saf fonksiyon, zamanlanmış iş yok): saklanan durum + askı kipi + deneme bitişi + <c>now</c> →
/// etkin durum ve erişim düzeyi. Tablo için bkz. docs/plan/m7-saas-hazirlik.md. <c>suspended</c> deneme bitişinden önceliklidir;
/// <c>now &gt;= trialEndsAt</c> deneme bitmiş demektir.
/// </summary>
public static class TenantLifecycle
{
    public static (string Status, AccessLevel Access) Evaluate(string rawStatus, string? suspensionMode, DateTimeOffset? trialEndsAt, DateTimeOffset now)
    {
        switch (rawStatus)
        {
            case StoredTenantStatuses.Deleted:
                return (TenantStatuses.Deleted, AccessLevel.None);
            case StoredTenantStatuses.PendingDeletion:
                return (TenantStatuses.PendingDeletion, AccessLevel.None);
            case StoredTenantStatuses.Suspended:
                return (TenantStatuses.Suspended, string.Equals(suspensionMode, SuspensionModes.Blocked, StringComparison.Ordinal) ? AccessLevel.None : AccessLevel.ReadOnly);
            default:
                if (trialEndsAt is null)
                {
                    return (TenantStatuses.Active, AccessLevel.Full);
                }

                return now >= trialEndsAt.Value ? (TenantStatuses.TrialExpired, AccessLevel.ReadOnly) : (TenantStatuses.Trial, AccessLevel.Full);
        }
    }
}

/// <summary>
/// Bir kiracının etkin plan hakları (plan + kiracıya özel istisna birleşimi) ve ham yaşam döngüsü durumu. Ham durum
/// önbelleklenir; etkin durum her çağrıda <c>now</c> ile hesaplanır (deneme bitişi önbellek süresine takılmaz).
/// </summary>
public sealed record EntitlementSnapshot(
    Guid TenantId,
    string PlanCode,
    string PlanName,
    string RawStatus,
    string? SuspensionMode,
    DateTimeOffset? TrialEndsAt,
    DateOnly? TrialEndsOn,
    string TimeZone,
    IReadOnlyDictionary<string, bool> Modules,
    int? MaxUsers,
    IReadOnlyDictionary<string, int?> MaxRecords,
    int? MaxWebhooks = null,
    int? MaxApiKeys = null,
    int? MaxStorageMb = null)
{
    /// <summary>Depolama kotası bayt olarak; <c>null</c> = sınırsız (M8C).</summary>
    public long? MaxStorageBytes => MaxStorageMb is { } mb ? mb * 1024L * 1024L : null;

    public (string Status, AccessLevel Access) Evaluate(DateTimeOffset now) => TenantLifecycle.Evaluate(RawStatus, SuspensionMode, TrialEndsAt, now);

    /// <summary>Kapı modülü planda açık mı (kapı olmayan modüller her zaman açıktır; eksik anahtar = kapalı).</summary>
    public bool IsModuleEnabled(string? module) => !GatedModules.IsGated(module) || (Modules.TryGetValue(module!, out var enabled) && enabled);

    /// <summary>Modül için kayıt üst sınırı; <c>null</c> = sınırsız.</summary>
    public int? MaxRecordsOf(string module) => MaxRecords.TryGetValue(module, out var max) ? max : null;

    /// <summary>Kiracı saat diliminde bugünden deneme bitiş gününe kalan gün (0 = son gün); yalnız etkin durum <c>trial</c> iken.</summary>
    public int? TrialDaysLeft(string status, DateTimeOffset now)
    {
        if (status != TenantStatuses.Trial || TrialEndsOn is not { } endsOn)
        {
            return null;
        }

        var today = TenantCalendar.For(TimeZone).Today(now);
        return Math.Max(endsOn.DayNumber - today.DayNumber, 0);
    }
}

/// <summary>Kiracı hakları okuma noktası (Platform uygular; olay veri yolu, Conductor yoklayıcısı ve oturum üretimi de kullanır).</summary>
public interface ITenantEntitlements
{
    Task<EntitlementSnapshot> GetAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>Bir limit tüketimi talebi (<paramref name="Module"/> = kaydın ait olduğu modül; <c>users</c> için <c>identity</c>).</summary>
public sealed record LimitDemand(string Key, string? Module, int Delta = 1);

/// <summary>
/// Limit denetimi: aşımda <c>plan.limit_exceeded</c> (402) döner (Platform uygular). <c>storage</c> için <see cref="LimitDemand.Delta"/> bayttır ve aşımın
/// <c>args</c>'ı <c>limit=storage, module=files, max, used</c> (bayt) taşır; dosya uçları bunu <c>file.quota_exceeded</c>'a çevirir.
/// </summary>
public interface ILimitGuard
{
    Task<Result> EnsureAsync(LimitDemand demand, CancellationToken ct = default);
}

/// <summary>Atanabilir (etkin) plan kataloğu; Identity eşzamanlı doğrulama için kullanır.</summary>
public interface IPlanCatalog
{
    Task<bool> IsAssignableAsync(string planCode, CancellationToken ct = default);

    /// <summary>Platform yöneticisi plan vermeden organizasyon açtığında atanan varsayılan plan kodu (<c>Platform:Provisioning:DefaultPlanCode</c>).</summary>
    string ProvisioningPlanCode { get; }
}

/// <summary>Kayıt oluşturup limite saydıran komut (<c>amount</c> kadar tüketir).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ConsumesLimitAttribute(string key, int amount = 1) : Attribute
{
    public string Key { get; } = key;

    public int Amount { get; } = amount;
}

/// <summary>Kayıt oluşturan ama limite saymayan komut (gerekçeli; mimari test her <c>Create*Command</c>'ın ya bunu ya <see cref="ConsumesLimitAttribute"/> taşımasını ister).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class NoPlanLimitAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>Salt okunur/askıda/engelli kiracıda da çalışan kullanıcı-düzeyi istek (onaylı listeyle birebir; mimari test).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class TenantStatusExemptAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>Sistem olay işleyicisi / Conductor görevi: kiracı durumu ve modül bayrağından bağımsız çalışır.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class EntitlementExemptAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
