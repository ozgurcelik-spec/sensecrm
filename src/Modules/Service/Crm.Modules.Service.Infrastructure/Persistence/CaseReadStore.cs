using System.Linq.Expressions;
using Crm.Modules.Identity.Contracts;
using Crm.Modules.Sales.Contracts;
using Crm.Modules.Service.Application;
using Crm.Modules.Service.Domain.Cases;
using Crm.Shared.Contracts.Paging;
using Microsoft.EntityFrameworkCore;

namespace Crm.Modules.Service.Infrastructure.Persistence;

/// <summary>
/// SLA okuma-anı tanımının SQL karşılığı (<see cref="CaseSlaCalculator.Evaluate"/> ile aynı): yanıttaki <c>slaState</c>, liste süzgeci ve
/// özet sayaçları bu ifadelerle aynı kümeyi verir. <c>now</c> sorgu parametresidir.
/// </summary>
internal static class CaseSlaQuery
{
    public static Expression<Func<Case, bool>> Active { get; } =
        c => c.Status == CaseStatus.New || c.Status == CaseStatus.Open || c.Status == CaseStatus.Pending;

    public static Expression<Func<Case, bool>> Breached(DateTime now) =>
        c => (c.FirstResponseAt ?? now) > c.FirstResponseDueAt || (c.ResolvedAt ?? now) > c.DueAt;

    public static Expression<Func<Case, bool>> AtRisk(DateTime now) =>
        c => !((c.FirstResponseAt ?? now) > c.FirstResponseDueAt || (c.ResolvedAt ?? now) > c.DueAt)
            && (c.Status == CaseStatus.New || c.Status == CaseStatus.Open || c.Status == CaseStatus.Pending)
            && ((c.FirstResponseAt == null && now >= c.FirstResponseWarnAt) || now >= c.ResolutionWarnAt);

    public static Expression<Func<Case, bool>> Ok(DateTime now) =>
        c => !((c.FirstResponseAt ?? now) > c.FirstResponseDueAt || (c.ResolvedAt ?? now) > c.DueAt)
            && !((c.Status == CaseStatus.New || c.Status == CaseStatus.Open || c.Status == CaseStatus.Pending)
                && ((c.FirstResponseAt == null && now >= c.FirstResponseWarnAt) || now >= c.ResolutionWarnAt));
}

/// <summary>
/// Sorgu projeksiyonları (kiracı + yumuşak silme filtresi altında). Arama <c>ILIKE</c> + kaçışlı parametre; sıralama yalnız beyaz listedeki
/// alanlarda (bilinmeyen alan yok sayılır), <c>status</c>/<c>priority</c> metin değil enum sırasıyla, her zaman <c>Id</c> ile kararlı. Adlar
/// (üye, firma, kişi) sayfa başına toplu çözülür (N+1 yok).
/// </summary>
public sealed class CaseReadStore(ServiceDbContext db, IMemberLookup members, IRecordLookup records) : ICaseReadStore
{
    private const string CommentType = "comment";

    public async Task<PagedResult<CaseListItemDto>> ListAsync(PagedQuery paging, CaseFilter filter, DateTime nowUtc, CancellationToken ct)
    {
        var query = db.Cases.AsNoTracking();
        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses.ToArray();
            query = query.Where(c => statuses.Contains(c.Status));
        }

        if (filter.Priorities.Count > 0)
        {
            var priorities = filter.Priorities.ToArray();
            query = query.Where(c => priorities.Contains(c.Priority));
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(c => c.Channel == channel);
        }

        if (filter.AssignedUserId is { } assignee)
        {
            query = query.Where(c => c.AssignedUserId == assignee);
        }

        if (filter.Unassigned)
        {
            query = query.Where(c => c.AssignedUserId == null);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(c => c.AccountId == accountId);
        }

        if (filter.ContactId is { } contactId)
        {
            query = query.Where(c => c.ContactId == contactId);
        }

