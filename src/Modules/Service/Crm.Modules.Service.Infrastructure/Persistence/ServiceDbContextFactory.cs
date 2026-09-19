using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crm.Modules.Service.Infrastructure.Persistence;

/// <summary>Design-time context for `dotnet ef migrations add` (connection: CRM_DATABASE env var or local default).</summary>
public sealed class ServiceDbContextFactory : IDesignTimeDbContextFactory<ServiceDbContext>
{
    public ServiceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ServiceDbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, ServiceDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ServiceDbContext(options, new TenantContext());
    }
}
