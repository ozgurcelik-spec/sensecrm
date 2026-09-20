using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sense.Crm.Modules.Platform.Domain;

/// <summary>Platform hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m7-saas-hazirlik.md).</summary>
public static class PlatformErrors
{
    /// <summary>Plan yok/pasif → 404.</summary>
    public const string PlanNotFound = "platform.plan_not_found";

    /// <summary>Yaşam döngüsünde tanımsız geçiş → 409 (<c>from</c>/<c>to</c> argümanları).</summary>
    public const string InvalidTransition = "platform.invalid_transition";

    /// <summary>Talep <c>scheduled</c> değil veya bekleme süresi doldu → 409.</summary>
    public const string DeletionNotCancellable = "platform.deletion_not_cancellable";

    /// <summary>Sistem kiracısına askı/silme/plan değişikliği → 422.</summary>
    public const string SystemTenantProtected = "platform.system_tenant_protected";

    /// <summary>Yazılan kiracı adı sunucudaki adla eşleşmiyor → 422 (yıkıcı komutlar; C-SEC2 H2).</summary>
    public const string ConfirmationMismatch = "platform.confirmation_mismatch";

    /// <summary>Step-up: çağıranın parolası verilmedi → 422 (401 DEĞİL: oturum düşmesin).</summary>
    public const string StepUpRequired = "platform.step_up_required";

    /// <summary>Step-up: parola yanlış → 422.</summary>
    public const string StepUpFailed = "platform.step_up_failed";

    /// <summary>Step-up: hatalı deneme sınırı aşıldı ya da hesap kilitli → 429.</summary>
    public const string StepUpRateLimited = "platform.step_up_rate_limited";

    /// <summary>Son aktif platform yöneticisi geri alınamaz/pasifleştirilemez → 409.</summary>
    public const string LastPlatformAdmin = "platform.last_platform_admin";

    /// <summary>Hedef hesap platform yöneticisi değil → 409 (geri alınacak bir şey yok).</summary>
    public const string NotAPlatformAdmin = "platform.not_a_platform_admin";

    /// <summary>Yeniden deneme yalnız <c>failed</c> talepte → 409.</summary>
    public const string DeletionNotRetryable = "platform.deletion_not_retryable";

    /// <summary>İmha sonrası doğrulama: kiracı kimlikli tabloda hâlâ satır var (<c>DeletionRequest.LastError</c> kodu; tombstone yazılmaz).</summary>
    public const string ErasureVerificationFailed = "erasure.verification_failed";

    /// <summary>İmha ön koşulu (yeniden doğrulama) sağlanmadı: talep artık koşmaya uygun değil (<c>DeletionRequest.LastError</c> kodu).</summary>
    public const string ErasurePreconditionFailed = "erasure.precondition_failed";

    // Doğrulama mesajı anahtarları
    public const string InvalidTrialDate = "validation.platform_trial_date";
    public const string InvalidReason = "validation.platform_reason";
    public const string InvalidMode = "validation.platform_mode";
    public const string InvalidOverrides = "validation.platform_overrides";
    public const string InvalidRetention = "validation.platform_retention";
    public const string InvalidRange = "validation.platform_range";
    public const string UnknownPlan = "validation.platform_plan";
}

/// <summary>Sınırlar ve sütun uzunlukları.</summary>
public static class PlatformLimits
{
    public const int PlanCodeMaxLength = 32;
    public const int PlanNameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int ReasonMaxLength = 500;
    public const int StatusMaxLength = 20;
    public const int SourceMaxLength = 16;
    public const int SuspensionModeMaxLength = 12;
    public const int SlugMaxLength = 64;
    public const int TenantNameMaxLength = 200;
    public const int ActionMaxLength = 48;
    public const int EmailMaxLength = 256;
    public const int IpMaxLength = 64;
    public const int CorrelationIdMaxLength = 128;
    public const int LastErrorMaxLength = 2000;
    public const string DeletedNamePlaceholder = "[deleted]";

    /// <summary>Serbest metin gerekçenin imhada yerine yazılan yer tutucu (<c>deletion_requests.reason</c>, <c>platform_audit_entries.details.reason</c>; L1).</summary>
    public const string RedactedReasonPlaceholder = "[redacted]";

