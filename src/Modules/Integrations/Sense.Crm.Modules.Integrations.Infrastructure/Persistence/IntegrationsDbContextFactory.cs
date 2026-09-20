using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Persistence;

/// <summary>Design-time context for `dotnet ef migrations add` (connection: CRM_DATABASE env var or local default).</summary>
public sealed class IntegrationsDbContextFactory : IDesignTimeDbContextFactory<IntegrationsDbContext>
{
    public IntegrationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, IntegrationsDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new IntegrationsDbContext(options, new TenantContext());
    }
}
