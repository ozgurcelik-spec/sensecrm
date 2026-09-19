using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crm.Modules.Activities.Infrastructure.Persistence;

/// <summary>Design-time context for `dotnet ef migrations add` (connection: CRM_DATABASE env var or local default).</summary>
public sealed class ActivitiesDbContextFactory : IDesignTimeDbContextFactory<ActivitiesDbContext>
{
    public ActivitiesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ActivitiesDbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, ActivitiesDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ActivitiesDbContext(options, new TenantContext());
    }
}
