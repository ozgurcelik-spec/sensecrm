using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Modules.Platform.Domain;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Platform.Infrastructure.Jobs;

/// <summary>
/// Denetim tablolarının (<c>audit.audit_log_entries</c>, <c>platform.platform_audit_entries</c>) <b>bakım işareti</b> (C-SEC2 M3). Tablolar veritabanı tetikleyicileriyle salt-eklemelidir;
/// imha ve saklama işleri işlem başına <c>SET LOCAL crm.audit_maintenance = '…'</c> (<c>set_config(…, true)</c>) yazar ve tetikleyici işareti <b>koşullarıyla birlikte</b> doğrular:
/// <c>erasure</c> yalnız <c>pending_deletion|deleted</c> kiracının satırları için, <c>retention</c> yalnız 30 günden eski platform denetim satırları için.
/// </summary>
public static class AuditMaintenance
{
    public const string Setting = "crm.audit_maintenance";
    public const string Erasure = "erasure";
    public const string Retention = "retention";

    /// <summary>Geçerli işlem için işareti yazar (işlem bitince kendiliğinden kalkar). Bir işlem (transaction) içinde çağrılmalıdır.</summary>
    public static Task SetLocalAsync(DbContext db, string marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlAsync($"SELECT set_config({Setting}, {marker}, true)", ct);
    }
}

/// <summary>Tombstone öncesi doğrulama başarısız: kiracı kimlikli tabloda ya da bir adımın deposunda kalıntı var (<c>erasure.verification_failed</c>).</summary>
public sealed class ErasureVerificationException(IReadOnlyList<string> problems)
    : Exception(PlatformErrors.ErasureVerificationFailed + ": " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// İmha sonrası <b>veritabanı doğrulaması</b> (C-SEC2 M2): <c>information_schema</c>'da <c>tenant_id</c> kolonu olan <b>her</b> taban tabloyu (tüm şemalar) tarar ve kiracıya ait satır kalıp
/// kalmadığını denetler. Elle kaydedilen DbContext listesine ya da EF modeline dayanmaz: unutulan bir modül/tablo tombstone'dan önce yakalanır. Kalıcı tutulan tablolar
/// (<see cref="RetainedTables"/>) hariçtir. Kiracı kimliği her zaman parametredir; tablo adları <c>information_schema</c>'dan gelir ve tırnaklanır.
/// </summary>
public sealed class TenantErasureVerifier(PlatformDbContext db)
{
    /// <summary>İmhadan sonra bilerek kalan tablolar: mezar taşı hesabı ve talep kanıtı (kişisel veri içermez).</summary>
    public static IReadOnlyList<(string Schema, string Table)> RetainedTables { get; } =
    [
        (PlatformDbContext.SchemaName, PlatformTables.TenantAccounts),
        (PlatformDbContext.SchemaName, PlatformTables.DeletionRequests),
    ];

    /// <summary>Kiracı kimlikli (<c>tenant_id</c>) tüm taban tablolar (<c>schema.table</c>), kalıcı tutulanlar hariç.</summary>
    public async Task<IReadOnlyList<(string Schema, string Table)>> ListTenantTablesAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<string>(
            $"""
            SELECT c.table_schema || '.' || c.table_name AS "Value"
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.column_name = 'tenant_id'
              AND t.table_type = 'BASE TABLE'
              AND c.table_schema NOT IN ('pg_catalog', 'information_schema', 'public')
            ORDER BY 1
            """).ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => { var i = r.IndexOf('.', StringComparison.Ordinal); return (Schema: r[..i], Table: r[(i + 1)..]); })
            .Where(t => !RetainedTables.Contains(t))
            .ToList();
    }

    /// <summary>Hâlâ satırı olan tablolar (<c>schema.table=count</c>); boşsa kalıntı yoktur.</summary>
    public async Task<IReadOnlyList<string>> FindRemainingAsync(Guid tenantId, CancellationToken ct)
    {
        var remaining = new List<string>();
        foreach (var (schema, table) in await ListTenantTablesAsync(ct).ConfigureAwait(false))
        {
            var sql = string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT count(*) AS \"Value\" FROM \"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WHERE tenant_id = {{0}}");
            var count = await db.Database.SqlQueryRaw<long>(sql, tenantId).SingleAsync(ct).ConfigureAwait(false);
            if (count > 0)
            {
                remaining.Add(string.Create(CultureInfo.InvariantCulture, $"{schema}.{table}={count}"));
            }
        }

        return remaining;
    }
}
