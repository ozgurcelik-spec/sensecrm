using Sense.Crm.Modules.Platform.Domain.Accounts;
using Sense.Crm.Modules.Platform.Domain.Deletion;
using Sense.Crm.Modules.Platform.Domain.Plans;
using Sense.Crm.Modules.Platform.Domain.Usage;
using Sense.Crm.Shared.Contracts.Paging;
using Sense.Crm.Shared.Contracts.Persistence;

namespace Sense.Crm.Modules.Platform.Application;

/// <summary>Platform'un SaveChanges portu (komutlar UnitOfWorkBehaviour ile; sorgu içi yazmalar ve işler doğrudan).</summary>
public interface IPlatformUnitOfWork : IUnitOfWork;

public interface IPlanRepository
{
    /// <summary>Plan (pasif olanlar dahil).</summary>
    Task<Plan?> GetAsync(string code, CancellationToken ct);

    Task<IReadOnlyList<Plan>> ListAsync(CancellationToken ct);
}

public interface ITenantAccountRepository
{
    Task<TenantAccount?> GetAsync(Guid tenantId, CancellationToken ct);

    void Add(TenantAccount account);

    /// <summary>Hesabı olmayan kiracı kimlikleri (backfill için).</summary>
    Task<IReadOnlySet<Guid>> GetExistingIdsAsync(CancellationToken ct);
}

public interface IDeletionRequestRepository
{
    /// <summary>Kiracının etkin (<c>scheduled | running | failed</c>) talebi; yoksa null.</summary>
    Task<DeletionRequest?> GetActiveAsync(Guid tenantId, CancellationToken ct);

    void Add(DeletionRequest request);
}

/// <summary>Platform denetimi (append-only): satırı iş verisiyle <b>aynı SaveChanges/transaction'a</b> ekler.</summary>
public interface IPlatformAudit
{
    void Record(string action, TenantAccount? target, Guid? targetTenantId, IReadOnlyDictionary<string, object?> details);
}

/// <summary>Varlık önbelleği geçersiz kılma (Platform komutları aynı süreçte anında; diğer kopyalar ≤ süre).</summary>
public interface IEntitlementCache
{
    Task InvalidateAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Bir kiracının kullanım sayımı (kullanıcı sayıları + modül metrikleri).</summary>
public sealed record UsageCollection(int UsersActive, int UsersPending, IReadOnlyDictionary<string, long> Metrics)
{
    /// <summary><c>{modül}.records</c> → modül adı → kayıt toplamı.</summary>
    public IReadOnlyDictionary<string, long> Records =>
        Metrics.Where(m => m.Key.EndsWith(".records", StringComparison.Ordinal))
            .ToDictionary(m => m.Key[..^".records".Length], m => m.Value, StringComparer.Ordinal);

    /// <summary>Kotaya sayılan kullanıcılar: etkin + bekleyen davetli (pasif saymaz).</summary>
    public int UsersUsed => UsersActive + UsersPending;
}

/// <summary>Önbellekli kayıt sayıları (<c>asOf</c>: sayım anı).</summary>
public sealed record RecordCounts(DateTimeOffset AsOf, IReadOnlyDictionary<string, long> Records, long StorageBytes = 0, long FileCount = 0);

/// <summary>Kullanım sayımı: tüm <c>IUsageReporter</c>'ları kiracı kapsamında (filtre atlamadan) çağırır.</summary>
public interface IUsageMeter
{
    /// <summary>Verilen kiracı kapsamında canlı sayım (<c>BeginScope</c> kurar).</summary>
    Task<UsageCollection> CollectAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Geçerli kiracının kesin (önbelleksiz) kullanıcı sayıları.</summary>
    Task<(int Active, int Pending)> CountUsersAsync(CancellationToken ct);

    /// <summary>Geçerli kiracının kesin (önbelleksiz) dosya depolama kullanımı, bayt (M8C; <c>files.storage_bytes</c>). Files yüklü değilse 0.</summary>
    Task<long> GetStorageBytesAsync(CancellationToken ct) => Task.FromResult(0L);

    /// <summary>Geçerli kiracının kayıt sayıları (<c>Platform:Usage:CacheSeconds</c> önbellekli; yumuşak limit).</summary>
    Task<RecordCounts> GetRecordCountsAsync(CancellationToken ct);

    /// <summary>Gecerli kiracinin tek bir modulunun <b>canli</b> (onbelleksiz) sayaclari (M8B sert limitler: webhook/API anahtari). Modul reporter yoksa bos.</summary>
    Task<IReadOnlyDictionary<string, long>> CountModuleAsync(string module, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
}

/// <summary>Kullanım anlık görüntüsü yazma (idempotent upsert: <c>INSERT … ON CONFLICT (tenant_id, day) DO UPDATE</c>).</summary>
public interface IUsageSnapshotWriter
{
    Task UpsertAsync(Guid tenantId, DateOnly day, UsageCollection usage, DateTime takenAtUtc, CancellationToken ct);
}

/// <summary>Konsol okuma tarafı (projeksiyonlar Infrastructure'da EF ile; platform tabloları küresel).</summary>
public interface IPlatformReadStore
{
    Task<PagedResult<OrganizationRowDto>> ListOrganizationsAsync(PagedQuery paging, OrganizationFilter filter, DateTime nowUtc, CancellationToken ct);

    Task<OrganizationRowDto?> GetOrganizationRowAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct);

    Task<DeletionDto?> GetLatestDeletionAsync(Guid tenantId, CancellationToken ct);

    Task<IReadOnlyList<PlanDto>> ListPlansAsync(CancellationToken ct);

    Task<PagedResult<PlatformAuditDto>> ListAuditAsync(PagedQuery paging, AuditFilter filter, CancellationToken ct);

    Task<IReadOnlyList<UsageDayDto>> ListUsageAsync(Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Dışa aktarılacak (kiracı, gün) satır sayısı (denetim satırı için).</summary>
    Task<long> CountUsageRowsAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>CSV dışa aktarma: akış halinde yazar (büyük satır sayısı belleğe alınmaz).</summary>
public interface IUsageExportWriter
{
    Task WriteCsvAsync(DateOnly from, DateOnly to, Stream output, DateTime nowUtc, CancellationToken ct);
}

public sealed record OrganizationFilter(string? Status, string? PlanCode, string? Source);

public sealed record AuditFilter(Guid? TenantId, string? Action, Guid? ActorUserId, DateOnly? From, DateOnly? To);
