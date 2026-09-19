using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sense.Crm.Modules.Marketing.Domain;
using Sense.Crm.Modules.Marketing.Domain.Campaigns;
using Sense.Crm.Modules.Marketing.Domain.Members;

namespace Sense.Crm.Modules.Marketing.Infrastructure.Persistence;

// Tüm sorgular ModuleDbContext'in "Tenant" ve "SoftDelete" global filtreleri altında çalışır.

public sealed class CampaignRepository(MarketingDbContext db) : ICampaignRepository
{
    public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct) => db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct);

    public void Add(Campaign campaign) => db.Campaigns.Add(campaign);

    public void Remove(Campaign campaign) => db.Campaigns.Remove(campaign);
}

public sealed class CampaignMemberRepository(MarketingDbContext db) : ICampaignMemberRepository
{
    public async Task LockCampaignMembersAsync(Guid campaignId, CancellationToken ct)
    {
        // Kampanya başına transaction düzeyinde advisory lock (hashtextextended: 64 bit). Kilit, UnitOfWork transaction'ının
        // commit/rollback'ine kadar tutulur; sonraki ön kontrol sorgusu (READ COMMITTED) önceki yazarın işlenmiş satırlarını görür.
        var key = "marketing.campaign_members:" + campaignId.ToString("N");
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<Guid>> GetExistingMemberIdsAsync(Guid campaignId, CampaignMemberType type, IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var ids = memberIds.ToArray();
        var existing = await db.CampaignMembers.AsNoTracking()
            .Where(m => m.CampaignId == campaignId && m.MemberType == type && ids.Contains(m.MemberId))
            .Select(m => m.MemberId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return existing.ToHashSet();
    }

    public async Task<IReadOnlyList<CampaignMember>> GetByIdsAsync(Guid campaignId, IReadOnlyCollection<Guid> membershipIds, CancellationToken ct)
    {
        var ids = membershipIds.ToArray();
        return await db.CampaignMembers
            .Where(m => m.CampaignId == campaignId && ids.Contains(m.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<Guid>> AddRangeIgnoringDuplicatesAsync(IReadOnlyList<CampaignMember> members, CancellationToken ct)
    {
        db.CampaignMembers.AddRange(members);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return members.Select(m => m.Id).ToHashSet();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Eşzamanlı istek aynı üyeyi önce ekledi. Açık transaction içinde EF, başarısız SaveChanges'ı savepoint'e geri alır (transaction
            // kullanılabilir kalır); bekleyen ekleme + denetim kayıtları bırakılır ve yalnız hâlâ olmayan üyelikler bir kez yeniden denenir.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            var remaining = new List<CampaignMember>();
            foreach (var group in members.GroupBy(m => (m.CampaignId, m.MemberType)))
            {
                var existing = await GetExistingMemberIdsAsync(group.Key.CampaignId, group.Key.MemberType, group.Select(m => m.MemberId).ToList(), ct).ConfigureAwait(false);
                remaining.AddRange(group.Where(m => !existing.Contains(m.MemberId)));
            }

            if (remaining.Count == 0)
            {
                return new HashSet<Guid>();
            }

            db.CampaignMembers.AddRange(remaining);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return remaining.Select(m => m.Id).ToHashSet();
        }
    }

    public void Remove(CampaignMember member) => db.CampaignMembers.Remove(member);

    public async Task<IReadOnlyList<CampaignMember>> GetOpenLeadMembershipsAsync(Guid leadId, CancellationToken ct) =>
        await db.CampaignMembers
            .Where(m => m.MemberType == CampaignMemberType.Lead
                && m.MemberId == leadId
                && m.Status != CampaignMemberStatus.Converted
                && db.Campaigns.Any(c => c.Id == m.CampaignId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
