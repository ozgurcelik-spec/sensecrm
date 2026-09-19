using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Contracts;
using Crm.Modules.Sales.Domain;
using Crm.Modules.Sales.Domain.Leads;
using Crm.Shared.Kernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Crm.Modules.Sales.Infrastructure.Persistence;

/// <summary>
/// <see cref="ILeadOwnerService"/> uygulaması: tüm sorgular <c>SalesDbContext</c>'in kiracı + yumuşak silme filtresi altındadır.
/// Sahip değişimi doğrudan kaydedilir (workflow worker'ı komut pipeline'ı dışında çağırır); denetim kaydı interceptor'dan gelir.
/// </summary>
public sealed class LeadOwnerService(SalesDbContext db, IMemberLookup members, TimeProvider clock) : ILeadOwnerService
{
    public async Task<Result> AssignOwnerAsync(Guid leadId, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken).ConfigureAwait(false);
        if (lead is null)
        {
            return Error.NotFound(ErrorCodes.NotFound);
        }

        if (lead.IsConverted)
        {
            return Error.Conflict(SalesErrors.LeadAlreadyConverted);
        }

        if (!await members.IsActiveMemberAsync(ownerUserId, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation(SalesErrors.OwnerNotMember);
        }

        var assigned = lead.AssignOwner(ownerUserId, clock.GetUtcNow().UtcDateTime);
        if (assigned.IsFailure)
        {
            return assigned;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountOpenLeadsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToArray();
        var counts = await db.Leads.AsNoTracking()
            .Where(l => ids.Contains(l.OwnerUserId) && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified))
            .GroupBy(l => l.OwnerUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToDictionary(id => id, id => counts.FirstOrDefault(c => c.UserId == id)?.Count ?? 0);
    }

    public async Task<IReadOnlyDictionary<Guid, DateTime>> GetLastAssignedAtAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToArray();
        var rows = await db.Leads.AsNoTracking()
            .Where(l => ids.Contains(l.OwnerUserId))
            .GroupBy(l => l.OwnerUserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(l => l.OwnerAssignedAt ?? l.CreatedAt) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.UserId, r => r.Last);
    }
}
