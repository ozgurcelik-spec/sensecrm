using System.Globalization;
using Crm.Modules.Commerce.Application;
using Crm.Modules.Commerce.Domain;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Kernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Crm.Modules.Commerce.Infrastructure.Persistence;

/// <summary>
/// Numara sayacı: <c>commerce.document_counters</c> satırı yalnız bu tek atomik ifadeyle yazılır (belge INSERT'iyle <b>aynı
/// transaction</b>'da). Satır kilidi transaction bitene kadar tutulur → aynı <c>(tenant, kind, year)</c> için eşzamanlı ayırmalar sıraya girer,
/// aynı numara çıkmaz; transaction (veya savepoint) geri alınırsa sayaç da geri döner (boşluksuz). <c>TenantId</c> her zaman
/// <see cref="ITenantContext"/>'ten açıkça parametre verilir.
/// </summary>
public sealed class DocumentNumberAllocator(CommerceDbContext db, ITenantContext tenant) : IDocumentNumberAllocator
{
    private const string NoTransaction = "Document numbers can only be allocated inside the command's unit-of-work transaction.";

    // Şema/tablo/kolon adları CommerceDbContext + snake_case adlandırma ile birebir aynıdır.
    private const string Sql = $"""
        INSERT INTO {CommerceDbContext.SchemaName}.{CommerceTables.DocumentCounters} (tenant_id, kind, year, last_value)
        VALUES (@tenant, @kind, @year, 1)
        ON CONFLICT (tenant_id, kind, year)
        DO UPDATE SET last_value = {CommerceTables.DocumentCounters}.last_value + 1
        RETURNING last_value;
        """;

    public async Task<long> NextAsync(string kind, int year, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException(NoTransaction);
        await using var command = new NpgsqlCommand(Sql, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
        command.Parameters.AddWithValue("tenant", tenant.TenantId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("year", year);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// <see cref="ICommerceTransaction"/>: UnitOfWork transaction'ı içinde savepoint + SaveChanges + çakışma eşlemesi
/// (bkz. arayüz belgesi). Aktif transaction yoksa (komut pipeline dışında çağrılmış) <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class CommerceTransaction(CommerceDbContext db) : ICommerceTransaction
{
    private const string Savepoint = "commerce_operation";
    private const string UniqueViolation = "23505";
    private const string NoTransaction = "Commerce commands must run inside the unit-of-work transaction.";

    public async Task<Result> ExecuteAsync(Func<CancellationToken, Task<Result>> work, CancellationToken ct)
    {
        var result = await ExecuteCoreAsync<bool>(
            async token =>
            {
                var inner = await work(token).ConfigureAwait(false);
                return inner.IsSuccess ? Result.Success(true) : Result.Failure<bool>(inner.Error);
            },
            ct).ConfigureAwait(false);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> work, CancellationToken ct) => ExecuteCoreAsync(work, ct);

    private async Task<Result<T>> ExecuteCoreAsync<T>(Func<CancellationToken, Task<Result<T>>> work, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException(NoTransaction);
        await transaction.CreateSavepointAsync(Savepoint, ct).ConfigureAwait(false);
        try
        {
            var result = await work(ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                await transaction.RollbackToSavepointAsync(Savepoint, ct).ConfigureAwait(false);
                return result;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.ReleaseSavepointAsync(Savepoint, ct).ConfigureAwait(false);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackToSavepointAsync(Savepoint, ct).ConfigureAwait(false);
            return Error.Conflict(CommerceErrors.ConcurrentUpdate);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation } violation && MapConflict(violation.ConstraintName) is { } error)
        {
            await transaction.RollbackToSavepointAsync(Savepoint, ct).ConfigureAwait(false);
            return error;
        }
    }

    private static Error? MapConflict(string? constraint) => constraint switch
    {
        CommerceTables.ProductCodeIndex => Error.Conflict(CommerceErrors.ProductCodeTaken),
        CommerceTables.OrderQuoteIndex => Error.Conflict(CommerceErrors.QuoteAlreadyConverted),

        // Numara yedek güvencesi: sayaç serileştirdiği için yalnız beklenmeyen yarışta oluşur; istemci yeniden dener.
        CommerceTables.QuoteNumberIndex or CommerceTables.OrderNumberIndex => Error.Conflict(CommerceErrors.ConcurrentUpdate),
        _ => null,
    };
}