        query = filter.SlaState switch
        {
            SlaState.Breached => query.Where(CaseSlaQuery.Breached(nowUtc)),
            SlaState.AtRisk => query.Where(CaseSlaQuery.AtRisk(nowUtc)),
            SlaState.Ok => query.Where(CaseSlaQuery.Ok(nowUtc)),
            _ => query,
        };

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(c => EF.Functions.ILike(c.Number, q, SearchPattern.Escape) || EF.Functions.ILike(c.Subject, q, SearchPattern.Escape));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses).ThenBy(c => c.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        var context = await NameContext.LoadAsync(rows, members, records, ct).ConfigureAwait(false);
        return new PagedResult<CaseListItemDto>(rows.Select(c => context.ToListItem(c, nowUtc)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<CaseDetailDto?> GetAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        var entity = await db.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return null;
        }

        var context = await NameContext.LoadAsync([entity], members, records, ct).ConfigureAwait(false);
        return context.ToDetail(entity, nowUtc);
    }

    public async Task<CaseSummaryDto> GetSummaryAsync(Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var active = db.Cases.AsNoTracking().Where(CaseSlaQuery.Active);
        var open = await active.CountAsync(ct).ConfigureAwait(false);
        var overdue = await active.Where(CaseSlaQuery.Breached(nowUtc)).CountAsync(ct).ConfigureAwait(false);
        var mine = await active.CountAsync(c => c.AssignedUserId == userId, ct).ConfigureAwait(false);
        var unassigned = await active.CountAsync(c => c.AssignedUserId == null, ct).ConfigureAwait(false);
        return new CaseSummaryDto(open, overdue, mine, unassigned);
    }

    public async Task<PagedResult<TimelineItemDto>?> GetTimelineAsync(Guid caseId, PagedQuery paging, CancellationToken ct)
    {
        if (!await db.Cases.AsNoTracking().AnyAsync(c => c.Id == caseId, ct).ConfigureAwait(false))
        {
            return null;
        }

        // Yorumlar + olaylar tek UNION ALL projeksiyonu: tüm sütunlar metin/skaler (enum → metin CASE ile), böylece iki kolun türleri eşleşir.
        var comments = db.CaseComments.AsNoTracking()
            .Where(c => c.CaseId == caseId)
            .Select(c => new TimelineRow
            {
                Id = c.Id,
                Type = CommentType,
                OccurredAt = c.CreatedAt,
                ActorUserId = c.AuthorUserId,
                Visibility = c.Visibility == CommentVisibility.Public ? "public" : "internal",
                Body = c.Body,
                FromValue = null,
                ToValue = null,
                Note = null,
            });
        var events = db.CaseEvents.AsNoTracking()
            .Where(e => e.CaseId == caseId)
            .Select(e => new TimelineRow
            {
                Id = e.Id,
                Type = e.Type == CaseEventType.Created ? "created"
                    : e.Type == CaseEventType.StatusChanged ? "statusChanged"
                    : e.Type == CaseEventType.PriorityChanged ? "priorityChanged"
                    : "assigned",
                OccurredAt = e.CreatedAt,
                ActorUserId = e.ActorUserId,
                Visibility = null,
                Body = null,
                FromValue = e.FromValue,
                ToValue = e.ToValue,
                Note = e.Note,
            });

        var union = comments.Concat(events);
        var total = await union.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await union.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);

        var userIds = rows.Where(r => r.ActorUserId is not null).Select(r => r.ActorUserId!.Value)
            .Concat(rows.Where(r => r.Type == "assigned").SelectMany(r => new[] { r.FromValue, r.ToValue }).Select(ParseId).Where(id => id is not null).Select(id => id!.Value))
            .Distinct()
            .ToList();
        var names = userIds.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(userIds, ct).ConfigureAwait(false);

        var items = rows.Select(r =>
        {
            var isAssigned = r.Type == "assigned";
            var isChange = r.Type is "statusChanged" or "priorityChanged";
            return new TimelineItemDto(
                r.Id,
                r.Type,
                r.OccurredAt,
                r.ActorUserId,
                r.ActorUserId is { } actor ? names.GetValueOrDefault(actor) : null,
                r.Visibility is null ? null : (r.Visibility == "public" ? CommentVisibility.Public : CommentVisibility.Internal),
                r.Body,
                isChange ? r.FromValue : null,
                isChange ? r.ToValue : null,
                isAssigned && ParseId(r.FromValue) is { } from ? names.GetValueOrDefault(from) : null,
                isAssigned && ParseId(r.ToValue) is { } to ? names.GetValueOrDefault(to) : null,
                r.Note);
        }).ToList();
        return new PagedResult<TimelineItemDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<ServiceSummaryTotals> GetSummaryTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateTime nowUtc, CancellationToken ct)
    {
        var cohort = db.Cases.AsNoTracking().Where(c => c.CreatedAt >= fromUtc && c.CreatedAt < toExclusiveUtc);
        var total = await cohort.CountAsync(ct).ConfigureAwait(false);
        var resolved = await cohort.CountAsync(c => c.ResolvedAt != null, ct).ConfigureAwait(false);
        var breached = await cohort.Where(CaseSlaQuery.Breached(nowUtc)).CountAsync(ct).ConfigureAwait(false);
        var byStatus = await cohort.GroupBy(c => c.Status).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        var byPriority = await cohort.GroupBy(c => c.Priority).Select(g => new { Key = g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        var averages = await cohort
            .GroupBy(_ => 1)
            .Select(g => new
            {
                FirstResponse = g.Average(c => c.FirstResponseAt != null ? (double?)(c.FirstResponseAt.Value - c.CreatedAt).TotalMinutes : null),
                Resolution = g.Average(c => c.ResolvedAt != null ? (double?)(c.ResolvedAt.Value - c.CreatedAt).TotalMinutes : null),
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new ServiceSummaryTotals(
            total,
            resolved,
            byStatus.ToDictionary(x => x.Key, x => x.Count),
            byPriority.ToDictionary(x => x.Key, x => x.Count),
            averages?.FirstResponse,
            averages?.Resolution,
            breached);
    }

    public async Task<IReadOnlyList<AssigneeTotals>> GetAssigneeTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateTime nowUtc, CancellationToken ct)
    {
        var cohort = db.Cases.AsNoTracking().Where(c => c.CreatedAt >= fromUtc && c.CreatedAt < toExclusiveUtc);
        var rows = await cohort
            .GroupBy(c => c.AssignedUserId)
            .Select(g => new
            {
                User = g.Key,
                Total = g.Count(),
                Open = g.Count(c => c.Status == CaseStatus.New || c.Status == CaseStatus.Open || c.Status == CaseStatus.Pending),
                Resolved = g.Count(c => c.ResolvedAt != null),
                Breached = g.Count(c => (c.FirstResponseAt ?? nowUtc) > c.FirstResponseDueAt || (c.ResolvedAt ?? nowUtc) > c.DueAt),
                FirstResponse = g.Average(c => c.FirstResponseAt != null ? (double?)(c.FirstResponseAt.Value - c.CreatedAt).TotalMinutes : null),
                Resolution = g.Average(c => c.ResolvedAt != null ? (double?)(c.ResolvedAt.Value - c.CreatedAt).TotalMinutes : null),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(r => new AssigneeTotals(r.User, r.Total, r.Open, r.Resolved, r.FirstResponse, r.Resolution, r.Breached)).ToList();
    }

    private static Guid? ParseId(string? text) => Guid.TryParse(text, out var id) ? id : null;

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>createdAt</c> azalan. Durum/öncelik enum sırasıyla.</summary>
    private static IOrderedQueryable<Case> Order(IQueryable<Case> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<Case>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "number":
                    ordered = Add(ordered, query, c => c.Number, clause.Descending);
                    break;

                case "subject":
                    ordered = Add(ordered, query, c => c.Subject, clause.Descending);
                    break;

                case "status":
                    ordered = Add(ordered, query, c => c.Status == CaseStatus.New ? 0 : c.Status == CaseStatus.Open ? 1 : c.Status == CaseStatus.Pending ? 2 : c.Status == CaseStatus.Resolved ? 3 : 4, clause.Descending);
                    break;

                case "priority":
                    ordered = Add(ordered, query, c => c.Priority == CasePriority.Low ? 0 : c.Priority == CasePriority.Normal ? 1 : c.Priority == CasePriority.High ? 2 : 3, clause.Descending);
                    break;

                case "dueat":
                    ordered = Add(ordered, query, c => c.DueAt, clause.Descending);
                    break;

                case "createdat":
                    ordered = Add(ordered, query, c => c.CreatedAt, clause.Descending);
                    break;

                case "updatedat":
                    ordered = Add(ordered, query, c => c.ModifiedDate, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderByDescending(c => c.CreatedAt);
    }

    private static IOrderedQueryable<Case> Add<TKey>(IOrderedQueryable<Case>? ordered, IQueryable<Case> query, Expression<Func<Case, TKey>> key, bool descending) =>
        ordered is null
            ? (descending ? query.OrderByDescending(key) : query.OrderBy(key))
            : (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));

    /// <summary>Zaman çizelgesi birleşik satırı (yalnız sorgu projeksiyonu).</summary>
    private sealed class TimelineRow
    {
        public Guid Id { get; init; }

        public string Type { get; init; } = string.Empty;

        public DateTime OccurredAt { get; init; }

        public Guid? ActorUserId { get; init; }

        public string? Visibility { get; init; }

        public string? Body { get; init; }

        public string? FromValue { get; init; }

        public string? ToValue { get; init; }

        public string? Note { get; init; }
    }

    /// <summary>Bir sayfadaki taleplerin üye/firma/kişi adları (toplu çözülür) ve DTO eşlemesi.</summary>
    private sealed class NameContext
    {
        private readonly IReadOnlyDictionary<Guid, string> _users;
        private readonly IReadOnlyDictionary<RecordRef, string> _records;

        private NameContext(IReadOnlyDictionary<Guid, string> users, IReadOnlyDictionary<RecordRef, string> records)
        {
            _users = users;
            _records = records;
        }

        public static async Task<NameContext> LoadAsync(IReadOnlyList<Case> cases, IMemberLookup members, IRecordLookup records, CancellationToken ct)
        {
            var userIds = cases.Select(c => c.AssignedUserId).Concat(cases.Select(c => c.CreatedUserId))
                .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
            var users = userIds.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(userIds, ct).ConfigureAwait(false);

            var refs = cases.Where(c => c.AccountId is not null).Select(c => new RecordRef(RecordType.Account, c.AccountId!.Value))
                .Concat(cases.Where(c => c.ContactId is not null).Select(c => new RecordRef(RecordType.Contact, c.ContactId!.Value)))
                .Distinct()
                .ToList();
            var names = refs.Count == 0 ? new Dictionary<RecordRef, string>() : await records.GetDisplayNamesAsync(refs, ct).ConfigureAwait(false);
            return new NameContext(users, names);
        }

        public CaseListItemDto ToListItem(Case c, DateTime nowUtc)
        {
            var sla = c.EvaluateSla(nowUtc);
            return new CaseListItemDto(
                c.Id,
                c.Number,
                c.Subject,
                c.Status,
                c.Priority,
                c.Channel,
                c.AccountId,
                AccountName(c),
                c.ContactId,
                ContactName(c),
                c.AssignedUserId,
                UserName(c.AssignedUserId),
                c.ReopenCount,
                c.FirstResponseAt,
                c.ResolvedAt,
                c.ClosedAt,
                c.FirstResponseDueAt,
                c.DueAt,
                sla.IsBreached,
                sla.State,
                sla.FirstResponseBreached,
                sla.ResolutionBreached,
                c.CreatedAt,
                c.ModifiedDate,
                c.CreatedUserId,
                UserName(c.CreatedUserId));
        }

        public CaseDetailDto ToDetail(Case c, DateTime nowUtc)
        {
            var sla = c.EvaluateSla(nowUtc);
            return new CaseDetailDto(
                c.Id,
                c.Number,
                c.Subject,
                c.Description,
                c.Status,
                c.Priority,
                c.Channel,
                c.AccountId,
                AccountName(c),
                c.ContactId,
                ContactName(c),
                c.AssignedUserId,
                UserName(c.AssignedUserId),
                c.ResolutionNote,
                c.ReopenCount,
                c.FirstResponseAt,
                c.ResolvedAt,
                c.ClosedAt,
                c.FirstResponseDueAt,
                c.DueAt,
                sla.IsBreached,
                sla.State,
                sla.FirstResponseBreached,
                sla.ResolutionBreached,
                c.CreatedAt,
                c.ModifiedDate,
                c.CreatedUserId,
                UserName(c.CreatedUserId));
        }

        private string? UserName(Guid? id) => id is { } value ? _users.GetValueOrDefault(value) : null;

        private string? AccountName(Case c) => c.AccountId is { } id ? _records.GetValueOrDefault(new RecordRef(RecordType.Account, id)) : null;

        private string? ContactName(Case c) => c.ContactId is { } id ? _records.GetValueOrDefault(new RecordRef(RecordType.Contact, id)) : null;
    }
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
