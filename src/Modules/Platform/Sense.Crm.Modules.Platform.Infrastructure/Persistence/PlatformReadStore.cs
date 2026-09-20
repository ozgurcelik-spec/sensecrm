using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Infrastructure.Querying;

namespace Sense.Crm.Modules.Platform.Infrastructure.Persistence;

/// <summary>
/// Etkin durum ifadesi (tek kaynak: liste filtresi, sıralama ve DTO aynı ifadeyi/aynı saf fonksiyonu kullanır): saklanan durum + deneme bitişi + <c>now</c> →
/// <c>trial | active | trial_expired | suspended | pending_deletion | deleted</c>. <c>TenantLifecycle.Evaluate</c> ile birebir aynı tablo.
/// </summary>
public static class TenantStatusExpression
{
    public static Expression<Func<TenantAccount, string>> Effective(DateTime nowUtc) =>
        a => a.Status == StoredTenantStatuses.Deleted ? TenantStatuses.Deleted
            : a.Status == StoredTenantStatuses.PendingDeletion ? TenantStatuses.PendingDeletion
            : a.Status == StoredTenantStatuses.Suspended ? TenantStatuses.Suspended
            : a.TrialEndsAt == null ? TenantStatuses.Active
            : nowUtc >= a.TrialEndsAt ? TenantStatuses.TrialExpired
            : TenantStatuses.Trial;

    /// <summary>Etkin durumu verilen değere eşit olan hesaplar.</summary>
    public static Expression<Func<TenantAccount, bool>> Is(string status, DateTime nowUtc)
    {
        var effective = Effective(nowUtc);
        return Expression.Lambda<Func<TenantAccount, bool>>(Expression.Equal(effective.Body, Expression.Constant(status)), effective.Parameters);
    }
}

