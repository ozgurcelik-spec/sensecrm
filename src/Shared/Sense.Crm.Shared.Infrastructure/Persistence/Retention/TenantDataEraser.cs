using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Shared.Kernel.Domain;

namespace Sense.Crm.Shared.Infrastructure.Persistence.Retention;

/// <summary>
/// Modül DbContext'inin EF modelindeki <b>tüm</b> <see cref="ITenantEntity"/> tablolarını (yumuşak silinenler dahil) ve modülün
/// <c>outbox_messages</c> satırlarını bir kiracı için kalıcı siler (KVKK imhası). Tablo listesi EF metadata'sından üretilir → yeni tablo/modül
/// otomatik kapsanır. Çocuk tablolar önce silinir (FK bağımlılığından topolojik sıra); silme <c>ctid</c> ile parça parça (<paramref name="chunkSize"/>)
/// yapılır ve idempotenttir. <c>audit.audit_log_entries</c> burada değil <c>AuditTenantDataEraser</c>'dadır. Kiracı kimliği her zaman
/// <b>parametredir</b> (ham SQL envanterinde listeli). <c>inbox_messages</c> kiracı anahtarı taşımaz ve kullanılmaz; kapsam dışıdır.
/// </summary>
public sealed class TenantDataEraser<TContext>(TContext db) : ITenantDataEraser
    where TContext : ModuleDbContext
{
    private const string TenantIdProperty = nameof(ITenantEntity.TenantId);

    public string Name => "module:" + db.Schema;

    public int Order => 100;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        var report = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var target in BuildPlan(db))
        {
            var deleted = await DeleteInChunksAsync(target, tenantId, Math.Max(chunkSize, 1), ct).ConfigureAwait(false);
            report[string.Create(CultureInfo.InvariantCulture, $"{target.Schema}.{target.Table}")] = deleted;
        }

        return new EraseReport(report);
    }

    /// <summary>Silinecek (şema, tablo, kiracı kolonu) listesi: çocuk önce, ana sonra; ardından outbox.</summary>
    public static IReadOnlyList<EraseTarget> BuildPlan(DbContext context)
    {
        var entities = context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.ClrType != typeof(AuditLogEntry) && typeof(ITenantEntity).IsAssignableFrom(e.ClrType))
            .Where(e => e.GetTableName() is not null)
            .GroupBy(e => (e.GetSchema(), e.GetTableName()!))
            .Select(g => g.First())
            .ToList();

        var ordered = TopologicalOrder(entities);
        var targets = new List<EraseTarget>();
        foreach (var entity in ordered)
        {
            var table = entity.GetTableName()!;
            var schema = entity.GetSchema() ?? context.Model.GetDefaultSchema() ?? "public";
            var column = entity.FindProperty(TenantIdProperty)?.GetColumnName(StoreObjectIdentifier.Table(table, entity.GetSchema()));
            if (column is not null)
            {
                targets.Add(new EraseTarget(schema, table, column));
            }
        }

        var outbox = context.Model.FindEntityType(typeof(OutboxMessage));
        if (outbox?.GetTableName() is { } outboxTable)
        {
            var schema = outbox.GetSchema() ?? context.Model.GetDefaultSchema() ?? "public";
            var column = outbox.FindProperty(nameof(OutboxMessage.TenantId))?.GetColumnName(StoreObjectIdentifier.Table(outboxTable, outbox.GetSchema()));
            if (column is not null)
            {
                targets.Add(new EraseTarget(schema, outboxTable, column));
            }
        }

        return targets;
    }

    /// <summary>Dependent → principal yönünde: bir tabloyu silmeden önce ona başvuran (FK) tablolar silinir.</summary>
    private static List<IEntityType> TopologicalOrder(List<IEntityType> entities)
    {
        var byTable = entities.ToDictionary(e => (e.GetSchema(), e.GetTableName()!));

        // principal → silinmeden önce silinmesi gereken (ona FK ile başvuran) tablolar.
        var blockers = entities.ToDictionary(e => e, _ => new HashSet<IEntityType>());
        foreach (var dependent in entities)
        {
            foreach (var fk in dependent.GetForeignKeys())
            {
                var principalTable = (fk.PrincipalEntityType.GetSchema(), fk.PrincipalEntityType.GetTableName() ?? string.Empty);
                if (byTable.TryGetValue(principalTable, out var principal) && principal != dependent)
                {
                    blockers[principal].Add(dependent);
                }
            }
        }

        var result = new List<IEntityType>(entities.Count);
        var remaining = entities.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(e => blockers[e].All(result.Contains)).ToList();
            if (ready.Count == 0)
            {
                // Döngü (beklenmez): kalanlar özgün sırayla sona eklenir.
                result.AddRange(remaining);
                break;
            }

            result.AddRange(ready);
            remaining.RemoveAll(ready.Contains);
        }

        return result;
    }

    private async Task<long> DeleteInChunksAsync(EraseTarget target, Guid tenantId, int chunkSize, CancellationToken ct)
    {
        // Tanımlayıcılar EF metadata'sından gelir (kullanıcı girdisi değil); kiracı kimliği ve parça boyutu parametredir.
        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"DELETE FROM \"{target.Schema}\".\"{target.Table}\" WHERE ctid IN (SELECT ctid FROM \"{target.Schema}\".\"{target.Table}\" WHERE \"{target.TenantColumn}\" = {{0}} LIMIT {{1}})");

        long total = 0;
        while (true)
        {
            var affected = await db.Database.ExecuteSqlRawAsync(sql, [tenantId, chunkSize], ct).ConfigureAwait(false);
            total += affected;
            if (affected < chunkSize)
            {
                return total;
            }
        }
    }
}

/// <summary>Bir imha hedefi tablosu.</summary>
public sealed record EraseTarget(string Schema, string Table, string TenantColumn);
