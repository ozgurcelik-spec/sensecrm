using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Modules.Sales.Domain.Leads;

namespace Sense.Crm.Modules.Sales.Infrastructure.Persistence;

/// <summary><see cref="ILeadStatusLookup"/> uygulaması: sorgu <c>SalesDbContext</c>'in kiracı + yumuşak silme filtresi altındadır (tek sorgu).</summary>
public sealed class LeadStatusLookup(SalesDbContext db) : ILeadStatusLookup
{
    public async Task<IReadOnlySet<Guid>> GetConvertedLeadIdsAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct = default)
    {
        if (leadIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = leadIds.Distinct().ToArray();
        var converted = await db.Leads.AsNoTracking()
            .Where(l => ids.Contains(l.Id) && l.Status == LeadStatus.Converted)
            .Select(l => l.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return converted.ToHashSet();
    }
}
