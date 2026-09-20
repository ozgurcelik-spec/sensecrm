using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Infrastructure;
using Sense.Crm.Modules.Integrations.Infrastructure.Delivery;

namespace Sense.Crm.Worker.Integrations;

/// <summary>
/// Webhook teslimat döngüsü (M8B, D4): <b>dış çağrıyı yalnız Worker yapar</b>. <c>Integrations:Webhooks:Enabled=false</c> (varsayılan) iken boşta bekler (fan-out zaten satır yazmaz). Production'da <c>Enabled=true</c> ama
/// egress proxy/DNS yoksa servis <b>başlatılmaz</b> ve hata günlüğü yazılır (doğrudan çıkış yalnız Development/Testing). Her turda vadesi gelmiş satırları talep eder ve işler; iş yoksa <c>PollSeconds</c> bekler.
/// </summary>
public sealed partial class WebhookDispatcherService(WebhookDispatcher dispatcher, IOptions<IntegrationsOptions> options, ILogger<WebhookDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var w = settings.Webhooks;
        if (!w.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        var egressConfigured = !string.IsNullOrWhiteSpace(w.EgressProxy) && !string.IsNullOrWhiteSpace(w.DnsServer);
        if (!egressConfigured && !(settings.DevelopmentLike && w.AllowDirectEgress))
        {
            LogNoEgress(logger);
            return;
        }

        var interval = TimeSpan.FromSeconds(w.PollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRunFailed(logger, ex);
            }

            if (processed == 0)
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 8600, Level = LogLevel.Information, Message = "Webhook delivery is disabled (Integrations:Webhooks:Enabled=false); the dispatcher is idle.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 8601, Level = LogLevel.Error, Message = "Webhook delivery is enabled but no egress proxy/DNS is configured (Integrations:Webhooks:EgressProxy, DnsServer); the dispatcher will NOT start. Direct egress is Development/Testing only.")]
    private static partial void LogNoEgress(ILogger logger);

    [LoggerMessage(EventId = 8602, Level = LogLevel.Error, Message = "Webhook dispatcher round failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);
}

/// <summary>Günlük saklama (M8B): teslimat/deneme satırları (30 gün), süresi dolmuş önceki sırlar, <c>api_key_usage_daily</c> (180 gün). Açılışta ve sonra 24 saatte bir çalışır.</summary>
public sealed partial class IntegrationsRetentionService(IServiceScopeFactory scopes, ILogger<IntegrationsRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IntegrationsRetention>().RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 8603, Level = LogLevel.Error, Message = "Integrations retention pass failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
