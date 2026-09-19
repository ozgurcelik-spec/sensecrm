namespace Sense.Crm.Modules.Service.Domain;

/// <summary>
/// Service hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m6b-servis.md §4.5).
/// Ortak kodlar (validation, forbidden, not_found, general.concurrency_conflict) <c>Sense.Crm.Shared.Kernel.Results.ErrorCodes</c>'tadır.
/// </summary>
public static class ServiceErrors
{
    /// <summary>Firma aktif organizasyonda yok → 404.</summary>
    public const string AccountNotFound = "case.account_not_found";

    /// <summary>Kişi aktif organizasyonda yok → 404.</summary>
    public const string ContactNotFound = "case.contact_not_found";

    /// <summary>Kişinin firması, verilen firmadan farklı → 400.</summary>
    public const string ContactAccountMismatch = "case.contact_account_mismatch";

    /// <summary>Durum makinesinde izinsiz geçiş (<c>from</c>, <c>to</c> argümanları) → 409.</summary>
    public const string InvalidTransition = "case.invalid_transition";

    /// <summary>Çözme / çözülmeden kapatmada not boş → 400.</summary>
    public const string ResolutionRequired = "case.resolution_required";

    /// <summary>Kapalı talep izin verilen süreden sonra açılmak istendi → 409.</summary>
    public const string ReopenWindowExpired = "case.reopen_window_expired";

    /// <summary>Aktif olmayan (resolved/closed) talepte PUT/öncelik/atama → 409.</summary>
    public const string NotActive = "case.not_active";

    /// <summary>Kapalı talepte yorum → 409.</summary>
    public const string Closed = "case.closed";

    /// <summary>Atanan kullanıcı organizasyonun aktif üyesi değil → 400 (Sales/Activities ile aynı anahtar).</summary>
    public const string OwnerNotMember = "owner.not_member";

    /// <summary>SLA politikası dizisi tam dört önceliği (her biri bir kez) içermeli (doğrulama mesajı anahtarı).</summary>
    public const string SlaPoliciesIncomplete = "validation.sla_policies";

    /// <summary>SLA süreleri: 1 ≤ ilk yanıt ≤ çözüm ≤ 525600 (doğrulama mesajı anahtarı).</summary>
    public const string SlaMinutesRange = "validation.sla_minutes";

    /// <summary>Filtre değeri bilinen bir enum değeri değil (doğrulama mesajı anahtarı).</summary>
    public const string InvalidFilterValue = "validation.case_filter";

    /// <summary><c>unassigned=true</c> ile <c>assignedUserId</c> birlikte verilemez (doğrulama mesajı anahtarı).</summary>
    public const string UnassignedConflict = "validation.case_unassigned";
}

/// <summary>Domain sınırları ve iş kuralı sabitleri. Ayar değil, veri modeli/iş kuralı kısıtı.</summary>
public static class ServiceLimits
{
    public const int NumberMaxLength = 20;
    public const int SubjectMaxLength = 200;
    public const int DescriptionMaxLength = 8000;
    public const int ResolutionNoteMaxLength = 4000;
    public const int CommentBodyMaxLength = 8000;
    public const int EnumColumnMaxLength = 20;
    public const int EventValueMaxLength = 64;
    public const int MaxSlaMinutes = 525_600;
}
