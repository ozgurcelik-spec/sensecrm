using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Domain.Memberships;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;

namespace Sense.Crm.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Sorgu projeksiyonları. Üyelik, rol ve denetim sorguları kiracı query filter'ı altında çalışır (yalnız aktif organizasyon);
/// tek bilinçli istisna, kullanıcının kendi organizasyon listesidir (<see cref="ListOrganizationsOfUserAsync"/>).
/// </summary>
public sealed class IdentityReadStore(IdentityDbContext db) : IIdentityReadStore
{
    public async Task<IReadOnlyList<MemberDto>> ListMembersAsync(CancellationToken ct)
    {
        var rows = await MemberRows().ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<InvitationDto>> ListInvitationsOfUserAsync(Guid userId, CancellationToken ct)
    {
        // Bilinçli kiracı filtresi aşımı: yalnız verilen kullanıcının kendi bekleyen davetleri; organizasyon adı ve rol adı davet edilene
        // gösterilmesi gereken bilgidir (başka hiçbir hesap verisi dönmez).
        var rows = await (from m in db.Memberships.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).AsNoTracking()
                          where m.UserId == userId && m.Status == MembershipStatus.Pending
                          join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
                          where t.IsActive
                          join r in db.Roles.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).AsNoTracking() on m.RoleId equals r.Id
                          orderby m.JoinedAt descending
                          select new { m.Id, OrganizationId = t.Id, OrganizationName = t.Name, RoleName = r.Name, InvitedAt = m.JoinedAt })
            .ToListAsync(ct);

        return rows.Select(r => new InvitationDto(r.Id, r.OrganizationId, r.OrganizationName, r.RoleName, new DateTimeOffset(DateTime.SpecifyKind(r.InvitedAt, DateTimeKind.Utc)))).ToList();
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
            .Skip(Sense.Crm.Shared.Contracts.Paging.PagedQuery.SkipFor(page, pageSize))
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(e => new AuditEntryDto(
                e.Id, e.EntityType, e.EntityId, e.Action, e.UserId, e.UserDisplayName, ParseChanges(e.Changes), e.OccurredAt, e.ApiKeyId))
            .ToList();
        return new AuditPageDto(items, total);
    }

    /// <summary>
    /// Etkin üyeler tam satırdır. Bekleyen davetler (mevcut hesaplar) YALNIZ e-posta ile görünür: ad boş, <c>userId</c> yerine davetin
    /// (üyelik satırının) kimliği döner — başka organizasyonlarla paylaşılan hesabın kimliği/adı bu organizasyona sızmaz.
    /// </summary>
    private IQueryable<MemberRow> MemberRows() =>
        from m in db.Memberships.AsNoTracking()
        join u in db.Users.AsNoTracking() on m.UserId equals u.Id
        join r in db.Roles.AsNoTracking() on m.RoleId equals r.Id
        orderby m.JoinedAt
        select new MemberRow(
            m.Status == MembershipStatus.Pending ? m.Id : m.UserId,
            u.Email,
            m.Status == MembershipStatus.Pending ? string.Empty : u.DisplayName,
            r.Id,
            r.Name,
            m.IsActive,
            m.JoinedAt,
            m.Status == MembershipStatus.Pending);

    private static MemberDto ToDto(MemberRow r) =>
        new(r.UserId, r.Email, r.DisplayName, r.RoleId, r.RoleName, r.IsActive, new DateTimeOffset(DateTime.SpecifyKind(r.JoinedAt, DateTimeKind.Utc)),
            r.IsPending ? MemberStatuses.Pending : MemberStatuses.Active,
            r.IsPending ? new DateTimeOffset(DateTime.SpecifyKind(r.JoinedAt, DateTimeKind.Utc)) : null);

    private static JsonElement ParseChanges(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record MemberRow(Guid UserId, string Email, string DisplayName, Guid RoleId, string RoleName, bool IsActive, DateTime JoinedAt, bool IsPending);
}
