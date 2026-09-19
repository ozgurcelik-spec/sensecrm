using Crm.Shared.Infrastructure.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crm.Shared.Infrastructure.Persistence;

/// <summary>
/// Kayıtlı tüm şemaların migration'larını uygular: önce ortak <c>audit</c> şeması (<see cref="AuditDbContext"/>), sonra
/// her modülün <see cref="ModuleDbContext"/>'i. Migrator ve entegrasyon testleri aynı yolu kullanır.
/// </summary>
public static partial class MigrationRunner
{
    public static async Task MigrateAllAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();

        if (scope.ServiceProvider.GetService<AuditDbContext>() is { } audit)
        {
            LogMigrating(logger, AuditDbContext.SchemaName);
            await audit.Database.MigrateAsync(ct).ConfigureAwait(false);
        }

        foreach (var db in scope.ServiceProvider.GetServices<ModuleDbContext>())
        {
            LogMigrating(logger, db.Schema);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Yalnız geliştirme: veritabanını tamamen siler ve tüm migration'ları yeniden uygular.</summary>
    public static async Task ResetAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        using (var scope = services.CreateScope())
        {
            var any = scope.ServiceProvider.GetServices<ModuleDbContext>().First();
            LogResetting(logger);
            await any.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
        }

        await MigrateAllAsync(services, logger, ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Migrating schema {Schema}")]
    private static partial void LogMigrating(ILogger logger, string schema);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Dropping the whole database before re-applying migrations")]
    private static partial void LogResetting(ILogger logger);
}
