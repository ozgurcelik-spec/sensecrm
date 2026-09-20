using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Platform.Application;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Domain.Audit;
using Sense.Crm.Modules.Platform.Domain.Deletion;
using Sense.Crm.Modules.Platform.Domain.Plans;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Platform.Infrastructure.Persistence;

// Platform tabloları küresel: kiracı filtresi yoktur (IgnoreQueryFilters gerekmez).

public sealed class PlanRepository(PlatformDbContext db) : IPlanRepository
{
    public Task<Plan?> GetAsync(string code, CancellationToken ct) => db.Plans.FirstOrDefaultAsync(p => p.Id == code, ct);

    public async Task<IReadOnlyList<Plan>> ListAsync(CancellationToken ct) => await db.Plans.OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync(ct);
}

public sealed class TenantAccountRepository(PlatformDbContext db) : ITenantAccountRepository
{
    public Task<TenantAccount?> GetAsync(Guid tenantId, CancellationToken ct) => db.TenantAccounts.FirstOrDefaultAsync(a => a.Id == tenantId, ct);

    public void Add(TenantAccount account) => db.TenantAccounts.Add(account);

    public async Task<IReadOnlySet<Guid>> GetExistingIdsAsync(CancellationToken ct) =>
        (await db.TenantAccounts.AsNoTracking().Select(a => a.Id).ToListAsync(ct)).ToHashSet();
}

public sealed class DeletionRequestRepository(PlatformDbContext db) : IDeletionRequestRepository
{
    public Task<DeletionRequest?> GetActiveAsync(Guid tenantId, CancellationToken ct) =>
        db.DeletionRequests.FirstOrDefaultAsync(
            r => r.TenantId == tenantId && (r.Status == DeletionStatuses.Scheduled || r.Status == DeletionStatuses.Running || r.Status == DeletionStatuses.Failed), ct);

    public void Add(DeletionRequest request) => db.DeletionRequests.Add(request);
}

/// <summary>
/// Platform denetimi: satır iş verisiyle <b>aynı SaveChanges/transaction'da</b> yazılır (komut başarısızsa satır yok). Kiracı denetim tablosuna
/// (<c>audit.audit_log_entries</c>) yazılmaz; <c>details</c> yalnız eski→yeni alan farkı ve <c>reason</c> taşır (kiracı iş verisi/kişisel veri yok).
/// </summary>
public sealed class PlatformAudit(PlatformDbContext db, ICurrentUser user, TimeProvider clock) : IPlatformAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(string action, TenantAccount? target, Guid? targetTenantId, IReadOnlyDictionary<string, object?> details)
    {
        db.AuditEntries.Add(new PlatformAuditEntry
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = clock.GetUtcNow().UtcDateTime,
            ActorUserId = user.UserId,
            ActorEmail = Truncate(user.Email, PlatformLimits.EmailMaxLength),
            Action = action,
            TargetTenantId = target?.TenantId ?? targetTenantId,
            TargetTenantName = target?.Name,
            Details = JsonSerializer.Serialize(details, Json),
            Ip = Truncate(user.IpAddress, PlatformLimits.IpMaxLength),
            CorrelationId = Truncate(user.CorrelationId, PlatformLimits.CorrelationIdMaxLength),
        });
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}

/// <summary>
/// <see cref="IPlatformAuditSink"/>: başka modülün (Identity <c>POST /platform/organizations</c>) platform eylemini kendi transaction'ından bağımsız, hemen kalıcı olarak yazar
/// (aktör: çağıran platform yöneticisi). Satır kiracı iş verisi/kişisel veri içermez.
/// </summary>
public sealed class PlatformAuditSink(PlatformDbContext db, IPlatformAudit audit) : IPlatformAuditSink
{
    public async Task RecordAsync(string action, Guid? targetTenantId, string? targetTenantName, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default)
    {
        audit.Record(action, null, targetTenantId, details);
        if (targetTenantName is not null && db.ChangeTracker.Entries<PlatformAuditEntry>().LastOrDefault(e => e.State == EntityState.Added) is { } entry)
        {
            entry.Entity.TargetTenantName = targetTenantName;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
