using Crm.Migrator;
using Crm.Modules.Activities.Infrastructure.Persistence;
using Crm.Modules.Identity.Application;
using Crm.Modules.Identity.Infrastructure;
using Crm.Modules.Identity.Infrastructure.Persistence;
using Crm.Modules.Sales.Infrastructure.Persistence;
using Crm.Modules.Workflows.Infrastructure.Persistence;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Kullanım: dotnet run --project src/Crm.Migrator -- [migrate|reset|create-platform-admin]
// Yeni modül eklendiğinde DbContext'i buraya da kaydedilir (build/new-module.ps1 çıktısındaki adımlar).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddCrmCore(builder.Configuration);
builder.Services.AddAuditStore(builder.Configuration);
builder.Services.AddModuleDbContext<IdentityDbContext>(builder.Configuration, IdentityDbContext.SchemaName);
// create-platform-admin için: Identity provisioning servisleri + tüm modüllerin izin kataloğu + outbox çözümlemesi (OrganizationCreated).
builder.Services.AddIdentityProvisioning(builder.Configuration);
builder.Services.AddSingleton<IPermissionCatalog, PlatformAdminCommand.StaticPermissionCatalog>();
builder.Services.AddModuleHandlers(
    IdentityDbContext.SchemaName,
    typeof(Crm.Modules.Identity.Domain.IUserRepository).Assembly,
    typeof(Crm.Modules.Identity.Contracts.OrgPermissions).Assembly);
builder.Services.AddModuleDbContext<SalesDbContext>(builder.Configuration, SalesDbContext.SchemaName);
builder.Services.AddModuleDbContext<ActivitiesDbContext>(builder.Configuration, ActivitiesDbContext.SchemaName);
builder.Services.AddModuleDbContext<WorkflowsDbContext>(builder.Configuration, WorkflowsDbContext.SchemaName);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(MigratorConstants.LoggerName);
var command = args.Length > 0 ? args[0].ToLowerInvariant() : MigratorConstants.MigrateCommand;

switch (command)
{
    case MigratorConstants.MigrateCommand:
        await MigrationRunner.MigrateAllAsync(host.Services, logger);
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

namespace Crm.Migrator
{
    internal static class MigratorConstants
    {
        public const string LoggerName = "Crm.Migrator";
        public const string MigrateCommand = "migrate";
        public const string ResetCommand = "reset";
        public const string UnknownCommandMessage = "Unknown command (or 'reset' outside Development): {Command}";
        public const string DoneMessage = "Completed: {Command}";
    }
}