    /// <summary>Platform denetiminin (retention işi) silebileceği en genç satır yaşı (gün): veritabanı tetikleyicisi bunun altını hiçbir işaretle silmez (M3).</summary>
    public const int MinAuditRetentionDays = 30;
    public const string DeletedSlugPrefix = "deleted-";
    public const int MinRetentionDays = 7;
    public const int MaxRetentionDays = 90;
    public const int MaxUsageRangeDays = 400;
}

/// <summary>Saklanan yaşam döngüsü durumları (tel değerleri <c>Shared.Contracts.Entitlements.StoredTenantStatuses</c> ile aynıdır; Domain yalnız Kernel'e bağlıdır).</summary>
public static class AccountStatuses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string PendingDeletion = "pending_deletion";
    public const string Deleted = "deleted";
}

/// <summary>Hesap kaynağı (<c>tenant_accounts.source</c>).</summary>
public static class AccountSources
{
    public const string Signup = "signup";
    public const string Platform = "platform";
    public const string Bootstrap = "bootstrap";
    public const string Backfill = "backfill";
    public const string Lazy = "lazy";

    public static IReadOnlyList<string> All { get; } = [Signup, Platform, Bootstrap, Backfill, Lazy];
}

/// <summary>Silme talebi durumları.</summary>
public static class DeletionStatuses
{
    public const string Scheduled = "scheduled";
    public const string Cancelled = "cancelled";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    /// <summary>Kiracı başına tek etkin talep (kısmi benzersiz indeks).</summary>
    public static IReadOnlyList<string> Active { get; } = [Scheduled, Running, Failed];
}

/// <summary>Platform denetim eylem adları (sözleşme).</summary>
public static class PlatformAuditActions
{
    public const string OrganizationCreated = "organization.created";
    public const string SubscriptionChanged = "subscription.changed";
    public const string OrganizationSuspended = "organization.suspended";
    public const string OrganizationReactivated = "organization.reactivated";
    public const string DeletionRequested = "deletion.requested";
    public const string DeletionCancelled = "deletion.cancelled";
    public const string DeletionCompleted = "deletion.completed";
    public const string DeletionFailed = "deletion.failed";
    public const string UsageRefreshed = "usage.refreshed";
    public const string UsageExported = "usage.exported";

    /// <summary>Başarısız imha talebi yeniden denemeye alındı (C-SEC2 L3).</summary>
    public const string DeletionRetried = "deletion.retried";

    /// <summary>Platform yöneticisi yetkisi geri alındı (C-SEC2 M6).</summary>
    public const string PlatformAdminRevoked = "platform_admin.revoked";
}

/// <summary>Plan limitleri (<c>null</c> = sınırsız; <c>0</c> = o modülde yeni kayıt açılamaz).</summary>
public sealed record PlanLimits(int? MaxUsers, IReadOnlyDictionary<string, int?> MaxRecords)
{
    public static PlanLimits Unlimited { get; } = new(null, new Dictionary<string, int?>());
}

