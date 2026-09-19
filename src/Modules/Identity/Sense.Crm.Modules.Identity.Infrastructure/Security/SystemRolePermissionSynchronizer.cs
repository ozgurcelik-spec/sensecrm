using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Application.Roles;
using Sense.Crm.Modules.Identity.Domain.Roles;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;

namespace Sense.Crm.Modules.Identity.Infrastructure.Security;

/// <summary>
/// Sistem rollerinin izinlerini güncel <see cref="SystemRoleDefinitions"/> + birleşik izin kataloğuna göre yeniden uygular
/// (idempotent). Yeni modül/izin anahtarları yalnız organizasyon kaydında yazıldığı için mevcut organizasyonlara bu yolla ulaşır.
/// Yalnız sistem rollerine dokunur (eksik sistem rolü oluşturulur); özel roller değişmez.
/// </summary>
public sealed partial class SystemRolePermissionSynchronizer(
    IdentityDbContext db,
    IPermissionCatalog catalog,
    ITenantContextSetter tenantSetter,
    IPermissionCacheInvalidator cache,
    ILogger<SystemRolePermissionSynchronizer> logger)
{
    /// <returns>Değişen (veya oluşturulan) sistem rolü sayısı.</returns>
    public async Task<int> SyncAllTenantsAsync(CancellationToken ct)
    {
        var tenantIds = await db.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct).ConfigureAwait(false);
        var total = 0;
        foreach (var tenantId in tenantIds)
        {
            total += await SyncTenantAsync(tenantId, ct).ConfigureAwait(false);
        }

        return total;
    }

    public async Task<int> SyncTenantAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = tenantSetter.BeginScope(tenantId);

        var systemRoles = await db.Roles.Where(r => r.IsSystem).ToListAsync(ct).ConfigureAwait(false);
        var changed = 0;
        foreach (var code in SystemRoleCodes.All)
        {
            var desired = SystemRoleDefinitions.PermissionsFor(code, catalog.All);
            var role = systemRoles.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.Ordinal));
            if (role is null)
            {
                db.Roles.Add(Role.CreateSystem(tenantId, code, desired));
                changed++;
            }
            else if (role.SyncPermissionsUnchecked(desired))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await cache.InvalidateTenantAsync(tenantId, ct).ConfigureAwait(false);
            LogTenantSynced(logger, tenantId, changed);
        }

        return changed;
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "System role permissions synchronized for tenant {TenantId}: {ChangedRoles} role(s) changed")]
    private static partial void LogTenantSynced(ILogger logger, Guid tenantId, int changedRoles);
}

/// <summary>API başlangıcında sistem rolü izin senkronizasyonunu bir kez çalıştırır; hata başlatmayı durdurmaz.</summary>
public sealed partial class SystemRolePermissionSyncHostedService(IServiceScopeFactory scopes, ILogger<SystemRolePermissionSyncHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var system = CurrentUserAccessor.UseSystem();
            await using var scope = scopes.CreateAsyncScope();
            var synchronizer = scope.ServiceProvider.GetRequiredService<SystemRolePermissionSynchronizer>();
            await synchronizer.SyncAllTenantsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Veritabanı henüz migrate edilmemiş olabilir (Migrator ayrı çalışır); bir sonraki başlatmada tekrar denenir.
            LogSyncFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "System role permission synchronization failed; it will be retried on next start")]
    private static partial void LogSyncFailed(ILogger logger, Exception exception);
}
