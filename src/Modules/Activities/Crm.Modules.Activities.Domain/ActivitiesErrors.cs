namespace Crm.Modules.Activities.Domain;

/// <summary>
/// Activities hata kodları (= web istemcisiyle sözleşme + yerelleştirme anahtarları, docs/plan/m3-aktivite-rapor.md).
/// Ortak kodlar (validation, forbidden, not_found) <c>Crm.Shared.Kernel.Results.ErrorCodes</c>'tadır.
/// </summary>
public static class ActivitiesErrors
{
    /// <summary>İlişkili kayıt (firma/kişi/potansiyel/fırsat) aktif organizasyonda yok → 404.</summary>
    public const string RelatedNotFound = "activity.related_not_found";

    /// <summary><c>endAt</c>, <c>startAt</c>'ten önce → 400.</summary>
    public const string InvalidRange = "activity.invalid_range";

    /// <summary>Not her zaman tamamlanmış: tamamla/yeniden aç veya farklı durum → 409.</summary>
    public const string NoteStatusFixed = "activity.note_status_fixed";

    /// <summary>Atanan kullanıcı organizasyonun aktif üyesi değil → 400 (Sales ile aynı anahtar).</summary>
    public const string OwnerNotMember = "owner.not_member";

    /// <summary><c>relatedType</c> ve <c>relatedId</c> birlikte verilmeli (doğrulama mesajı anahtarı).</summary>
    public const string RelatedIncomplete = "validation.activity_related";
}

/// <summary>Domain sınırları (uzunluklar vb.). Ayar değil, veri modeli kısıtı.</summary>
public static class ActivityLimits
{
    public const int SubjectMaxLength = 200;
    public const int DescriptionMaxLength = 4000;
    public const int EnumColumnMaxLength = 20;
}