/// <summary>
/// Kiracıya özel istisna (kısmi). <c>maxUsers</c> anahtarının <b>varlığı</b> esastır: <see cref="MaxUsersSet"/> ve <c>MaxUsers == null</c> açıkça
/// "sınırsız" demektir; anahtar yoksa plan geçerlidir. <c>maxRecords</c> içinde <c>null</c> değer de aynı şekilde "sınırsız"dır.
/// </summary>
public sealed record TenantOverrides(bool MaxUsersSet, int? MaxUsers, IReadOnlyDictionary<string, int?> MaxRecords, IReadOnlyDictionary<string, bool> Modules)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static TenantOverrides None { get; } = new(false, null, new Dictionary<string, int?>(), new Dictionary<string, bool>());

    public bool IsEmpty => !MaxUsersSet && MaxRecords.Count == 0 && Modules.Count == 0;

    /// <summary>
    /// Yapısal eşitlik (metin karşılaştırması değil): <c>jsonb</c> sütunu JSON metnini normalize eder (boşluk, anahtar sırası), bu yüzden saklı metin ile yeni
    /// <see cref="ToJson"/> çıktısı aynı istisna için bile farklı olabilir; "değişiklik yok" tespiti yapısal yapılır.
    /// </summary>
    public bool SameAs(TenantOverrides other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return MaxUsersSet == other.MaxUsersSet
            && MaxUsers == other.MaxUsers
            && SameMap(MaxRecords, other.MaxRecords)
            && SameMap(Modules, other.Modules);
    }

    private static bool SameMap<T>(IReadOnlyDictionary<string, T> left, IReadOnlyDictionary<string, T> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(pair.Value, value));

    /// <summary>JSON gösterimi (yalnız verilen anahtarlar): <c>{ "maxUsers": 10, "maxRecords": { "sales": null }, "modules": { "workflows": true } }</c>.</summary>
    public string ToJson()
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (MaxUsersSet)
        {
            map["maxUsers"] = MaxUsers;
        }

        if (MaxRecords.Count > 0)
        {
            map["maxRecords"] = MaxRecords;
        }

        if (Modules.Count > 0)
        {
            map["modules"] = Modules;
        }

        return JsonSerializer.Serialize(map, JsonOptions);
    }

    /// <summary>Saklı JSON'u okur; boş/geçersiz → <see cref="None"/>.</summary>
    public static TenantOverrides FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return None;
        }

        using var document = JsonDocument.Parse(json);
        return FromElement(document.RootElement) ?? None;
    }

    /// <summary>JSON nesnesinden okur (istek gövdesi ve saklı değer ortak yol); nesne değilse null.</summary>
    public static TenantOverrides? FromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var maxUsersSet = false;
        int? maxUsers = null;
        var records = new Dictionary<string, int?>(StringComparer.Ordinal);
        var modules = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "maxusers":
                    maxUsersSet = true;
                    maxUsers = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var users) ? users : null;
                    break;
                case "maxrecords" when property.Value.ValueKind == JsonValueKind.Object:
                    foreach (var record in property.Value.EnumerateObject())
                    {
                        records[record.Name] = record.Value.ValueKind == JsonValueKind.Number && record.Value.TryGetInt32(out var max) ? max : null;
                    }

                    break;
                case "modules" when property.Value.ValueKind == JsonValueKind.Object:
                    foreach (var module in property.Value.EnumerateObject())
                    {
                        modules[module.Name] = module.Value.ValueKind == JsonValueKind.True;
                    }

                    break;
                default:
                    break;
            }
        }

        return new TenantOverrides(maxUsersSet, maxUsers, records, modules);
    }
}

/// <summary>Plan + istisna birleşiminin sonucu (etkin haklar).</summary>
public sealed record EffectiveLimits(int? MaxUsers, IReadOnlyDictionary<string, int?> MaxRecords, IReadOnlyDictionary<string, bool> Modules);

/// <summary>
/// Etkin hak birleştirme (saf fonksiyon, birim testli): <b>etkin değer = istisna varsa o, yoksa plan</b>. <c>maxUsers: null</c> istisnası açıkça sınırsız;
/// anahtar hiç yoksa plan geçerli. Modül bayrakları: istisna &gt; plan &gt; kapalı (eksik anahtar = <c>false</c>).
/// </summary>
public static class EffectiveEntitlements
{
    public static EffectiveLimits Merge(PlanLimits planLimits, IReadOnlyDictionary<string, bool> planModules, TenantOverrides? overrides, IReadOnlyList<string> gatedModules)
    {
        ArgumentNullException.ThrowIfNull(planLimits);
        ArgumentNullException.ThrowIfNull(planModules);
        ArgumentNullException.ThrowIfNull(gatedModules);

        var over = overrides ?? TenantOverrides.None;

        var maxUsers = over.MaxUsersSet ? over.MaxUsers : planLimits.MaxUsers;

        var records = new Dictionary<string, int?>(planLimits.MaxRecords, StringComparer.Ordinal);
        foreach (var (module, max) in over.MaxRecords)
        {
            records[module] = max;
        }

        var modules = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var module in gatedModules)
        {
            modules[module] = over.Modules.TryGetValue(module, out var forced)
                ? forced
                : planModules.TryGetValue(module, out var included) && included;
        }

        return new EffectiveLimits(maxUsers, records, modules);
    }
}
