using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.ApiKeys;
using Sense.Crm.Modules.Integrations.Application.Webhooks;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain;
using Sense.Crm.Modules.Integrations.Domain.ApiKeys;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Shared.Contracts.Paging;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Persistence;

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

/// <summary>Sıralama yardımcısı: beyaz liste dışındaki alanlar yok sayılır; hiçbiri geçerli değilse varsayılan sıralama; her zaman <c>Id</c> ile kararlı.</summary>
internal static class Ordering
{
    public static IOrderedQueryable<T> Apply<T, TKey>(IQueryable<T> query, IOrderedQueryable<T>? ordered, System.Linq.Expressions.Expression<Func<T, TKey>> key, bool descending) =>
        ordered is null
            ? (descending ? query.OrderByDescending(key) : query.OrderBy(key))
            : (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));
}

/// <summary>Webhook okuma tarafı (kiracı filtresi altında; adlar toplu çözülür, N+1 yok).</summary>
public sealed class WebhookReadStore(IntegrationsDbContext db, IMemberLookup members) : IWebhookReadStore
{
    public async Task<PagedResult<WebhookSubscriptionDto>> ListSubscriptionsAsync(PagedQuery paging, string? eventType, bool? enabled, DateTime nowUtc, CancellationToken ct)
    {
        var query = db.WebhookSubscriptions.AsNoTracking();
        if (enabled is { } flag)
        {
            query = query.Where(s => s.Enabled == flag);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var type = eventType.Trim();
            query = query.Where(s => s.EventTypes.Contains(type));
        }

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(s => EF.Functions.ILike(s.Name, q, SearchPattern.Escape) || EF.Functions.ILike(s.Host, q, SearchPattern.Escape));
        }

        IOrderedQueryable<WebhookSubscription>? ordered = null;
        foreach (var clause in paging.SortClauses)
        {
            ordered = clause.Field.ToLowerInvariant() switch
            {
                "name" => Ordering.Apply(query, ordered, s => s.Name, clause.Descending),
                "createdat" => Ordering.Apply(query, ordered, s => s.CreatedAt, clause.Descending),
                "lastfailureat" => Ordering.Apply(query, ordered, s => s.LastFailureAt, clause.Descending),
                _ => ordered,
            };
        }

        ordered ??= query.OrderBy(s => s.Name);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await ordered.ThenBy(s => s.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        var names = await members.GetDisplayNamesAsync([.. rows.Select(r => r.CreatedUserId).OfType<Guid>().Distinct()], ct).ConfigureAwait(false);
        return new PagedResult<WebhookSubscriptionDto>(rows.Select(r => WebhookMapper.ToDto(r, nowUtc, NameOf(names, r.CreatedUserId))).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<WebhookSubscriptionDto?> GetSubscriptionAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        var row = await db.WebhookSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var names = await members.GetDisplayNamesAsync([.. new[] { row.CreatedUserId }.OfType<Guid>()], ct).ConfigureAwait(false);
        return WebhookMapper.ToDto(row, nowUtc, NameOf(names, row.CreatedUserId));
    }

    public async Task<PagedResult<DeliveryListItemDto>> ListDeliveriesAsync(PagedQuery paging, DeliveryFilter filter, int maxAttempts, CancellationToken ct)
    {
        var query = db.WebhookDeliveries.AsNoTracking().AsQueryable();
        if (filter.SubscriptionId is { } subscriptionId)
        {
            query = query.Where(d => d.SubscriptionId == subscriptionId);
        }

        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses.ToArray();
            query = query.Where(d => statuses.Contains(d.Status));
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            var type = filter.EventType.Trim();
            query = query.Where(d => d.EventType == type);
        }

        if (filter.EventId is { } eventId)
        {
            query = query.Where(d => d.EventId == eventId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Kind))
        {
            var kind = filter.Kind.Trim();
            query = query.Where(d => d.Kind == kind);
        }

        if (filter.From is { } from)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(d => d.CreatedAt >= fromUtc);
        }

