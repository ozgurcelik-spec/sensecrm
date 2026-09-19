using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Activities.Application;
using Sense.Crm.Modules.Activities.Application.Activities;
using Sense.Crm.Modules.Activities.Domain.Activities;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Contracts;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Activities.Infrastructure.Persistence;

/// <summary>
/// Sorgu projeksiyonları (kiracı + yumuşak silme filtresi altında). Arama <c>ILIKE</c> + kaçışlı parametre; sıralama yalnız beyaz
/// listedeki alanlarda (<c>dueAt</c>, <c>createdAt</c>, <c>subject</c>, <c>priority</c>; bilinmeyen alan yok sayılır) ve her zaman
/// <c>Id</c> ile kararlı; <c>dueAt</c> boş olanlar her yönde sonda. Atanan ve ilişkili kayıt adları sayfa başına toplu çözülür.
/// </summary>
public sealed class ActivityReadStore(ActivitiesDbContext db, IMemberLookup members, IRecordLookup records, ICurrentUser currentUser, IPermissionService permissions) : IActivityReadStore
{
    /// <summary>
    /// Çağıran ilişkili kaydın türünü okuyamıyorsa (ör. yalnız <c>crm.activities.read</c>) <c>relatedName</c> yerine bu nötr yer tutucu
    /// döner (L2): aktivite listesi, okuma izni olmayan firma/kişi/potansiyel/fırsat adlarını sızdırmaz. Tür/kimlik yine döner.
    /// </summary>
    public const string HiddenRelatedName = "***";

    public async Task<PagedResult<ActivityDto>> ListAsync(PagedQuery paging, ActivityFilter filter, DateTime nowUtc, CancellationToken ct)
    {
        var query = db.Activities.AsNoTracking();
        if (filter.AssignedUserId is { } assignee)
        {
            query = query.Where(a => a.AssignedUserId == assignee);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(a => a.Type == type);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        if (filter.RelatedType is { } relatedType)
        {
            query = query.Where(a => a.RelatedType == relatedType);
        }

        if (filter.RelatedId is { } relatedId)
        {
            query = query.Where(a => a.RelatedId == relatedId);
        }

        if (filter.DueFrom is { } dueFrom)
        {
            query = query.Where(a => a.DueAt != null && a.DueAt >= dueFrom);
        }

        if (filter.DueTo is { } dueTo)
        {
            query = query.Where(a => a.DueAt != null && a.DueAt <= dueTo);
        }

        if (filter.Overdue is true)
        {
            query = query.Where(a => a.Status == ActivityStatus.Open && a.DueAt != null && a.DueAt < nowUtc);
        }
        else if (filter.Overdue is false)
        {
            query = query.Where(a => !(a.Status == ActivityStatus.Open && a.DueAt != null && a.DueAt < nowUtc));
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(a => EF.Functions.ILike(a.Subject, q, SearchPattern.Escape)
                || (a.Description != null && EF.Functions.ILike(a.Description, q, SearchPattern.Escape)));
        }

        var total = await query.LongCountAsync(ct);
        var rows = await Order(query, paging.SortClauses).ThenBy(a => a.CreatedAt).ThenBy(a => a.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct);
        return new PagedResult<ActivityDto>(await MapAsync(rows, nowUtc, ct), paging.Page, paging.PageSize, total);
    }

    public async Task<ActivityDto?> GetAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return activity is null ? null : (await MapAsync([activity], nowUtc, ct))[0];
    }

