using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sense.Crm.Worker;

/// <summary>
/// Worker'ın HTTP ucu yoktur; konteyner <c>HEALTHCHECK</c>'i için her 15 sn'de bir sinyal dosyasının zamanını yeniler
/// (<c>Worker:HeartbeatFile</c>, varsayılan <c>/tmp/crm-worker.heartbeat</c>). Dockerfile dosyanın 60 sn içinde yenilenmiş olmasını
/// bekler; süreç kilitlenirse ya da kapanırsa dosya eskir ve konteyner "unhealthy" olur.
/// </summary>
public sealed partial class HeartbeatService(IConfiguration configuration, ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = configuration["Worker:HeartbeatFile"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "crm-worker.heartbeat");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await File.WriteAllTextAsync(path, DateTimeOffset.UtcNow.ToString("O"), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogHeartbeatFailed(logger, ex, path);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Could not write worker heartbeat file {Path}")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, string path);
}
