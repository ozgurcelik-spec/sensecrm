using Crm.Modules.Identity.Infrastructure.Persistence;
using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Infrastructure.DependencyInjection;
using Crm.Shared.Infrastructure.Persistence;
using Crm.Shared.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Worker (K5): modül outbox'larını boşaltır (domain event → aynı modül handler'ları, integration event → IEventBus).
// Zamanlanmış işler, bildirim/e-posta teslimi ve gerçek zamanlı yayın MVP'de yok; yalnız OutboxPollingService<T> kalır.
// Yeni modül: DbContext + Domain/Contracts assembly'leri (EventTypeRegistry için) + OutboxPollingService<TContext> eklenir.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddCrmCore(builder.Configuration);

builder.Services.AddModuleDbContext<IdentityDbContext>(builder.Configuration, IdentityDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    IdentityDbContext.SchemaName,
    typeof(Crm.Modules.Identity.Domain.IUserRepository).Assembly,
    typeof(Crm.Modules.Identity.Contracts.OrgPermissions).Assembly);
builder.Services.AddHostedService<Crm.Worker.OutboxPollingService<IdentityDbContext>>();

await builder.Build().RunAsync();

namespace Crm.Worker
{
    /// <summary>Bir modülün outbox'ını periyodik olarak işler; iş yoksa <see cref="OutboxOptions.PollingIntervalSeconds"/> bekler.</summary>
    public sealed partial class OutboxPollingService<TContext>(
        IServiceProvider services,
        IOptions<OutboxOptions> options,
        ILogger<OutboxPollingService<TContext>> logger) : BackgroundService
        where TContext : ModuleDbContext
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds);
            while (!stoppingToken.IsCancellationRequested)
            {
                var processed = 0;
                try
                {
                    using var scope = services.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor<TContext>>();
                    processed = await processor.ProcessAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Veritabanı henüz hazır/migrate edilmemiş olabilir; süreç çökmeden bir sonraki turda tekrar denenir.
                    LogPollFailed(logger, ex, typeof(TContext).Name);
                }

                if (processed == 0)
                {
                    await Task.Delay(interval, stoppingToken);
                }
            }
        }

        [LoggerMessage(EventId = 4000, Level = LogLevel.Error, Message = "Outbox polling failed for {Context}")]
        private static partial void LogPollFailed(ILogger logger, Exception exception, string context);
    }
}