    public async Task<ActivitySummaryDto> GetSummaryAsync(Guid userId, ActivitySummaryWindow window, CancellationToken ct)
    {
        var (now, todayStart, tomorrowStart, weekStart, nextWeekStart) = (window.NowUtc, window.TodayStartUtc, window.TomorrowStartUtc, window.WeekStartUtc, window.NextWeekStartUtc);
        var counts = await db.Activities.AsNoTracking()
            .Where(a => a.AssignedUserId == userId && a.Type != ActivityType.Note)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Open = g.Count(a => a.Status == ActivityStatus.Open),
                Overdue = g.Count(a => a.Status == ActivityStatus.Open && a.DueAt != null && a.DueAt < now),
                DueToday = g.Count(a => a.Status == ActivityStatus.Open && a.DueAt != null && a.DueAt >= todayStart && a.DueAt < tomorrowStart),
                Completed = g.Count(a => a.Status == ActivityStatus.Completed && a.CompletedAt != null && a.CompletedAt >= weekStart && a.CompletedAt < nextWeekStart),
            })
            .SingleOrDefaultAsync(ct);
        return counts is null ? new ActivitySummaryDto(0, 0, 0, 0) : new ActivitySummaryDto(counts.Open, counts.Overdue, counts.DueToday, counts.Completed);
    }

    public async Task<IReadOnlyList<ActivityUserTotals>> GetUserTotalsAsync(DateTime fromUtc, DateTime toExclusiveUtc, DateTime nowUtc, CancellationToken ct)
    {
        var work = db.Activities.AsNoTracking().Where(a => a.Type != ActivityType.Note);

        var completed = await work
            .Where(a => a.Status == ActivityStatus.Completed && a.CompletedAt != null && a.CompletedAt >= fromUtc && a.CompletedAt < toExclusiveUtc)
            .GroupBy(a => a.AssignedUserId)
            .Select(g => new { User = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var open = await work
            .Where(a => a.Status == ActivityStatus.Open && a.DueAt != null && a.DueAt >= fromUtc && a.DueAt < toExclusiveUtc)
            .GroupBy(a => a.AssignedUserId)
            .Select(g => new { User = g.Key, Count = g.Count(), Overdue = g.Count(a => a.DueAt < nowUtc) })
            .ToListAsync(ct);

        return completed.Select(c => c.User).Concat(open.Select(o => o.User)).Distinct()
            .Select(user =>
            {
                var c = completed.FirstOrDefault(x => x.User == user);
                var o = open.FirstOrDefault(x => x.User == user);
                return new ActivityUserTotals(user, c?.Count ?? 0, o?.Count ?? 0, o?.Overdue ?? 0);
            })
            .ToList();
    }

    /// <summary>Sıralama beyaz listesi; hiçbiri geçerli değilse <c>dueAt</c> artan. Boş <c>dueAt</c> her zaman sonda.</summary>
    private static IOrderedQueryable<Activity> Order(IQueryable<Activity> query, IReadOnlyList<SortClause> clauses)
    {
        IOrderedQueryable<Activity>? ordered = null;
        foreach (var clause in clauses)
        {
            switch (clause.Field.ToLowerInvariant())
            {
                case "dueat":
                    ordered = ordered is null ? query.OrderBy(a => a.DueAt == null) : ordered.ThenBy(a => a.DueAt == null);
                    ordered = clause.Descending ? ordered.ThenByDescending(a => a.DueAt) : ordered.ThenBy(a => a.DueAt);
                    break;

                case "createdat":
                    ordered = Add(ordered, query, a => a.CreatedAt, clause.Descending);
                    break;

                case "subject":
                    ordered = Add(ordered, query, a => a.Subject, clause.Descending);
                    break;

                case "priority":
                    ordered = Add(ordered, query, a => a.Priority == ActivityPriority.High ? 2 : a.Priority == ActivityPriority.Normal ? 1 : 0, clause.Descending);
                    break;

                default:
                    break;
            }
        }

        return ordered ?? query.OrderBy(a => a.DueAt == null).ThenBy(a => a.DueAt);
    }

    private static IOrderedQueryable<Activity> Add<TKey>(
        IOrderedQueryable<Activity>? ordered,
        IQueryable<Activity> query,
        System.Linq.Expressions.Expression<Func<Activity, TKey>> key,
        bool descending) =>
        ordered is null
            ? (descending ? query.OrderByDescending(key) : query.OrderBy(key))
            : (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));

    private async Task<IReadOnlyList<ActivityDto>> MapAsync(IReadOnlyList<Activity> activities, DateTime nowUtc, CancellationToken ct)
    {
        var userIds = activities.Select(a => a.AssignedUserId).Distinct().ToList();
        var users = userIds.Count == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(userIds, ct);

        var refs = activities
            .Where(a => a.RelatedType is not null && a.RelatedId is not null)
            .Select(a => new RecordRef(ToRecordType(a.RelatedType!.Value), a.RelatedId!.Value))
            .Distinct()
            .ToList();
        var readable = await ReadableRecordTypesAsync(refs, ct);
        var names = refs.Count == 0 ? new Dictionary<RecordRef, string>() : await records.GetDisplayNamesAsync(refs.Where(r => readable.Contains(r.Type)).ToList(), ct);

        return activities.Select(a => new ActivityDto(
            a.Id,
            a.Type,
            a.Subject,
            a.Description,
            a.Status,
            a.Priority,
            a.DueAt,
            a.StartAt,
            a.EndAt,
            a.RelatedType,
            a.RelatedId,
            RelatedName(a, names, readable),
            a.AssignedUserId,
            users.GetValueOrDefault(a.AssignedUserId),
            a.CompletedAt,
            a.IsOverdue(nowUtc),
            a.CreatedAt,
            a.ModifiedDate)).ToList();
    }

    private static RecordType ToRecordType(ActivityRelatedType type) => RelatedRecordVerifier.ToRecordType(type);

    private static string? RelatedName(Activity activity, IReadOnlyDictionary<RecordRef, string> names, HashSet<RecordType> readable)
    {
        if (activity.RelatedType is not { } type || activity.RelatedId is not { } id)
        {
            return null;
        }

        var recordType = ToRecordType(type);
        return readable.Contains(recordType) ? names.GetValueOrDefault(new RecordRef(recordType, id)) : HiddenRelatedName;
    }

    /// <summary>Çağıranın okuma izni olan ilişkili kayıt türleri (sistem bağlamı: hepsi). Sayfa başına tek izin çözümü.</summary>
    private async Task<HashSet<RecordType>> ReadableRecordTypesAsync(IReadOnlyCollection<RecordRef> refs, CancellationToken ct)
    {
        var types = refs.Select(r => r.Type).Distinct().ToList();
        if (types.Count == 0 || currentUser.UserId is not { } userId)
        {
            return [.. types];
        }

        var held = await permissions.GetPermissionsAsync(userId, ct);
        return types.Where(t => held.Contains(ReadPermission(t))).ToHashSet();
    }

    private static string ReadPermission(RecordType type) => type switch
    {
        RecordType.Account => SalesPermissions.AccountsRead,
        RecordType.Contact => SalesPermissions.ContactsRead,
        RecordType.Lead => SalesPermissions.LeadsRead,
        RecordType.Deal => SalesPermissions.DealsRead,
        _ => string.Empty,
    };
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
