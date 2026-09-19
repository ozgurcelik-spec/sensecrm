using Crm.Modules.Marketing.Application;
using Crm.Modules.Marketing.Domain;
using Crm.Modules.Marketing.Domain.Campaigns;
using Crm.Modules.Marketing.Domain.Members;
using Crm.Modules.Marketing.Domain.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Crm.Modules.Marketing.Infrastructure.Persistence;

/// <summary>
/// Pazarlama raporu (kiracı + yumuşak silme filtresi altında). Kampanya aralığa <c>startDate ∈ [from, to]</c> (yoksa
/// <c>createdAt</c> UTC yarı açık <c>[fromUtc, toExclusiveUtc)</c>) ile girer. Üye sayımları <c>campaign_members</c> üzerinde tek
/// gruplama sorgusudur (kampanya başına bir satır); tutarlar ve oranlar kampanya oranlarının ortalaması değil <b>toplam sayılar</b>
/// üzerinden hesaplanır (<see cref="MarketingMetrics"/>).
/// </summary>
public sealed class MarketingReportStore(MarketingDbContext db) : IMarketingReportStore
{
    private static readonly CampaignStatus[] StatusOrder = [CampaignStatus.Planned, CampaignStatus.Active, CampaignStatus.Completed, CampaignStatus.Cancelled];
    private static readonly CampaignType[] TypeOrder = [CampaignType.Email, CampaignType.Event, CampaignType.Webinar, CampaignType.Advertising, CampaignType.Other];

    public async Task<MarketingSummaryDto> GetSummaryAsync(DateOnly from, DateOnly to, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct)
    {
        var inRange = db.Campaigns.AsNoTracking().Where(c => c.StartDate != null
            ? c.StartDate >= from && c.StartDate <= to
            : c.CreatedAt >= fromUtc && c.CreatedAt < toExclusiveUtc);

        var campaigns = await inRange
            .Select(c => new CampaignRow(c.Id, c.Name, c.Type, c.Status, c.Budget, c.ExpectedRevenue, c.ActualCost))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stats = await db.CampaignMembers.AsNoTracking()
            .Where(m => inRange.Select(c => c.Id).Contains(m.CampaignId))
            .GroupBy(m => m.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Leads = g.Count(m => m.MemberType == CampaignMemberType.Lead),
                Contacts = g.Count(m => m.MemberType == CampaignMemberType.Contact),
                Added = g.Count(m => m.Status == CampaignMemberStatus.Added),
                Sent = g.Count(m => m.Status == CampaignMemberStatus.Sent),
                Responded = g.Count(m => m.Status == CampaignMemberStatus.Responded),
                Converted = g.Count(m => m.Status == CampaignMemberStatus.Converted),
                Unsubscribed = g.Count(m => m.Status == CampaignMemberStatus.Unsubscribed),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var countsById = stats.ToDictionary(
            s => s.CampaignId,
            s => new MemberCounts(s.Leads, s.Contacts, s.Added, s.Sent, s.Responded, s.Converted, s.Unsubscribed));
        MemberCounts CountsOf(Guid id) => countsById.GetValueOrDefault(id);

        var byStatus = StatusOrder
            .Select(status => new CampaignStatusReportRow(status, campaigns.Count(c => c.Status == status)))
            .ToList();

        var byType = TypeOrder.Select(type =>
        {
            var ofType = campaigns.Where(c => c.Type == type).ToList();
            return new CampaignTypeReportRow(
                type,
                ofType.Count,
                ofType.Sum(c => c.Budget ?? 0m),
                ofType.Sum(c => c.ActualCost ?? 0m),
                ofType.Sum(c => CountsOf(c.Id).MemberCount),
                ofType.Sum(c => CountsOf(c.Id).Converted));
        }).ToList();

        var totalCounts = campaigns.Aggregate(default(MemberCounts), (sum, c) => sum + CountsOf(c.Id));
        var totalCost = campaigns.Sum(c => c.ActualCost ?? 0m);
        var totals = new MarketingTotalsDto(
            campaigns.Sum(c => c.Budget ?? 0m),
            campaigns.Sum(c => c.ExpectedRevenue ?? 0m),
            totalCost,
            totalCounts.MemberCount,
            totalCounts.LeadCount,
            totalCounts.ContactedCount,
            totalCounts.ResponseCount,
            MarketingMetrics.ResponseRate(totalCounts),
            totalCounts.Converted,
            MarketingMetrics.ConversionRate(totalCounts),
            MarketingMetrics.CostPerLead(totalCost, totalCounts.LeadCount));

        var top = campaigns
            .Select(c => (Campaign: c, Counts: CountsOf(c.Id)))
            .OrderByDescending(x => x.Counts.Converted)
            .ThenByDescending(x => x.Counts.MemberCount)
            .ThenBy(x => x.Campaign.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Campaign.Id)
            .Take(MarketingLimits.TopCampaignCount)
            .Select(x => new TopCampaignDto(
                x.Campaign.Id,
                x.Campaign.Name,
                x.Campaign.Type,
                x.Campaign.Status,
                x.Campaign.Budget,
                x.Campaign.ActualCost,
                x.Counts.MemberCount,
                MarketingMetrics.ResponseRate(x.Counts),
                x.Counts.Converted))
            .ToList();

        return new MarketingSummaryDto(from, to, campaigns.Count, byStatus, byType, totals, top);
    }

    private sealed record CampaignRow(Guid Id, string Name, CampaignType Type, CampaignStatus Status, decimal? Budget, decimal? ExpectedRevenue, decimal? ActualCost);
}
