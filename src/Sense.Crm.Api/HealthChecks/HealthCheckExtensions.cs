using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sense.Crm.Shared.Contracts.Configuration;

namespace Sense.Crm.Api.HealthChecks;

/// <summary>
/// Sağlık kontrolleri: postgres ("ready" etiketli dış bağımlılık; Redis isteğe bağlı önbellek olduğundan hazır olma koşulu değildir).
/// /health tam rapor, /health/live yalnızca sürecin ayakta olduğunu (bağımlılık kontrolü yok),
/// /health/ready dış bağımlılıkların hazır olduğunu doğrular (bkz. Microsoft'un liveness/readiness deseni).
/// </summary>
public static class HealthCheckExtensions
{
    public static IServiceCollection AddCrmHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var databaseConnection = configuration.GetConnectionString(ConnectionStringNames.Database);

        var builder = services.AddHealthChecks();

        if (!string.IsNullOrWhiteSpace(databaseConnection))
        {
            builder.AddNpgSql(databaseConnection, name: "postgres", tags: [HostConstants.HealthReadyTag]);
        }

        return services;
    }

    /// <summary>/health, /health/live, /health/ready uçlarını eşler.</summary>
    public static IEndpointRouteBuilder MapCrmHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(HostConstants.HealthPath, new HealthCheckOptions
        {
            ResponseWriter = WriteReportAsync,
        });

        endpoints.MapHealthChecks(HostConstants.HealthLivePath, new HealthCheckOptions
        {
            // Bağımlılık kontrolü yok: yalnızca sürecin istek karşılayabildiğini doğrular.
            Predicate = _ => false,
            ResponseWriter = WriteReportAsync,
        });

        endpoints.MapHealthChecks(HostConstants.HealthReadyPath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HostConstants.HealthReadyTag),
            ResponseWriter = WriteReportAsync,
        });

        return endpoints;
    }

    private static Task WriteReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = e.Value.Duration.TotalMilliseconds,
                tags = e.Value.Tags,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
