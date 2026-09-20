using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Modules.Files.Domain;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Infrastructure.Querying;

namespace Sense.Crm.Modules.Files.Infrastructure.Persistence;

public sealed class FileAttachmentRepository(FilesDbContext db) : IFileAttachmentRepository
{
    public void Add(FileAttachment file) => db.Attachments.Add(file);

    public Task<FileAttachment?> GetAsync(Guid id, CancellationToken ct) =>
        db.Attachments.FirstOrDefaultAsync(a => a.Id == id, ct);

    public void AddAccess(FileAccessLogEntry entry) => db.AccessLog.Add(entry);
}

/// <summary>
/// Sorgu projeksiyonları (kiracı filtresi altında; <c>state &lt;&gt; deleted</c> açık sorgulanır — <c>is_deleted</c> filtresi yoktur). Arama <c>ILIKE</c> + kaçışlı parametre;
/// sıralama yalnız beyaz listedeki alanlarda (<c>name, sizeBytes, extension, uploadedAt</c>; bilinmeyen alan yok sayılır), varsayılan <c>-uploadedAt</c>, her zaman <c>Id</c> ile kararlı.
/// Kullanım tek indeks taramasıdır (<c>ix_attachments_tenant_usage</c>).
/// </summary>
public sealed class FilesReadStore(FilesDbContext db) : IFilesReadStore
{
    public async Task<PagedResult<FileRow>> ListAsync(string recordType, Guid recordId, PagedQuery paging, CancellationToken ct)
    {
        var query = db.Attachments.AsNoTracking().Where(a => a.RecordType == recordType && a.RecordId == recordId && a.State != FileState.Deleted);

        if (paging.Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = "%" + FilterExpressionBuilder.EscapeLike(q) + "%";
            query = query.Where(a => EF.Functions.ILike(a.Name, pattern, "\\"));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await Order(query, paging.SortClauses).Skip(paging.Skip).Take(paging.PageSize)
            .Select(a => new FileRow(a.Id, a.RecordType, a.RecordId, a.Name, a.Extension, a.ContentType, a.SizeBytes, a.Sha256, a.State, a.UploadedByUserId, a.UploadedAt, a.RenamedAt))
            .ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<FileRow>(rows, paging.Page, paging.PageSize, total);
    }

    public async Task<StorageUsage> GetUsageAsync(CancellationToken ct)
    {
        var groups = await db.Attachments.AsNoTracking()
            .Where(a => a.State != FileState.Deleted)
            .GroupBy(a => new { a.RecordType, a.State })
            .Select(g => new { g.Key.RecordType, g.Key.State, Count = g.LongCount(), Bytes = g.Sum(a => a.SizeBytes) })
            .ToListAsync(ct).ConfigureAwait(false);

        var byType = groups
            .GroupBy(g => g.RecordType)
            .Select(g => new RecordTypeUsage(g.Key, g.Sum(x => x.Count), g.Sum(x => x.Bytes)))
            .OrderBy(r => r.RecordType, StringComparer.Ordinal)
            .ToList();

        return new StorageUsage(
            groups.Sum(g => g.Bytes),
            groups.Sum(g => g.Count),
            groups.Where(g => g.State == FileState.Quarantined).Sum(g => g.Count),
            groups.Where(g => g.State == FileState.Missing).Sum(g => g.Count),
            byType);
    }

    private static IOrderedQueryable<FileAttachment> Order(IQueryable<FileAttachment> query, IReadOnlyList<SortClause> sorts)
    {
        IOrderedQueryable<FileAttachment>? ordered = null;
        foreach (var sort in sorts)
        {
            switch (sort.Field.ToLowerInvariant())
            {
                case "name":
                    ordered = Add(ordered, query, a => a.Name, sort.Descending);
                    break;
                case "sizebytes":
                    ordered = Add(ordered, query, a => a.SizeBytes, sort.Descending);
                    break;
                case "extension":
                    ordered = Add(ordered, query, a => a.Extension, sort.Descending);
                    break;
                case "uploadedat":
                    ordered = Add(ordered, query, a => a.UploadedAt, sort.Descending);
                    break;
                default:
                    break;
            }
        }

        ordered ??= query.OrderByDescending(a => a.UploadedAt);
        return ordered.ThenBy(a => a.Id);
    }

    private static IOrderedQueryable<FileAttachment> Add<TKey>(
        IOrderedQueryable<FileAttachment>? ordered,
        IQueryable<FileAttachment> query,
        System.Linq.Expressions.Expression<Func<FileAttachment, TKey>> key,
        bool descending) =>
        ordered is null
            ? (descending ? query.OrderByDescending(key) : query.OrderBy(key))
            : (descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key));
}
