using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;

namespace Sense.Crm.Shared.Infrastructure.Persistence;

/// <summary>
/// dotnet-ef design-time bağlantısı: <c>CRM_DATABASE</c> ortam değişkeni, yoksa yerel compose varsayılanı (parolasız;
/// migration üretimi veritabanına bağlanmaz). Modüllerin IDesignTimeDbContextFactory'leri bunu kullanır.
/// </summary>
public static class DesignTimeDefaults
{
    public const string ConnectionStringVariable = "CRM_DATABASE";
    public const string LocalConnectionString = "Host=127.0.0.1;Port=15432;Database=crm;Username=crm";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } cs ? cs : LocalConnectionString;
}

/// <summary>Ortak denetim şeması için design-time context (dotnet ef migrations add ... --context AuditDbContext).</summary>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(DesignTimeDefaults.ConnectionString, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, AuditDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AuditDbContext(options);
    }
}
