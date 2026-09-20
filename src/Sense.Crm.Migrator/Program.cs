using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sense.Crm.Migrator;
using Sense.Crm.Modules.Activities.Infrastructure;
using Sense.Crm.Modules.Activities.Infrastructure.Persistence;
using Sense.Crm.Modules.Commerce.Infrastructure;
using Sense.Crm.Modules.Commerce.Infrastructure.Persistence;
using Sense.Crm.Modules.Files.Infrastructure;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Infrastructure;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Modules.Integrations.Infrastructure;
using Sense.Crm.Modules.Marketing.Infrastructure;
using Sense.Crm.Modules.Platform.Infrastructure;
using Sense.Crm.Modules.Sales.Infrastructure;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Modules.Service.Infrastructure;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Modules.Workflows.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Persistence;

// Kullanım: dotnet run --project src/Sense.Crm.Migrator -- [migrate|reset|create-platform-admin|sync-plans|backfill|erase-deleted-tenants|reencrypt-integration-secrets|files-reconcile]
// Yeni modül eklendiğinde DbContext'i buraya da kaydedilir (build/new-module.ps1 çıktısındaki adımlar).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Docker secret dosyalari (/run/secrets/<Ad>; "__" = ":"): Integrations__Encryption__Keys__k1 (reencrypt-integration-secrets) (M8B), Files__Storage__AccessKey (M8C).
builder.Configuration.AddDockerSecrets();
builder.Services.AddCrmCore(builder.Configuration);
builder.Services.AddAuditStore(builder.Configuration);
builder.Services.AddModuleDbContext<IdentityDbContext>(builder.Configuration, IdentityDbContext.SchemaName);
// create-platform-admin için: Identity provisioning servisleri + tüm modüllerin izin kataloğu + outbox çözümlemesi (OrganizationCreated).
builder.Services.AddIdentityProvisioning(builder.Configuration);
builder.Services.AddSingleton<IPermissionCatalog, PlatformAdminCommand.StaticPermissionCatalog>();
builder.Services.AddModuleHandlers(
    IdentityDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Identity.Domain.IUserRepository).Assembly,
    typeof(Sense.Crm.Modules.Identity.Contracts.OrgPermissions).Assembly);
builder.Services.AddModuleDbContext<SalesDbContext>(builder.Configuration, SalesDbContext.SchemaName);
builder.Services.AddModuleDbContext<ActivitiesDbContext>(builder.Configuration, ActivitiesDbContext.SchemaName);
builder.Services.AddModuleDbContext<WorkflowsDbContext>(builder.Configuration, WorkflowsDbContext.SchemaName);
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext>(builder.Configuration, Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext.SchemaName);
builder.Services.AddModuleDbContext<CommerceDbContext>(builder.Configuration, CommerceDbContext.SchemaName);
builder.Services.AddModuleDbContext<ServiceDbContext>(builder.Configuration, ServiceDbContext.SchemaName);

// Platform (M7): plan kataloğu senkronu (sync-plans), mevcut kiracı backfill'i (backfill) ve KVKK imha yeniden koşusu (erase-deleted-tenants).
// Domain + Contracts assembly'leri outbox olay tipi kaydı ve UnitOfWork çözümlemesi içindir.
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Platform.Infrastructure.Persistence.PlatformDbContext>(builder.Configuration, Sense.Crm.Modules.Platform.Infrastructure.Persistence.PlatformDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    Sense.Crm.Modules.Platform.Infrastructure.Persistence.PlatformDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Platform.Domain.Accounts.TenantAccount).Assembly,
    typeof(Sense.Crm.Modules.Platform.Contracts.TenantSuspended).Assembly);
builder.Services.AddIdentityContractServices();
builder.Services.AddPlatformContractServices(builder.Configuration);

