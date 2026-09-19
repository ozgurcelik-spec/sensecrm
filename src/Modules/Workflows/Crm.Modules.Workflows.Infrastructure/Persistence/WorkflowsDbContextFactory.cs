using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crm.Modules.Workflows.Infrastructure.Persistence;

/// <summary>Design-time context for `dotnet ef migrations add` (connection: CRM_DATABASE env var or local default).</summary>
public sealed class WorkflowsDbContextFactory : IDesignTimeDbContextFactory<WorkflowsDbContext>
{
    public WorkflowsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkflowsDbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, WorkflowsDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WorkflowsDbContext(options, new TenantContext());
    }
}
