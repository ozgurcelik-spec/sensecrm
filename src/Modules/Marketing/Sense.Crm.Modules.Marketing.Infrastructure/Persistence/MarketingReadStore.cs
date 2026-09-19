using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Marketing.Application;
using Sense.Crm.Modules.Marketing.Application.Campaigns;
using Sense.Crm.Modules.Marketing.Domain;
using Sense.Crm.Modules.Marketing.Domain.Campaigns;
using Sense.Crm.Modules.Marketing.Domain.Members;
using Sense.Crm.Modules.Marketing.Domain.Metrics;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Shared.Contracts.Paging;

namespace Sense.Crm.Modules.Marketing.Infrastructure.Persistence;

/// <summary>
/// Sorgu projeksiyonları (kiracı + yumuşak silme filtresi altında). Arama <c>ILIKE</c> + kaçışlı parametre; sıralama yalnız beyaz
/// listedeki alanlarda (bilinmeyen alan yok sayılır) ve her zaman <c>createdAt</c> + <c>Id</c> ile kararlı; boş tarih/tutar alanları her
/// iki yönde de sonda. Kampanya başına üye sayısı, sahip/üye/ekleyen adları sayfa başına toplu çözülür (N+1 yok).
/// </summary>
public sealed class MarketingReadStore(MarketingDbContext db, IMemberLookup members, IRecordLookup records) : IMarketingReadStore
{
    public async Task<PagedResult<CampaignDto>> ListCampaignsAsync(PagedQuery paging, CampaignFilter filter, CancellationToken ct)
    {
        var query = db.Campaigns.AsNoTracking();
        if (filter.Types.Count > 0)
        {
            var types = filter.Types.ToArray();
            query = query.Where(c => types.Contains(c.Type));
        }

        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses.ToArray();
            query = query.Where(c => statuses.Contains(c.Status));
        }

        if (filter.OwnerUserId is { } owner)
        {
            query = query.Where(c => c.OwnerUserId == owner);
        }

        if (filter.StartFrom is { } from)
        {
            query = query.Where(c => c.StartDate != null && c.StartDate >= from);
        }

        if (filter.StartTo is { } to)
        {
            query = query.Where(c => c.StartDate != null && c.StartDate <= to);
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(c => EF.Functions.ILike(c.Name, q, SearchPattern.Escape)
                || (c.Description != null && EF.Functions.ILike(c.Description, q, SearchPattern.Escape)));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await OrderCampaigns(query, paging.SortClauses).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<CampaignDto>(await MapAsync(rows, ct).ConfigureAwait(false), paging.Page, paging.PageSize, total);
    }