// Files (M8C): FilesDbContext + nesne imhası (erase-deleted-tenants geri yükleme sonrası nesneleri yeniden siler) + files-reconcile. Uzlaştırmanın kayıt-yok süpürmesi tüm
// modüllerin IAttachmentTarget'larını ister (Add<Modül>ContractServices).
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Files.Infrastructure.Persistence.FilesDbContext>(builder.Configuration, Sense.Crm.Modules.Files.Infrastructure.Persistence.FilesDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    Sense.Crm.Modules.Files.Infrastructure.Persistence.FilesDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Files.Domain.FileAttachment).Assembly,
    typeof(Sense.Crm.Modules.Files.Contracts.FileAttached).Assembly);
builder.Services.AddFilesContractServices(builder.Configuration);
builder.Services.AddSalesContractServices();
builder.Services.AddActivitiesContractServices();
builder.Services.AddCommerceContractServices();
builder.Services.AddServiceContractServices();
builder.Services.AddMarketingContractServices();

// Integrations (M8B): şema + KVKK imha adımları (delivery_queue) + webhook sırrı anahtar döndürme (reencrypt-integration-secrets).
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Integrations.Infrastructure.Persistence.IntegrationsDbContext>(builder.Configuration, Sense.Crm.Modules.Integrations.Infrastructure.Persistence.IntegrationsDbContext.SchemaName);
builder.Services.AddIntegrationsContractServices(builder.Configuration);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(MigratorConstants.LoggerName);
var command = args.Length > 0 ? args[0].ToLowerInvariant() : MigratorConstants.MigrateCommand;

switch (command)
{
    case MigratorConstants.MigrateCommand:
        await MigrationRunner.MigrateAllAsync(host.Services, logger);

        // M7: plan senkronu (geçersiz yapılandırma → çıkış ≠ 0, yayın durur) + mevcut kiracılar için internal plan backfill'i.
        var syncExit = await PlatformCommands.SyncPlansAsync(host.Services, logger, CancellationToken.None);
        if (syncExit != 0)
        {
            return syncExit;
        }

        await PlatformCommands.BackfillAsync(host.Services, logger, CancellationToken.None);
        break;

    case PlatformCommands.SyncPlansName:
        var syncOnlyExit = await PlatformCommands.SyncPlansAsync(host.Services, logger, CancellationToken.None);
        if (syncOnlyExit != 0)
        {
            return syncOnlyExit;
        }

        break;

    case PlatformCommands.BackfillName:
        await PlatformCommands.BackfillAsync(host.Services, logger, CancellationToken.None);
        break;

    case PlatformCommands.EraseDeletedTenantsName:
        await PlatformCommands.EraseDeletedTenantsAsync(host.Services, logger, CancellationToken.None);
        break;

    case FilesCommands.ReconcileName:
        var reconcileExit = await FilesCommands.ReconcileAsync(args, host.Services, logger, CancellationToken.None);
        if (reconcileExit != 0)
        {
            return reconcileExit;
        }

        break;

    case IntegrationsSecretsCommand.Name:
        var reencryptExit = await IntegrationsSecretsCommand.RunAsync(host.Services, logger, CancellationToken.None);
        if (reencryptExit != 0)
        {
            return reencryptExit;
        }

        break;

    case PlatformAdminCommand.Name:
        var exitCode = await PlatformAdminCommand.RunAsync(host.Services, logger, CancellationToken.None);
        if (exitCode != 0)
        {
            return exitCode;
        }

        break;

    case MigratorConstants.ResetCommand when host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment():
        await MigrationRunner.ResetAsync(host.Services, logger);
        break;

    default:
        logger.LogError(MigratorConstants.UnknownCommandMessage, command);
        return 1;
}

logger.LogInformation(MigratorConstants.DoneMessage, command);
return 0;

namespace Sense.Crm.Migrator
{
    internal static class MigratorConstants
    {
        public const string LoggerName = "Sense.Crm.Migrator";
        public const string MigrateCommand = "migrate";
        public const string ResetCommand = "reset";
        public const string UnknownCommandMessage = "Unknown command (or 'reset' outside Development): {Command}";
        public const string DoneMessage = "Completed: {Command}";
    }
}
