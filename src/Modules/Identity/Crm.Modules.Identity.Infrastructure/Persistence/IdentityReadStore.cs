using System.Text.Json;
using Crm.Modules.Identity.Application;
using Crm.Shared.Infrastructure.Persistence;
using Crm.Shared.Infrastructure.Persistence.Audit;
using Microsoft.EntityFrameworkCore;

namespace Crm.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Sorgu projeksiyonları. Üyelik, rol ve denetim sorguları kiracı query filter'ı altında çalışır (yalnız aktif organizasyon);
/// tek bilinçli istisna, kullanıcının kendi organizasyon listesidir (<see cref="ListOrganizationsOfUserAsync"/>).
/// </summary>
public sealed class IdentityReadStore(IdentityDbContext db) : IIdentityReadStore
{
    public async Task<IReadOnlyList<MemberDto>> ListMembersAsync(CancellationToken ct)
    {
        var rows = await MemberRows(userId: null).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<MemberDto?> GetMemberAsync(Guid userId, CancellationToken ct)
    {
        var row = await MemberRows(userId).FirstOrDefaultAsync(ct);
        return row is null ? null : ToDto(row);
    }

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct)
    {
        var rows = await db.Roles.AsNoTracking()
            .OrderByDescending(r => r.IsSystem)
            .ThenBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.IsSystem, r.Permissions, MemberCount = db.Memberships.Count(m => m.RoleId == r.Id) })
            .ToListAsync(ct);

        return rows.Select(r => new RoleDto(r.Id, r.Name, r.IsSystem, r.Permissions, r.MemberCount)).ToList();
    }

    public async Task<IReadOnlyList<OrganizationSummaryDto>> ListOrganizationsOfUserAsync(Guid userId, CancellationToken ct) =>
        // Bilinçli kiracı filtresi aşımı: yalnız verilen kullanıcının aktif üyelikleri (organizasyon değiştirici için).
        await db.Memberships.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Join(db.Tenants.Where(t => t.IsActive), m => m.TenantId, t => t.Id, (m, t) => t)
            .OrderBy(t => t.Name)
            .Select(t => new OrganizationSummaryDto(t.Id, t.Name, t.Slug))
            .ToListAsync(ct);

    public async Task<AuditPageDto> GetAuditPageAsync(int page, int pageSize, CancellationToken ct) =>
        await ToAuditPageAsync(db.AuditLogEntries.AsNoTracking(), page, pageSize, ct);

    public async Task<AuditPageDto> GetEntityAuditPageAsync(string entityType, string entityId, int page, int pageSize, CancellationToken ct) =>
        await ToAuditPageAsync(db.AuditLogEntries.AsNoTracking().Where(e => e.EntityType == entityType && e.EntityId == entityId), page, pageSize, ct);

    private static async Task<AuditPageDto> ToAuditPageAsync(IQueryable<AuditLogEntry> query, int page, int pageSize, CancellationToken ct)
    {
        var total = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(e => new AuditEntryDto(
                e.Id, e.EntityType, e.EntityId, e.Action, e.UserId, e.UserDisplayName, ParseChanges(e.Changes), e.OccurredAt))
            .ToList();
        return new AuditPageDto(items, total);
    }

    private IQueryable<MemberRow> MemberRows(Guid? userId) =>
        from m in db.Memberships.AsNoTracking()
        where userId == null || m.UserId == userId
        join u in db.Users.AsNoTracking() on m.UserId equals u.Id
        join r in db.Roles.AsNoTracking() on m.RoleId equals r.Id
        orderby m.JoinedAt
        select new MemberRow(m.UserId, u.Email, u.DisplayName, r.Id, r.Name, m.IsActive, m.JoinedAt);

    private static MemberDto ToDto(MemberRow r) =>
        new(r.UserId, r.Email, r.DisplayName, r.RoleId, r.RoleName, r.IsActive, new DateTimeOffset(DateTime.SpecifyKind(r.JoinedAt, DateTimeKind.Utc)));

    private static JsonElement ParseChanges(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record MemberRow(Guid UserId, string Email, string DisplayName, Guid RoleId, string RoleName, bool IsActive, DateTime JoinedAt);
}
