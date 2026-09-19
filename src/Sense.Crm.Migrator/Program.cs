using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sense.Crm.Migrator;
using Sense.Crm.Modules.Activities.Infrastructure.Persistence;
using Sense.Crm.Modules.Commerce.Infrastructure.Persistence;
using Sense.Crm.Modules.Identity.Application;
using Sense.Crm.Modules.Identity.Infrastructure;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Modules.Workflows.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Persistence;

// Kullanım: dotnet run --project src/Sense.Crm.Migrator -- [migrate|reset|create-platform-admin]
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
    typeof(Sense.Crm.Modules.Identity.Domain.IUserRepository).Assembly,
    typeof(Sense.Crm.Modules.Identity.Contracts.OrgPermissions).Assembly);
builder.Services.AddModuleDbContext<SalesDbContext>(builder.Configuration, SalesDbContext.SchemaName);
builder.Services.AddModuleDbContext<ActivitiesDbContext>(builder.Configuration, ActivitiesDbContext.SchemaName);
builder.Services.AddModuleDbContext<WorkflowsDbContext>(builder.Configuration, WorkflowsDbContext.SchemaName);
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext>(builder.Configuration, Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext.SchemaName);
builder.Services.AddModuleDbContext<CommerceDbContext>(builder.Configuration, CommerceDbContext.SchemaName);
builder.Services.AddModuleDbContext<ServiceDbContext>(builder.Configuration, ServiceDbContext.SchemaName);

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
