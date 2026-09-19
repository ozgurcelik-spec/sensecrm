using Sense.Crm.Modules.Service.Domain.Cases;

namespace Sense.Crm.Modules.Service.Application;

// HTTP sözleşmesi (docs/plan/m6b-servis.md): alanlar camelCase serileştirilir, null alanlar yazılmaz, enum'lar camelCase string.

/// <summary>
/// Talep liste öğesi (açıklama ve çözüm notu yok). Adlar (<c>accountName</c>, <c>contactName</c>, <c>assignedUserName</c>, <c>createdByName</c>)
/// toplu çözülür; bağlı kayıt silinmişse ad boş döner. <c>slaState</c>/<c>isSlaBreached</c> okuma anında hesaplanır.
/// </summary>
public sealed record CaseListItemDto(
    Guid Id,
    string Number,
    string Subject,
    CaseStatus Status,
    CasePriority Priority,
    CaseChannel Channel,
    Guid? AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? AssignedUserId,
    string? AssignedUserName,
    int ReopenCount,
    DateTime? FirstResponseAt,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    DateTime FirstResponseDueAt,
    DateTime DueAt,
    bool IsSlaBreached,
    SlaState SlaState,
    bool FirstResponseBreached,
    bool ResolutionBreached,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    Guid? CreatedByUserId,
    string? CreatedByName);

/// <summary>Talep detayı: liste öğesi alanları + <c>description</c>, <c>resolutionNote</c>.</summary>
public sealed record CaseDetailDto(
    Guid Id,
    string Number,
    string Subject,
    string? Description,
    CaseStatus Status,
    CasePriority Priority,
    CaseChannel Channel,
    Guid? AccountId,
    string? AccountName,
    Guid? ContactId,
    string? ContactName,
    Guid? AssignedUserId,
    string? AssignedUserName,
    string? ResolutionNote,
    int ReopenCount,
    DateTime? FirstResponseAt,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    DateTime FirstResponseDueAt,
    DateTime DueAt,
    bool IsSlaBreached,
    SlaState SlaState,
    bool FirstResponseBreached,
    bool ResolutionBreached,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    Guid? CreatedByUserId,
    string? CreatedByName);

/// <summary>Yorum ekleme yanıtı (<c>POST /cases/{id}/comments</c>).</summary>
public sealed record CaseCommentDto(Guid Id, Guid CaseId, CommentVisibility Visibility, string Body, Guid AuthorUserId, string? AuthorName, DateTime CreatedAt);

/// <summary>
/// Zaman çizelgesi öğesi (yorumlar + olaylar birleşik). <c>type</c>: <c>comment</c> (<c>visibility</c>, <c>body</c> dolu), <c>created</c>,
/// <c>statusChanged</c> (<c>from</c>,<c>to</c> durum; çözüm/kapatmada <c>note</c>), <c>priorityChanged</c> (<c>from</c>,<c>to</c> öncelik),
/// <c>assigned</c> (<c>fromName</c>/<c>toName</c> kullanıcı adları, boş = atanmamış).
/// </summary>
public sealed record TimelineItemDto(
    Guid Id,
    string Type,
    DateTime OccurredAt,
    Guid? ActorUserId,
    string? ActorName,
    CommentVisibility? Visibility,
    string? Body,
    string? From,
    string? To,
    string? FromName,
    string? ToName,
    string? Note);

/// <summary>Ana sayfa sayaçları: aktif (<c>new|open|pending</c>) talepler üzerinden.</summary>
public sealed record CaseSummaryDto(int OpenCount, int OverdueCount, int MineCount, int UnassignedCount);

/// <summary>Ayrıştırılmış liste süzgeci (boş küme = süzgeç yok).</summary>
public sealed record CaseFilter(
    IReadOnlyList<CaseStatus> Statuses,
    IReadOnlyList<CasePriority> Priorities,
    CaseChannel? Channel,
    Guid? AssignedUserId,
    bool Unassigned,
    Guid? AccountId,
    Guid? ContactId,
    SlaState? SlaState);

/// <summary>SLA politikası satırı (kiracıda her zaman dört: low, normal, high, urgent).</summary>
public sealed record SlaPolicyDto(CasePriority Priority, int FirstResponseMinutes, int ResolutionMinutes);

// ---- Raporlar ----

public sealed record StatusCount(CaseStatus Status, int Count);

public sealed record PriorityCount(CasePriority Priority, int Count);

/// <summary>Servis özeti raporu (§4.3). Ortalamalar dakika, 1 ondalık; örneklem yoksa alan yazılmaz.</summary>
public sealed record ServiceSummaryReportDto(
    DateOnly From,
    DateOnly To,
    int TotalCount,
    int ResolvedCount,
    IReadOnlyList<StatusCount> ByStatus,
    IReadOnlyList<PriorityCount> ByPriority,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    int SlaBreachedCount,
    double SlaBreachRate);

/// <summary>Temsilci bazlı rapor satırı; atanmamış talepler <c>assignedUserId</c>/<c>assignedUserName</c> olmadan tek satırdır.</summary>
public sealed record AssigneeReportRowDto(
    Guid? AssignedUserId,
    string? AssignedUserName,
    int TotalCount,
    int OpenCount,
    int ResolvedCount,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    int SlaBreachedCount);

/// <summary>Depo çıktısı: kohort (createdAt aralıkta, silinmemiş) özet ham toplamları.</summary>
public sealed record ServiceSummaryTotals(
    int TotalCount,
    int ResolvedCount,
    IReadOnlyDictionary<CaseStatus, int> ByStatus,
    IReadOnlyDictionary<CasePriority, int> ByPriority,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    int SlaBreachedCount);

/// <summary>Depo çıktısı: temsilci başına ham toplamlar (ad çözümü handler'da).</summary>
public sealed record AssigneeTotals(
    Guid? AssignedUserId,
    int TotalCount,
    int OpenCount,
    int ResolvedCount,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    int SlaBreachedCount);
