using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Shared.Contracts.Entitlements;

namespace Sense.Crm.Modules.Platform.Infrastructure.Persistence;

/// <summary>
/// Finans CSV dışa aktarma (faturalama kancası): satır = (kiracı, gün) anlık görüntüsü, gün artan sonra kiracı adı; sütunlar
/// <c>day,tenantId,slug,name,planCode,status,usersActive,usersPending</c> + aralıktaki anlık görüntülerde geçen tüm <c>metrics</c> anahtarlarının
/// <b>alfabetik birleşimi</b> (eksik değer boş hücre). <c>status</c> anlık görüntü gününün değil <b>dışa aktarma anındaki</b> etkin durumdur.
/// UTF-8 BOM, RFC 4180 kaçışı, formül enjeksiyonu önleme; iki geçişte <b>akış halinde</b> yazılır (satırlar belleğe alınmaz).
/// </summary>
public sealed class UsageExportWriter(PlatformDbContext db) : IUsageExportWriter
{
    private static readonly string[] FixedColumns = ["day", "tenantId", "slug", "name", "planCode", "status", "usersActive", "usersPending"];

    public async Task WriteCsvAsync(DateOnly from, DateOnly to, Stream output, DateTime nowUtc, CancellationToken ct)
    {
        // Geçiş 1: metrik anahtarlarının birleşimi (yalnız metrics sütunu akıtılır).
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        await foreach (var metrics in db.UsageSnapshots.AsNoTracking()
                           .Where(s => s.Day >= from && s.Day <= to)
                           .Select(s => s.Metrics)
                           .AsAsyncEnumerable()
                           .WithCancellation(ct))
        {
            foreach (var key in metrics.Keys)
            {
                keys.Add(key);
            }
        }

        var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        await using var writer = new StreamWriter(output, CsvFormatter.Utf8WithBom, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteAsync(CsvFormatter.Line(FixedColumns.Concat(keys.Select(CsvFormatter.Text)))).ConfigureAwait(false);

        // Geçiş 2: satırlar (gün artan, sonra kiracı adı; kararlılık için kimlik).
        var rows = db.UsageSnapshots.AsNoTracking()
            .Where(s => s.Day >= from && s.Day <= to)
            .Join(db.TenantAccounts.AsNoTracking(), s => s.TenantId, a => a.Id, (s, a) => new { s, a })
            .OrderBy(x => x.s.Day).ThenBy(x => x.a.Name).ThenBy(x => x.a.Id)
            .Select(x => new
            {
                x.s.Day,
                x.a.Id,
                x.a.Slug,
                x.a.Name,
                x.a.PlanCode,
                x.a.Status,
                x.a.SuspensionMode,
                x.a.TrialEndsAt,
                x.s.UsersActive,
                x.s.UsersPending,
                x.s.Metrics,
            });

        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            var trialAt = row.TrialEndsAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : (DateTimeOffset?)null;
            var (status, _) = TenantLifecycle.Evaluate(row.Status, row.SuspensionMode, trialAt, now);

            var cells = new List<string>(FixedColumns.Length + keys.Count)
            {
                row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.Id.ToString("D"),
                CsvFormatter.Text(row.Slug),
                CsvFormatter.Text(row.Name),
                CsvFormatter.Text(row.PlanCode),
                status,
                row.UsersActive.ToString(CultureInfo.InvariantCulture),
                row.UsersPending.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var key in keys)
            {
                cells.Add(row.Metrics.TryGetValue(key, out var value) ? value.ToString(CultureInfo.InvariantCulture) : string.Empty);
            }

            await writer.WriteAsync(CsvFormatter.Line(cells)).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}