/// <summary>Konsol okuma tarafı: projeksiyonlar EF ile; tablolar küresel (filtre atlama yok).</summary>
public sealed class PlatformReadStore(PlatformDbContext db) : IPlatformReadStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<OrganizationRowDto>> ListOrganizationsAsync(PagedQuery paging, OrganizationFilter filter, DateTime nowUtc, CancellationToken ct)
    {
        var q = Rows(nowUtc);
        if (!string.IsNullOrWhiteSpace(paging.Q))
        {
            var pattern = "%" + FilterExpressionBuilder.EscapeLike(paging.Q.Trim()) + "%";
            q = q.Where(r => EF.Functions.ILike(r.Name, pattern, "\\") || EF.Functions.ILike(r.Slug, pattern, "\\"));
        }

        if (filter.Status is { } status)
        {
            q = q.Where(r => r.EffectiveStatus == status);
        }

        if (filter.PlanCode is { } plan)
        {
            q = q.Where(r => r.PlanCode == plan);
        }

        if (filter.Source is { } source)
        {
            q = q.Where(r => r.Source == source);
        }

        var total = await q.LongCountAsync(ct).ConfigureAwait(false);
        var page = await ApplySort(q, paging.SortClauses).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);

        var latest = await LatestSnapshotsAsync(page.Select(r => r.TenantId).ToList(), ct).ConfigureAwait(false);
        var items = page.Select(r => ToDto(r, latest.GetValueOrDefault(r.TenantId), nowUtc)).ToList();
        return new PagedResult<OrganizationRowDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<OrganizationRowDto?> GetOrganizationRowAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var row = await Rows(nowUtc).Where(r => r.TenantId == tenantId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var latest = await LatestSnapshotsAsync([tenantId], ct).ConfigureAwait(false);
        return ToDto(row, latest.GetValueOrDefault(tenantId), nowUtc);
    }

    public async Task<DeletionDto?> GetLatestDeletionAsync(Guid tenantId, CancellationToken ct)
    {
        var request = await db.DeletionRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (request is null)
        {
            return null;
        }

        var email = await db.AuditEntries.AsNoTracking()
            .Where(e => e.TargetTenantId == tenantId && e.Action == PlatformAuditActions.DeletionRequested)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.ActorEmail)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new DeletionDto(
            request.Id,
            request.Status,
            Utc(request.RequestedAt),
            email,
            request.Reason,
            request.RetentionDays,
            Utc(request.ScheduledFor),
            request.CancelledAt is { } cancelled ? Utc(cancelled) : null,
            request.CompletedAt is { } completed ? Utc(completed) : null,
            request.Attempts,
            request.LastError);
    }

    public async Task<IReadOnlyList<PlanDto>> ListPlansAsync(CancellationToken ct)
    {
        var rows = await db.Plans.AsNoTracking()
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new { Plan = p, Assigned = db.TenantAccounts.Count(a => a.PlanCode == p.Id && a.Status != StoredTenantStatuses.Deleted) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new PlanDto(
                r.Plan.Id,
                r.Plan.Name,
                r.Plan.Description,
                r.Plan.IsActive,
                r.Plan.SortOrder,
                r.Plan.TrialDays,
                new PlanLimitsDto(r.Plan.Limits.MaxUsers, r.Plan.Limits.MaxRecords),
                r.Plan.Modules,
                r.Assigned))
            .ToList();
    }

    public async Task<PagedResult<PlatformAuditDto>> ListAuditAsync(PagedQuery paging, AuditFilter filter, CancellationToken ct)
    {
        var q = db.AuditEntries.AsNoTracking().AsQueryable();
        if (filter.TenantId is { } tenantId)
        {
            q = q.Where(e => e.TargetTenantId == tenantId);
        }

        if (filter.Action is { } action)
        {
            q = q.Where(e => e.Action == action);
        }

        if (filter.ActorUserId is { } actor)
        {
            q = q.Where(e => e.ActorUserId == actor);
        }

        if (filter.From is { } from)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            q = q.Where(e => e.OccurredAt >= fromUtc);
        }

        if (filter.To is { } to)
        {
            var toExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            q = q.Where(e => e.OccurredAt < toExclusive);
        }

        var total = await q.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(e => new PlatformAuditDto(
                e.Id,
                Utc(e.OccurredAt),
                e.Action,
                e.ActorUserId,
                e.ActorEmail,
                e.TargetTenantId,
                e.TargetTenantName,
                ParseJson(e.Details),
                e.CorrelationId))
            .ToList();
        return new PagedResult<PlatformAuditDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<IReadOnlyList<UsageDayDto>> ListUsageAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.UsageSnapshots.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Day >= from && s.Day <= to)
            .OrderBy(s => s.Day)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(s => new UsageDayDto(s.Day, s.UsersActive, s.UsersPending, s.Metrics)).ToList();
    }

    public Task<long> CountUsageRowsAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        db.UsageSnapshots.AsNoTracking().LongCountAsync(s => s.Day >= from && s.Day <= to, ct);

    private IQueryable<OrgRow> Rows(DateTime nowUtc)
    {
        // Etkin durum ifadesi TenantStatusExpression.Effective ile birebir aynıdır (üye başlatıcı içinde çağrılamadığından satır içidir; testle doğrulanır).
        return db.TenantAccounts.AsNoTracking()
            .Join(db.Plans.AsNoTracking(), a => a.PlanCode, p => p.Id, (a, p) => new { a, p })
            .Select(x => new OrgRow
            {
                TenantId = x.a.Id,
                Name = x.a.Name,
                Slug = x.a.Slug,
                PlanCode = x.a.PlanCode,
                PlanName = x.p.Name,
                Source = x.a.Source,
                RawStatus = x.a.Status,
                SuspensionMode = x.a.SuspensionMode,
                TrialEndsOn = x.a.TrialEndsOn,
                TrialEndsAt = x.a.TrialEndsAt,
                IsSystem = x.a.IsSystem,
                CreatedAt = x.a.TenantCreatedAt,
                EffectiveStatus = x.a.Status == StoredTenantStatuses.Deleted ? TenantStatuses.Deleted
                    : x.a.Status == StoredTenantStatuses.PendingDeletion ? TenantStatuses.PendingDeletion
                    : x.a.Status == StoredTenantStatuses.Suspended ? TenantStatuses.Suspended
                    : x.a.TrialEndsAt == null ? TenantStatuses.Active
                    : nowUtc >= x.a.TrialEndsAt ? TenantStatuses.TrialExpired
                    : TenantStatuses.Trial,
                LatestUsers = db.UsageSnapshots.Where(s => s.TenantId == x.a.Id).OrderByDescending(s => s.Day).Select(s => (int?)s.UsersActive).FirstOrDefault(),
            });
    }

    private static IQueryable<OrgRow> ApplySort(IQueryable<OrgRow> q, IReadOnlyList<SortClause> sorts)
    {
        IOrderedQueryable<OrgRow>? ordered = null;
        foreach (var sort in sorts)
        {
            ordered = sort.Field.ToLowerInvariant() switch
            {
                "name" => Order(q, ordered, r => r.Name, sort.Descending),
                "createdat" => Order(q, ordered, r => r.CreatedAt, sort.Descending),
                "plan" => Order(q, ordered, r => r.PlanName, sort.Descending),
                "status" => Order(q, ordered, r => r.EffectiveStatus, sort.Descending),
                "users" => sort.Descending
                    ? Chain(q, ordered, r => r.LatestUsers == null, false).ThenByDescending(r => r.LatestUsers)
                    : Chain(q, ordered, r => r.LatestUsers == null, false).ThenBy(r => r.LatestUsers),
                _ => ordered,
            };
        }

        return (ordered ?? q.OrderBy(r => r.Name)).ThenBy(r => r.TenantId);
    }

    private static IOrderedQueryable<OrgRow> Order<TKey>(IQueryable<OrgRow> q, IOrderedQueryable<OrgRow>? ordered, Expression<Func<OrgRow, TKey>> key, bool descending) =>
        ordered is null
            ? (descending ? q.OrderByDescending(key) : q.OrderBy(key))
            : (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));

    private static IOrderedQueryable<OrgRow> Chain<TKey>(IQueryable<OrgRow> q, IOrderedQueryable<OrgRow>? ordered, Expression<Func<OrgRow, TKey>> key, bool descending) =>
        Order(q, ordered, key, descending);

    private async Task<Dictionary<Guid, Domain.Usage.UsageSnapshot>> LatestSnapshotsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken ct)
    {
        if (tenantIds.Count == 0)
        {
            return [];
        }

        var ids = tenantIds.ToArray();
        var snapshots = await db.UsageSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.TenantId))
            .GroupBy(s => s.TenantId)
            .Select(g => g.OrderByDescending(s => s.Day).First())
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return snapshots.ToDictionary(s => s.TenantId);
    }

    private static OrganizationRowDto ToDto(OrgRow row, Domain.Usage.UsageSnapshot? snapshot, DateTime nowUtc)
    {
        var trialAt = row.TrialEndsAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        var (status, access) = TenantLifecycle.Evaluate(row.RawStatus, row.SuspensionMode, trialAt, new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)));
        OrganizationUsageDto? usage = null;
        if (snapshot is not null)
        {
            var records = snapshot.Metrics
                .Where(m => m.Key.EndsWith(".records", StringComparison.Ordinal))
                .ToDictionary(m => m.Key[..^".records".Length], m => m.Value, StringComparer.Ordinal);
            usage = new OrganizationUsageDto(snapshot.Day, snapshot.UsersActive, snapshot.UsersPending, records);
        }

        return new OrganizationRowDto(row.TenantId, row.Name, row.Slug, row.PlanCode, row.PlanName, row.Source, status, access, row.TrialEndsOn, row.IsSystem, Utc(row.CreatedAt), usage);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }

    private sealed class OrgRow
    {
        public Guid TenantId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Slug { get; set; } = string.Empty;

        public string PlanCode { get; set; } = string.Empty;

        public string PlanName { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string RawStatus { get; set; } = string.Empty;

        public string? SuspensionMode { get; set; }

        public DateOnly? TrialEndsOn { get; set; }

        public DateTime? TrialEndsAt { get; set; }

        public bool IsSystem { get; set; }

        public DateTime CreatedAt { get; set; }

        public string? EffectiveStatus { get; set; }

        public int? LatestUsers { get; set; }
    }
}

/// <summary>CSV hücre biçimlendirme: RFC 4180 kaçışı + formül enjeksiyonu önleme (<c>= + - @ \t \r</c> ile başlayan metin hücresinin başına <c>'</c>).</summary>
public static class CsvFormatter
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];
    private static readonly char[] CsvSpecials = [',', '"', '\r', '\n'];

    /// <summary>Metin hücresi (formül öneki + RFC 4180 kaçışı).</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cell = Array.IndexOf(FormulaStarts, value[0]) >= 0 ? "'" + value : value;
        return cell.AsSpan().IndexOfAny(CsvSpecials) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
    }

    public static string Line(IEnumerable<string> cells) => string.Join(',', cells) + "\r\n";

    public static UTF8Encoding Utf8WithBom { get; } = new(encoderShouldEmitUTF8Identifier: true);
}
