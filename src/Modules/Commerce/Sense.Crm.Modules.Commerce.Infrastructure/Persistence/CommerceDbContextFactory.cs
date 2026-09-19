using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Persistence;

namespace Sense.Crm.Modules.Commerce.Infrastructure.Persistence;

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
