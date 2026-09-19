using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Sales.Application;
using Sense.Crm.Modules.Sales.Application.Reports;
using Sense.Crm.Modules.Sales.Domain.Leads;
using Sense.Crm.Modules.Sales.Domain.Pipelines;

namespace Sense.Crm.Modules.Sales.Infrastructure.Persistence;

/// <summary>
/// Satış raporu toplama sorguları (kiracı + yumuşak silme filtresi altında). Toplamlar veritabanında yapılır; kapanış günleri
/// kiracı saat diliminde (<c>AT TIME ZONE</c>) yerel güne indirilir, ay/hafta dönemlerine bölme ve boş dönem doldurma uygulama
/// katmanındadır (saf hesap).
/// </summary>
public sealed class SalesReportStore(SalesDbContext db) : ISalesReportStore
{
    public async Task<FunnelDto?> GetFunnelAsync(Guid pipelineId, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().Include(p => p.Stages).FirstOrDefaultAsync(p => p.Id == pipelineId, ct);
        if (pipeline is null)
        {
            return null;
        }

        var totals = (await db.Deals.AsNoTracking()
                .Where(d => d.PipelineId == pipelineId)
                .GroupBy(d => d.StageId)
                .Select(g => new { StageId = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount ?? 0m) })
                .ToListAsync(ct))
            .ToDictionary(t => t.StageId);

        var stages = pipeline.Stages
            .OrderBy(s => s.Order)
            .Select(s =>
            {
                totals.TryGetValue(s.Id, out var total);
                return new FunnelStageDto(s.Id, s.Name, s.Kind, s.Order, s.Probability, total?.Count ?? 0, total?.Total ?? 0m);
            })
            .ToList();
        return new FunnelDto(pipeline.Id, stages);
    }

    public async Task<IReadOnlyList<ClosedDealDayRow>> GetClosedDealsByDayAsync(DateTime fromUtc, DateTime toExclusiveUtc, string timeZoneId, CancellationToken ct)
    {
        var rows = await (from d in db.Deals.AsNoTracking()
                          join s in db.PipelineStages.AsNoTracking() on d.StageId equals s.Id
                          where d.ClosedAt != null && d.ClosedAt >= fromUtc && d.ClosedAt < toExclusiveUtc && s.Kind != StageKind.Open
                          group d by new { Day = SalesDbFunctions.ToLocalTimestamp(timeZoneId, d.ClosedAt!.Value).Date, s.Kind } into g
                          select new { g.Key.Day, g.Key.Kind, Count = g.Count(), Amount = g.Sum(x => x.Amount ?? 0m) })
            .ToListAsync(ct);
        return rows.Select(r => new ClosedDealDayRow(DateOnly.FromDateTime(r.Day), r.Kind, r.Count, r.Amount)).ToList();
    }

    public async Task<IReadOnlyList<LeadSourceReportRow>> GetLeadsBySourceAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct)
    {
        var rows = await db.Leads.AsNoTracking()
            .Where(l => l.CreatedAt >= fromUtc && l.CreatedAt < toExclusiveUtc)
            .GroupBy(l => l.Source)
            .Select(g => new { Source = g.Key, Count = g.Count(), Converted = g.Count(x => x.Status == LeadStatus.Converted) })
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Source)
            .Select(r => new LeadSourceReportRow(r.Source, r.Count, r.Converted))
            .ToList();
    }

    public async Task<IReadOnlyList<OwnerTotals>> GetOwnerTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct)
    {
        var open = await (from d in db.Deals.AsNoTracking()
                          join s in db.PipelineStages.AsNoTracking() on d.StageId equals s.Id
                          where s.Kind == StageKind.Open
                          group d by d.OwnerUserId into g
                          select new { Owner = g.Key, Count = g.Count(), Amount = g.Sum(x => x.Amount ?? 0m) })
            .ToListAsync(ct);

        var won = await (from d in db.Deals.AsNoTracking()
                         join s in db.PipelineStages.AsNoTracking() on d.StageId equals s.Id
                         where s.Kind == StageKind.Won && d.ClosedAt != null && d.ClosedAt >= fromUtc && d.ClosedAt < toExclusiveUtc
                         group d by d.OwnerUserId into g
                         select new { Owner = g.Key, Count = g.Count(), Amount = g.Sum(x => x.Amount ?? 0m) })
            .ToListAsync(ct);

        var leads = await db.Leads.AsNoTracking()
            .Where(l => l.CreatedAt >= fromUtc && l.CreatedAt < toExclusiveUtc)
            .GroupBy(l => l.OwnerUserId)
            .Select(g => new { Owner = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var owners = open.Select(o => o.Owner).Concat(won.Select(w => w.Owner)).Concat(leads.Select(l => l.Owner)).Distinct();
        return owners.Select(owner =>
        {
            var o = open.FirstOrDefault(x => x.Owner == owner);
            var w = won.FirstOrDefault(x => x.Owner == owner);
            var l = leads.FirstOrDefault(x => x.Owner == owner);
            return new OwnerTotals(owner, o?.Count ?? 0, o?.Amount ?? 0m, w?.Count ?? 0, w?.Amount ?? 0m, l?.Count ?? 0);
        }).ToList();
    }
}
