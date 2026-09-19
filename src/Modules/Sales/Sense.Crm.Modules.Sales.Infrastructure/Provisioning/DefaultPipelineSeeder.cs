using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Sales.Application;
using Sense.Crm.Modules.Sales.Domain.Pipelines;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;

namespace Sense.Crm.Modules.Sales.Infrastructure.Provisioning;

/// <summary>
/// <see cref="IDefaultPipelineSeeder"/>: organizasyonda hiç huni yoksa varsayılan huniyi organizasyon dilinde oluşturur.
/// Organizasyon başına PostgreSQL advisory lock (transaction ömürlü) eşzamanlı çağrıları (Worker olayı, API başlangıcı,
/// tembel yol) sıraya dizer; kilit alındıktan sonra yeniden kontrol edildiği için ikinci çağrı hiçbir şey yapmaz.
/// </summary>
public sealed partial class DefaultPipelineSeeder(SalesDbContext db, ITenantContextSetter tenants, ILogger<DefaultPipelineSeeder> logger) : IDefaultPipelineSeeder
{
    public async Task<bool> EnsureAsync(Guid tenantId, string locale, CancellationToken ct)
    {
        using var tenantScope = tenants.BeginScope(tenantId);
        var created = false;

        await db.ExecuteInTransactionAsync(async token =>
        {
            var lockKey = $"sales.default_pipeline:{tenantId:N}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", token).ConfigureAwait(false);

            if (await db.Pipelines.AnyAsync(token).ConfigureAwait(false))
            {
                return;
            }

            var pipeline = Pipeline.Create(tenantId, DefaultPipelineTemplate.PipelineName(locale), isDefault: true, DefaultPipelineTemplate.Stages(locale));
            db.Pipelines.Add(pipeline.Value);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            created = true;
        }, ct).ConfigureAwait(false);

        if (created)
        {
            LogSeeded(logger, tenantId, locale);
        }

        return created;
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Information, Message = "Default sales pipeline seeded for tenant {TenantId} ({Locale})")]
    private static partial void LogSeeded(ILogger logger, Guid tenantId, string locale);
}

/// <summary>
/// API başlangıcında M1'de (veya Worker'dan önce) açılmış tüm organizasyonlara varsayılan huniyi tohumlar (idempotent);
/// hata başlatmayı durdurmaz, bir sonraki başlatmada tekrar denenir. <c>SystemRolePermissionSyncHostedService</c> ile aynı desen.
/// </summary>
public sealed partial class DefaultPipelineSyncHostedService(IServiceScopeFactory scopes, ILogger<DefaultPipelineSyncHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var system = CurrentUserAccessor.UseSystem();
            await using var scope = scopes.CreateAsyncScope();
            var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            var tenants = await directory.ListAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (var tenant in tenants)
            {
                // Her organizasyon kendi kapsamında (temiz DbContext) tohumlanır.
                await using var tenantScope = scopes.CreateAsyncScope();
                var seeder = tenantScope.ServiceProvider.GetRequiredService<IDefaultPipelineSeeder>();
                await seeder.EnsureAsync(tenant.Id, tenant.DefaultLocale, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Veritabanı henüz migrate edilmemiş olabilir (Migrator ayrı çalışır); bir sonraki başlatmada tekrar denenir.
            LogSeedFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Default sales pipeline seeding failed; it will be retried on next start")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception);
}
