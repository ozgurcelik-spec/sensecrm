using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crm.Modules.Commerce.Infrastructure.Persistence;

/// <summary>Design-time context for `dotnet ef migrations add` (connection: CRM_DATABASE env var or local default).</summary>
public sealed class CommerceDbContextFactory : IDesignTimeDbContextFactory<CommerceDbContext>
{
    public CommerceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CommerceDbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, CommerceDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CommerceDbContext(options, new TenantContext());
    }
}