        if (filter.To is { } to)
        {
            var toUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(d => d.CreatedAt < toUtc);
        }

        IOrderedQueryable<WebhookDelivery>? ordered = null;
        foreach (var clause in paging.SortClauses)
        {
            ordered = clause.Field.ToLowerInvariant() switch
            {
                "createdat" => Ordering.Apply(query, ordered, d => d.CreatedAt, clause.Descending),
                "completedat" => Ordering.Apply(query, ordered, d => d.CompletedAt, clause.Descending),
                "attempts" => Ordering.Apply(query, ordered, d => d.Attempts, clause.Descending),
                "status" => Ordering.Apply(query, ordered, d => d.Status, clause.Descending),
                _ => ordered,
            };
        }

        ordered ??= query.OrderByDescending(d => d.CreatedAt);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await ordered.ThenBy(d => d.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        var subscriptionIds = rows.Select(r => r.SubscriptionId).Distinct().ToArray();
        var subscriptionNames = await db.WebhookSubscriptions.AsNoTracking().Where(s => subscriptionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct).ConfigureAwait(false);
        return new PagedResult<DeliveryListItemDto>(rows.Select(r => ToListItem(r, subscriptionNames.GetValueOrDefault(r.SubscriptionId, string.Empty), maxAttempts)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<DeliveryDetailDto?> GetDeliveryAsync(Guid id, int maxAttempts, CancellationToken ct)
    {
        var row = await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var name = await db.WebhookSubscriptions.AsNoTracking().Where(s => s.Id == row.SubscriptionId).Select(s => s.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? string.Empty;
        var attempts = await db.WebhookDeliveryAttempts.AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.AttemptNo).ToListAsync(ct).ConfigureAwait(false);
        var item = ToListItem(row, name, maxAttempts);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["User-Agent"] = "SenseCRM-Webhooks/1.0",
            ["X-Crm-Event-Id"] = row.EventId.ToString("D"),
            ["X-Crm-Event-Type"] = row.EventType,
            ["X-Crm-Delivery-Id"] = row.Id.ToString("D"),
        };
        if (row.Attempts > 0)
        {
            headers["X-Crm-Delivery-Attempt"] = row.Attempts.ToString(CultureInfo.InvariantCulture);
        }

        headers[WebhookSignature.HeaderName] = "[redacted]";

        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(row.Payload).RootElement.Clone();
        }
        catch (JsonException)
        {
            payload = JsonDocument.Parse("{}").RootElement.Clone();
        }

        return new DeliveryDetailDto(
            item.Id, item.SubscriptionId, item.SubscriptionName, item.EventId, item.EventType, item.Kind, item.Status, item.Attempts, item.MaxAttempts, item.NextAttemptAt,
            item.LastAttemptAt, item.CompletedAt, item.ResponseStatus, item.DurationMs, item.FailureReason, item.Host, item.CreatedAt, payload, headers,
            attempts.Select(a => new DeliveryAttemptDto(a.AttemptNo, a.StartedAt, a.DurationMs, a.ResponseStatus, a.FailureReason, a.ErrorDetail, a.ResponseSnippet)).ToList());
    }

    private static DeliveryListItemDto ToListItem(WebhookDelivery d, string subscriptionName, int maxAttempts) =>
        new(d.Id, d.SubscriptionId, subscriptionName, d.EventId, d.EventType, d.Kind, d.Status, d.Attempts, maxAttempts, d.NextAttemptAt, d.LastAttemptAt, d.CompletedAt, d.ResponseStatus, d.DurationMs, d.FailureReason, d.Host, d.CreatedAt);

    private static string? NameOf(IReadOnlyDictionary<Guid, string> names, Guid? userId) => userId is { } id && names.TryGetValue(id, out var name) ? name : null;
}

/// <summary>API anahtarı okuma tarafı (özet/<c>secret_hash</c> asla okunmaz/dönmez).</summary>
public sealed class ApiKeyReadStore(IntegrationsDbContext db, IMemberLookup members) : IApiKeyReadStore
{
    public async Task<PagedResult<ApiKeyDto>> ListAsync(PagedQuery paging, string? status, DateTime nowUtc, CancellationToken ct)
    {
        var query = db.ApiKeys.AsNoTracking().AsQueryable();
        query = status switch
        {
            ApiKeyStatuses.Active => query.Where(k => k.RevokedAt == null && k.ExpiresAt > nowUtc),
            ApiKeyStatuses.Expired => query.Where(k => k.RevokedAt == null && k.ExpiresAt <= nowUtc),
            ApiKeyStatuses.Revoked => query.Where(k => k.RevokedAt != null),
            _ => query,
        };

        if (SearchPattern.Contains(paging.Q) is { } q)
        {
            query = query.Where(k => EF.Functions.ILike(k.Name, q, SearchPattern.Escape) || EF.Functions.ILike(k.Prefix, q, SearchPattern.Escape));
        }

        IOrderedQueryable<ApiKey>? ordered = null;
        foreach (var clause in paging.SortClauses)
        {
            ordered = clause.Field.ToLowerInvariant() switch
            {
                "createdat" => Ordering.Apply(query, ordered, k => k.CreatedAt, clause.Descending),
                "name" => Ordering.Apply(query, ordered, k => k.Name, clause.Descending),
                "lastusedat" => Ordering.Apply(query, ordered, k => k.LastUsedAt, clause.Descending),
                "expiresat" => Ordering.Apply(query, ordered, k => k.ExpiresAt, clause.Descending),
                _ => ordered,
            };
        }

        ordered ??= query.OrderByDescending(k => k.CreatedAt);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await ordered.ThenBy(k => k.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(ct).ConfigureAwait(false);
        var names = await ResolveNamesAsync(rows, ct).ConfigureAwait(false);
        return new PagedResult<ApiKeyDto>(rows.Select(k => ToDto(k, nowUtc, names)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<ApiKeyDto?> GetAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        var row = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return ToDto(row, nowUtc, await ResolveNamesAsync([row], ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ApiKeyUsageDayDto>> GetUsageAsync(Guid keyId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.ApiKeyUsageDays.AsNoTracking()
            .Where(u => u.ApiKeyId == keyId && u.Day >= from && u.Day <= to)
            .OrderBy(u => u.Day)
            .Select(u => new ApiKeyUsageDayDto(u.Day, u.Requests, u.Errors, u.Throttled))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<DateTime?> GetExpiryAsync(Guid keyId, CancellationToken ct)
    {
        var expiry = await db.ApiKeys.AsNoTracking().Where(k => k.Id == keyId).Select(k => (DateTime?)k.ExpiresAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return expiry;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IReadOnlyList<ApiKey> rows, CancellationToken ct)
    {
        var ids = rows.SelectMany(k => new Guid?[] { k.CreatedByUserId, k.RevokedByUserId }).OfType<Guid>().Distinct().ToArray();
        return ids.Length == 0 ? new Dictionary<Guid, string>() : await members.GetDisplayNamesAsync(ids, ct).ConfigureAwait(false);
    }

    private static ApiKeyDto ToDto(ApiKey key, DateTime nowUtc, IReadOnlyDictionary<Guid, string> names) =>
        ApiKeyMapper.ToDto(
            key,
            nowUtc,
            names.GetValueOrDefault(key.CreatedByUserId),
            key.RevokedByUserId is { } revoker ? names.GetValueOrDefault(revoker) : null);
}

/// <summary>Kiracı sayaçları (durum ucu): abonelikler (pasifler dahil) ve etkin anahtarlar; canlı, önbelleksiz.</summary>
public sealed class IntegrationsStats(IntegrationsDbContext db) : IIntegrationsStats
{
    public async Task<(int Webhooks, int ApiKeys)> CountAsync(DateTime nowUtc, CancellationToken ct)
    {
        var webhooks = await db.WebhookSubscriptions.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
        var keys = await db.ApiKeys.AsNoTracking().CountAsync(k => k.RevokedAt == null && k.ExpiresAt > nowUtc, ct).ConfigureAwait(false);
        return (webhooks, keys);
    }
}
