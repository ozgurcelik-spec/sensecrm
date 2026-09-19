using Crm.Modules.Marketing.Domain.Campaigns;
using Crm.Modules.Marketing.Domain.Members;

namespace Crm.Modules.Marketing.Application;

// HTTP sözleşmesi (docs/plan/m6c-pazarlama.md): alanlar camelCase serileştirilir, null alanlar yazılmaz, enum'lar camelCase string,
// "yalnız tarih" alanlar YYYY-MM-DD, tarih-saatler ISO 8601 UTC.

/// <summary>
/// Kampanya yanıtı (liste ve detayda aynı). <see cref="MemberCount"/> kampanyanın tüm üyeleridir (liste için sayfadaki kampanyalar tek
/// toplama sorgusuyla sayılır); <see cref="OwnerName"/> <c>IMemberLookup</c> ile çözülür (pasif üyeler dahil).
/// </summary>
public sealed record CampaignDto(
    Guid Id,
    string Name,
    CampaignType Type,
    CampaignStatus Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Currency,
    decimal? Budget,
    decimal? ExpectedRevenue,
    decimal? ActualCost,
    string? Description,
    Guid OwnerUserId,
    string? OwnerName,
    int MemberCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>
/// Üye satırı. <see cref="Id"/> üyelik satırının kimliğidir (kayıt kimliği <see cref="MemberId"/>). <see cref="MemberName"/> Sales
/// lead/kişi tam adıdır; kayıt silinmiş/çözülemezse yazılmaz ve <see cref="MemberMissing"/> true olur (üyelik yine listelenir).
/// </summary>
public sealed record CampaignMemberDto(
    Guid Id,
    CampaignMemberType MemberType,
    Guid MemberId,
    string? MemberName,
    bool MemberMissing,
    CampaignMemberStatus Status,
    DateTime AddedAt,
    DateTime StatusChangedAt,
    Guid? AddedByUserId,
    string? AddedByName);

/// <summary>Bir lead/kişinin kampanya üyelikleri (<c>GET /campaigns/by-member</c>): yalnız silinmemiş kampanyalar.</summary>
public sealed record RecordCampaignDto(
    Guid CampaignId,
    string CampaignName,
    CampaignType CampaignType,
    CampaignStatus CampaignStatus,
    Guid MembershipId,
    CampaignMemberStatus MemberStatus,
    DateTime AddedAt);

/// <summary>Toplu ekleme sonucu (200). Zaten üye olanlar hata değil <see cref="AlreadyMemberCount"/>'a girer.</summary>
public sealed record AddMembersResult(int AddedCount, int AlreadyMemberCount, IReadOnlyList<SkippedMember> Skipped);

/// <summary>Eklenmeyen kayıt ve nedeni (<c>not_found</c>, <c>lead_converted</c>).</summary>
public sealed record SkippedMember(Guid MemberId, string Reason);

/// <summary>Toplu durum güncelleme sonucu: değişen satırlar ve <c>converted</c> kilitli (atlanan) satırlar.</summary>
public sealed record SetMemberStatusResult(int UpdatedCount, int SkippedCount);

public sealed record RemoveMembersResult(int RemovedCount);

/// <summary>Beş üye durumunun sayısı (her zaman beşi de vardır, 0 dahil).</summary>
public sealed record MemberStatusCounts(int Added, int Sent, int Responded, int Converted, int Unsubscribed);

/// <summary>Kampanya metrikleri (hesaplanır, saklanmaz). Tanımlar <see cref="Domain.Metrics.MarketingMetrics"/>'tedir.</summary>
public sealed record CampaignMetricsDto(
    Guid CampaignId,
    int MemberCount,
    int LeadCount,
    int ContactCount,
    MemberStatusCounts StatusCounts,
    int ContactedCount,
    int ResponseCount,
    decimal ResponseRate,
    int ConvertedCount,
    decimal ConversionRate,
    string Currency,
    decimal? CostPerLead);

/// <summary>Kampanya listesi filtreleri (sözleşme: <c>q, type, status, ownerUserId, startFrom, startTo</c>); <c>q</c> sayfalama girdisindedir.</summary>
public sealed record CampaignFilter(
    IReadOnlyList<CampaignType> Types,
    IReadOnlyList<CampaignStatus> Statuses,
    Guid? OwnerUserId,
    DateOnly? StartFrom,
    DateOnly? StartTo);

/// <summary>Üye listesi filtreleri (sözleşme: <c>memberType, status</c>).</summary>
public sealed record MemberFilter(CampaignMemberType? MemberType, IReadOnlyList<CampaignMemberStatus> Statuses);

/// <summary>Pazarlama raporu satırı: durum dağılımı.</summary>
public sealed record CampaignStatusReportRow(CampaignStatus Status, int Count);

/// <summary>Pazarlama raporu satırı: tür dağılımı (bütçe/maliyet/üye/dönüşen).</summary>
public sealed record CampaignTypeReportRow(CampaignType Type, int Count, decimal Budget, decimal ActualCost, int MemberCount, int ConvertedCount);

/// <summary>Rapor toplamları: oranlar kampanya oranlarının ortalaması değil <b>toplam sayılar</b> üzerinden hesaplanır.</summary>
public sealed record MarketingTotalsDto(
    decimal Budget,
    decimal ExpectedRevenue,
    decimal ActualCost,
    int MemberCount,
    int LeadCount,
    int ContactedCount,
    int ResponseCount,
    decimal ResponseRate,
    int ConvertedCount,
    decimal ConversionRate,
    decimal? CostPerLead);

/// <summary>Rapordaki en iyi kampanyalar satırı.</summary>
public sealed record TopCampaignDto(
    Guid Id,
    string Name,
    CampaignType Type,
    CampaignStatus Status,
    decimal? Budget,
    decimal? ActualCost,
    int MemberCount,
    decimal ResponseRate,
    int ConvertedCount);

/// <summary>Pazarlama özeti (<c>GET /reports/marketing/summary</c>).</summary>
public sealed record MarketingSummaryDto(
    DateOnly From,
    DateOnly To,
    int CampaignCount,
    IReadOnlyList<CampaignStatusReportRow> ByStatus,
    IReadOnlyList<CampaignTypeReportRow> ByType,
    MarketingTotalsDto Totals,
    IReadOnlyList<TopCampaignDto> TopCampaigns);
