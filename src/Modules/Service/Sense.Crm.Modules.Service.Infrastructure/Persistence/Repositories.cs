using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sense.Crm.Modules.Service.Application;
using Sense.Crm.Modules.Service.Domain;
using Sense.Crm.Modules.Service.Domain.Cases;
using Sense.Crm.Modules.Service.Domain.Sla;
using Sense.Crm.Shared.Contracts.Context;

namespace Sense.Crm.Modules.Service.Infrastructure.Persistence;

// Tüm sorgular ModuleDbContext'in "Tenant" ve "SoftDelete" global filtreleri altında çalışır.

public sealed class CaseRepository(ServiceDbContext db) : ICaseRepository
{
    public Task<Case?> GetByIdAsync(Guid id, CancellationToken ct) => db.Cases.FirstOrDefaultAsync(c => c.Id == id, ct);

    public void Add(Case entity) => db.Cases.Add(entity);

    public void Remove(Case entity) => db.Cases.Remove(entity);

    public void AddEvent(CaseEvent caseEvent) => db.CaseEvents.Add(caseEvent);

    public void AddComment(CaseComment comment) => db.CaseComments.Add(comment);

    public async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // EF, açık transaction içinde başarısız SaveChanges'i savepoint'e geri alır (yorum/olay/denetim satırları dahil);
            // izlenen varlıklar bırakılır ki işleyici güncel satırı yeniden yükleyebilsin.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

public sealed class SlaPolicyRepository(ServiceDbContext db) : ISlaPolicyRepository
{
    public async Task<IReadOnlyList<SlaPolicy>> ListAsync(CancellationToken ct) =>
        (await db.SlaPolicies.ToListAsync(ct).ConfigureAwait(false)).OrderBy(p => p.Priority).ToList();

    public Task<SlaPolicy?> GetAsync(CasePriority priority, CancellationToken ct) => db.SlaPolicies.FirstOrDefaultAsync(p => p.Priority == priority, ct);
}

/// <summary>
/// <see cref="ICaseNumberGenerator"/>: <c>C-{yıl}-{sıra:D4}</c> (9999'u aşarsa basamak artar). Tek ham SQL, talep INSERT'iyle aynı
/// transaction'da (komut pipeline'ının açtığı): <c>ON CONFLICT DO UPDATE</c> satır kilidi eşzamanlı oluşturmaları commit'e kadar sıraya
/// dizer → numara benzersiz ve boşluksuz; transaction geri alınırsa sayaç da geri alınır. <c>(tenant, number)</c> benzersiz indeksi ikinci
/// emniyettir. Ham SQL <c>TenantId</c>'yi <see cref="ITenantContext"/>'ten kendisi yazar.
/// </summary>
public sealed class CaseNumberGenerator(ServiceDbContext db, ITenantContext tenant) : ICaseNumberGenerator
{
    private const string IncrementSql =
        "INSERT INTO service.case_counters (tenant_id, year, last_value) VALUES (@tenant, @year, 1) " +
        "ON CONFLICT (tenant_id, year) DO UPDATE SET last_value = case_counters.last_value + 1 RETURNING last_value";

    private const string NumberFormat = "D4";
    private const string Prefix = "C-";

    public async Task<string> NextAsync(int year, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var command = db.Database.GetDbConnection().CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = IncrementSql;
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
                AddParameter(command, "tenant", tenant.TenantId);
                AddParameter(command, "year", year);

                var next = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{year}-{next.ToString(NumberFormat, CultureInfo.InvariantCulture)}");
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
