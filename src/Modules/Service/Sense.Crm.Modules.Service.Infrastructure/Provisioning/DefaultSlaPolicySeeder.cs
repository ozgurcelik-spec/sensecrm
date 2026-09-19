using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Service.Application;
using Sense.Crm.Modules.Service.Domain.Sla;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;

namespace Sense.Crm.Modules.Service.Infrastructure.Provisioning;

/// <summary>
/// <see cref="IDefaultSlaPolicySeeder"/>: organizasyonda eksik öncelikler varsayılan SLA değerleriyle eklenir (dört satır tamamsa hiçbir şey
/// yapmaz). Organizasyon başına PostgreSQL advisory lock (transaction ömürlü) eşzamanlı çağrıları (Worker olayı, API başlangıcı, tembel yol)
/// sıraya dizer; kilit alındıktan sonra yeniden kontrol edildiği için ikinci çağrı hiçbir şey yapmaz. Dile bağlı metin olmadığından locale yok.
/// </summary>
public sealed partial class DefaultSlaPolicySeeder(ServiceDbContext db, ITenantContextSetter tenants, ILogger<DefaultSlaPolicySeeder> logger) : IDefaultSlaPolicySeeder
{
    public async Task<bool> EnsureAsync(Guid tenantId, CancellationToken ct)
    {
        using var tenantScope = tenants.BeginScope(tenantId);
        var created = false;

        await db.ExecuteInTransactionAsync(async token =>
        {
            var lockKey = $"service.sla_policies:{tenantId:N}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", token).ConfigureAwait(false);

            var existing = await db.SlaPolicies.Select(p => p.Priority).ToListAsync(token).ConfigureAwait(false);
            var missing = SlaPolicyDefaults.Priorities.Where(p => !existing.Contains(p)).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            db.SlaPolicies.AddRange(missing.Select(p => SlaPolicy.CreateDefault(tenantId, p)));
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            created = true;
        }, ct).ConfigureAwait(false);

        if (created)
        {
            LogSeeded(logger, tenantId);
        }

        return created;
    }

    [LoggerMessage(EventId = 6100, Level = LogLevel.Information, Message = "Default SLA policies seeded for tenant {TenantId}")]
    private static partial void LogSeeded(ILogger logger, Guid tenantId);
}

/// <summary>
/// API başlangıcında M1–M6A'da açılmış tüm organizasyonlara varsayılan SLA politikalarını tohumlar (idempotent); hata başlatmayı
/// durdurmaz, bir sonraki başlatmada tekrar denenir. <c>DefaultPipelineSyncHostedService</c> ile aynı desen.
/// </summary>
public sealed partial class DefaultSlaPolicySyncHostedService(IServiceScopeFactory scopes, ILogger<DefaultSlaPolicySyncHostedService> logger) : IHostedService
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
                var seeder = tenantScope.ServiceProvider.GetRequiredService<IDefaultSlaPolicySeeder>();
                await seeder.EnsureAsync(tenant.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Veritabanı henüz migrate edilmemiş olabilir (Migrator ayrı çalışır); bir sonraki başlatmada tekrar denenir.
            LogSeedFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Default SLA policy seeding failed; it will be retried on next start")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception);
}