    public async Task<CampaignDto?> GetCampaignAsync(Guid id, CancellationToken ct)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
        return campaign is null ? null : (await MapAsync([campaign], ct).ConfigureAwait(false))[0];
    }

    public Task<bool> CampaignExistsAsync(Guid id, CancellationToken ct) => db.Campaigns.AsNoTracking().AnyAsync(c => c.Id == id, ct);

    public async Task<PagedResult<CampaignMemberDto>> ListMembersAsync(Guid campaignId, PagedQuery paging, MemberFilter filter, CancellationToken ct)
    {
        var query = db.CampaignMembers.AsNoTracking().Where(m => m.CampaignId == campaignId);
        if (filter.MemberType is { } type)
        {
            query = query.Where(m => m.MemberType == type);
        }

        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses.ToArray();
            query = query.Where(m => statuses.Contains(m.Status));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await OrderMembers(query, paging.SortClauses).ThenBy(m => m.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<CampaignMemberDto>(await MapMembersAsync(rows, ct).ConfigureAwait(false), paging.Page, paging.PageSize, total);
    }

    public async Task<MemberCounts> GetMemberCountsAsync(Guid campaignId, CancellationToken ct)
    {
        var rows = await db.CampaignMembers.AsNoTracking()
            .Where(m => m.CampaignId == campaignId)
            .GroupBy(m => new { m.MemberType, m.Status })
            .Select(g => new { g.Key.MemberType, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var counts = default(MemberCounts);
        foreach (var row in rows)
        {
            counts += new MemberCounts(
                row.MemberType == CampaignMemberType.Lead ? row.Count : 0,
                row.MemberType == CampaignMemberType.Contact ? row.Count : 0,
                row.Status == CampaignMemberStatus.Added ? row.Count : 0,
                row.Status == CampaignMemberStatus.Sent ? row.Count : 0,
                row.Status == CampaignMemberStatus.Responded ? row.Count : 0,
                row.Status == CampaignMemberStatus.Converted ? row.Count : 0,
                row.Status == CampaignMemberStatus.Unsubscribed ? row.Count : 0);
        }

        return counts;
    }

    public async Task<IReadOnlyList<RecordCampaignDto>> ListRecordCampaignsAsync(CampaignMemberType type, Guid memberId, CancellationToken ct) =>
        await (from m in db.CampaignMembers.AsNoTracking()
               join c in db.Campaigns.AsNoTracking() on m.CampaignId equals c.Id
               where m.MemberType == type && m.MemberId == memberId
               orderby m.AddedAt descending, m.Id
               select new RecordCampaignDto(c.Id, c.Name, c.Type, c.Status, m.Id, m.Status, m.AddedAt))
            .Take(MarketingLimits.MaxRecordCampaigns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>createdAt</c> azalan. Boş değerler her zaman sonda.</summary>
    private static IOrderedQueryable<Campaign> OrderCampaigns(IQueryable<Campaign> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<Campaign>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "name":
                    ordered = Add(ordered, query, c => c.Name, clause.Descending);
                    break;

                case "type":
                    ordered = Add(ordered, query, c => c.Type == CampaignType.Email ? 0 : c.Type == CampaignType.Event ? 1 : c.Type == CampaignType.Webinar ? 2 : c.Type == CampaignType.Advertising ? 3 : 4, clause.Descending);
                    break;

                case "status":
                    ordered = Add(ordered, query, c => c.Status == CampaignStatus.Planned ? 0 : c.Status == CampaignStatus.Active ? 1 : c.Status == CampaignStatus.Completed ? 2 : 3, clause.Descending);
                    break;

                case "startdate":
                    ordered = AddNullsLast(ordered, query, c => c.StartDate == null, c => c.StartDate, clause.Descending);
                    break;

                case "enddate":
                    ordered = AddNullsLast(ordered, query, c => c.EndDate == null, c => c.EndDate, clause.Descending);
                    break;

                case "budget":
                    ordered = AddNullsLast(ordered, query, c => c.Budget == null, c => c.Budget, clause.Descending);
                    break;

                case "actualcost":
                    ordered = AddNullsLast(ordered, query, c => c.ActualCost == null, c => c.ActualCost, clause.Descending);
                    break;

                case "createdat":
                    ordered = Add(ordered, query, c => c.CreatedAt, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderByDescending(c => c.CreatedAt);
    }

    private static IOrderedQueryable<CampaignMember> OrderMembers(IQueryable<CampaignMember> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<CampaignMember>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "addedat":
                    ordered = Add(ordered, query, m => m.AddedAt, clause.Descending);
                    break;

                case "statuschangedat":
                    ordered = Add(ordered, query, m => m.StatusChangedAt, clause.Descending);
                    break;

                case "status":
                    ordered = Add(
                        ordered,
                        query,
                        m => m.Status == CampaignMemberStatus.Added ? 0 : m.Status == CampaignMemberStatus.Sent ? 1 : m.Status == CampaignMemberStatus.Responded ? 2 : m.Status == CampaignMemberStatus.Converted ? 3 : 4,
                        clause.Descending);
                    break;

                case "membertype":
                    ordered = Add(ordered, query, m => m.MemberType == CampaignMemberType.Lead ? 0 : 1, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderByDescending(m => m.AddedAt);
    }

    private static IOrderedQueryable<T> Add<T, TKey>(IOrderedQueryable<T>? ordered, IQueryable<T> query, Expression<Func<T, TKey>> key, bool descending) =>
        ordered is null
            ? (descending ? query.OrderByDescending(key) : query.OrderBy(key))
            : (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));

    private static IOrderedQueryable<T> AddNullsLast<T, TKey>(
        IOrderedQueryable<T>? ordered,
        IQueryable<T> query,
        Expression<Func<T, bool>> isNull,
        Expression<Func<T, TKey>> key,
        bool descending)
    {
        var first = ordered is null ? query.OrderBy(isNull) : ordered.ThenBy(isNull);
        return descending ? first.ThenByDescending(key) : first.ThenBy(key);
    }

    private async Task<IReadOnlyList<CampaignDto>> MapAsync(IReadOnlyList<Campaign> campaigns, CancellationToken ct)
    {
        var campaignIds = campaigns.Select(c => c.Id).ToArray();
        var counts = campaignIds.Length == 0
            ? new Dictionary<Guid, int>()
            : await db.CampaignMembers.AsNoTracking()
                .Where(m => campaignIds.Contains(m.CampaignId))
                .GroupBy(m => m.CampaignId)
                .Select(g => new { CampaignId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CampaignId, x => x.Count, ct)
                .ConfigureAwait(false);

        var ownerIds = campaigns.Select(c => c.OwnerUserId).Distinct().ToList();
        var owners = ownerIds.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(ownerIds, ct).ConfigureAwait(false);

        return campaigns.Select(c => new CampaignDto(
            c.Id,
            c.Name,
            c.Type,
            c.Status,
            c.StartDate,
            c.EndDate,
            c.Currency,
            c.Budget,
            c.ExpectedRevenue,
            c.ActualCost,
            c.Description,
            c.OwnerUserId,
            owners.GetValueOrDefault(c.OwnerUserId),
            counts.GetValueOrDefault(c.Id),
            c.CreatedAt,
            c.ModifiedDate)).ToList();
    }

    private async Task<IReadOnlyList<CampaignMemberDto>> MapMembersAsync(IReadOnlyList<CampaignMember> rows, CancellationToken ct)
    {
        var refs = rows.Select(m => new RecordRef(ToRecordType(m.MemberType), m.MemberId)).Distinct().ToList();
        var names = refs.Count == 0 ? new Dictionary<RecordRef, string>() : await records.GetDisplayNamesAsync(refs, ct).ConfigureAwait(false);

        var userIds = rows.Where(m => m.AddedByUserId is not null).Select(m => m.AddedByUserId!.Value).Distinct().ToList();
        var users = userIds.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(userIds, ct).ConfigureAwait(false);

        return rows.Select(m =>
        {
            var name = names.GetValueOrDefault(new RecordRef(ToRecordType(m.MemberType), m.MemberId));
            return new CampaignMemberDto(
                m.Id,
                m.MemberType,
                m.MemberId,
                name,
                name is null,
                m.Status,
                m.AddedAt,
                m.StatusChangedAt,
                m.AddedByUserId,
                m.AddedByUserId is { } by ? users.GetValueOrDefault(by) : null);
        }).ToList();
    }

    private static RecordType ToRecordType(CampaignMemberType type) => MemberTypeMapping.ToRecordType(type);
}

/// <summary>ILIKE arama deseni: kullanıcı girdisindeki joker karakterler kaçışlanır; desen her zaman parametre olarak gider.</summary>
internal static class SearchPattern
{
    public const string Escape = "\\";

    public static string? Contains(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var escaped = trimmed.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }
}
