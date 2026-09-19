using Sense.Crm.Modules.Activities.Domain.Activities;

namespace Sense.Crm.Modules.Activities.Application;

// HTTP sözleşmesi (docs/plan/m3-aktivite-rapor.md): alanlar camelCase serileştirilir, null alanlar yazılmaz, enum'lar camelCase string.

/// <summary>
/// Aktivite yanıtı. <see cref="AssignedUserName"/> üye adı, <see cref="RelatedName"/> bağlı kaydın görünen adıdır (kayıt silinmişse
/// boş); <see cref="IsOverdue"/> açık ve son tarihi geçmiş aktivitelerde true.
/// </summary>
public sealed record ActivityDto(
    Guid Id,
    ActivityType Type,
    string Subject,
    string? Description,
    ActivityStatus Status,
    ActivityPriority Priority,
    DateTime? DueAt,
    DateTime? StartAt,
    DateTime? EndAt,
    ActivityRelatedType? RelatedType,
    Guid? RelatedId,
    string? RelatedName,
    Guid AssignedUserId,
    string? AssignedUserName,
    DateTime? CompletedAt,
    bool IsOverdue,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Kişisel iş özeti: kiracı saat dilimine göre "bugün" ve hafta başı (pazartesi). Notlar sayılmaz.</summary>
public sealed record ActivitySummaryDto(int OpenCount, int OverdueCount, int DueTodayCount, int CompletedThisWeek);

/// <summary>Liste filtreleri (sözleşme: assignedUserId, type, status, relatedType, relatedId, dueFrom, dueTo, overdue). Tarihler UTC.</summary>
public sealed record ActivityFilter(
    Guid? AssignedUserId,
    ActivityType? Type,
    ActivityStatus? Status,
    ActivityRelatedType? RelatedType,
    Guid? RelatedId,
    DateTime? DueFrom,
    DateTime? DueTo,
    bool? Overdue);

/// <summary>Aktivite raporu satırı: kullanıcı başına tamamlanan/açık/geciken aktivite sayısı.</summary>
public sealed record ActivityUserReportRow(Guid UserId, string? UserName, int CompletedCount, int OpenCount, int OverdueCount);

/// <summary>Kullanıcı başına ham toplamlar (depo çıktısı; ad çözümü handler'da).</summary>
public sealed record ActivityUserTotals(Guid UserId, int CompletedCount, int OpenCount, int OverdueCount);

/// <summary>
/// Özet sınırları (UTC): "bugün" [<see cref="TodayStartUtc"/>, <see cref="TomorrowStartUtc"/>) ve "bu hafta"
/// [<see cref="WeekStartUtc"/>, <see cref="NextWeekStartUtc"/>) kiracı saat diliminden hesaplanır.
/// </summary>
public sealed record ActivitySummaryWindow(DateTime NowUtc, DateTime TodayStartUtc, DateTime TomorrowStartUtc, DateTime WeekStartUtc, DateTime NextWeekStartUtc);
