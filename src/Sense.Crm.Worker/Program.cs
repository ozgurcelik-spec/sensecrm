using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Activities.Infrastructure;
using Sense.Crm.Modules.Activities.Infrastructure.Persistence;
using Sense.Crm.Modules.Commerce.Infrastructure;
using Sense.Crm.Modules.Identity.Infrastructure;
using Sense.Crm.Modules.Identity.Infrastructure.Persistence;
using Sense.Crm.Modules.Integrations.Infrastructure;
using Sense.Crm.Modules.Marketing.Infrastructure;
using Sense.Crm.Modules.Platform.Infrastructure;
using Sense.Crm.Modules.Platform.Infrastructure.Persistence;
using Sense.Crm.Modules.Sales.Infrastructure;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Modules.Service.Infrastructure;
using Sense.Crm.Modules.Service.Infrastructure.Persistence;
using Sense.Crm.Modules.Workflows.Infrastructure;
using Sense.Crm.Modules.Workflows.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Infrastructure.Observability;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Outbox;
using Sense.Crm.Worker.Observability;

// Worker (K5): modül outbox'larını boşaltır (domain event → aynı modül handler'ları, integration event → IEventBus).
// Zamanlanmış işler, bildirim/e-posta teslimi ve gerçek zamanlı yayın MVP'de yok; yalnız OutboxPollingService<T> kalır.
// Yeni modül: DbContext + Domain/Contracts assembly'leri (EventTypeRegistry için) + OutboxPollingService<TContext> eklenir.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Docker secret dosyalari (/run/secrets/<Ad>; "__" = ":"): Integrations__Encryption__Keys__k1 vb. (M8B).
builder.Configuration.AddDockerSecrets();
builder.Services.AddCrmCore(builder.Configuration);

builder.Services.AddModuleDbContext<IdentityDbContext>(builder.Configuration, IdentityDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    IdentityDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Identity.Domain.IUserRepository).Assembly,
    typeof(Sense.Crm.Modules.Identity.Contracts.OrgPermissions).Assembly);
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<IdentityDbContext>>();

// Sales: outbox'ı (LeadConverted, DealStageChanged) boşaltır; ayrıca Identity'nin OrganizationCreated olayının tüketicisi
// (varsayılan satış hunisi tohumlama) burada elle kayıtlıdır: Application assembly'sini taramak tüm handler bağımlılıklarını gerektirirdi.
builder.Services.AddModuleDbContext<SalesDbContext>(builder.Configuration, SalesDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    SalesDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Sales.Domain.IAccountRepository).Assembly,
    typeof(Sense.Crm.Modules.Sales.Contracts.SalesPermissions).Assembly);
builder.Services.AddScoped<Sense.Crm.Modules.Sales.Application.IDefaultPipelineSeeder, Sense.Crm.Modules.Sales.Infrastructure.Provisioning.DefaultPipelineSeeder>();
builder.Services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Identity.Contracts.OrganizationCreated>, Sense.Crm.Modules.Sales.Application.Pipelines.OrganizationCreatedHandler>();
// M4: Sales'in DealStageChanged domain event'ini modüller arası DealStageChangedIntegration'a çevirir (Workflows tüketir).
builder.Services.AddScoped<Sense.Crm.Shared.Infrastructure.Persistence.Outbox.IDomainEventHandler<Sense.Crm.Modules.Sales.Domain.Deals.DealStageChanged>, Sense.Crm.Modules.Sales.Infrastructure.Events.DealStageChangedIntegrationPublisher>();
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<SalesDbContext>>();

// Activities: outbox'ı boşaltır (bugün olay üretmez; ileride bildirim/hatırlatma için hazır).
builder.Services.AddModuleDbContext<ActivitiesDbContext>(builder.Configuration, ActivitiesDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    ActivitiesDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Activities.Domain.IActivityRepository).Assembly,
    typeof(Sense.Crm.Modules.Activities.Contracts.ActivitiesPermissions).Assembly);
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<ActivitiesDbContext>>();

// Workflows (Milestone 4): outbox'ı boşaltır; lead/fırsat olaylarının tüketicilerini (kural değerlendirme + yürütme başlatma) barındırır
// (InProcessEventBus olayı bu süreçte dağıtır) ve Conductor görev işleyicilerini + yürütme durumu senkronunu çalıştırır. Worker Application
// assembly'lerini taramaz; Workflows'un ihtiyaç duyduğu diğer modüllerin Contracts uygulamaları (üye/rol arama, lead sahipliği, aktivite
// oluşturma, kayıt arama) ve olay tüketicileri burada elle kaydedilir.
builder.Services.AddModuleDbContext<WorkflowsDbContext>(builder.Configuration, WorkflowsDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    WorkflowsDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Workflows.Domain.IWorkflowRuleRepository).Assembly,
    typeof(Sense.Crm.Modules.Workflows.Contracts.WorkflowsPermissions).Assembly);
builder.Services.AddIdentityContractServices();
builder.Services.AddSalesContractServices();
builder.Services.AddActivitiesContractServices();
builder.Services.AddWorkflowsRuntime(builder.Configuration);
builder.Services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Sales.Contracts.LeadCreated>, Sense.Crm.Modules.Workflows.Application.Triggering.LeadCreatedWorkflowHandler>();
builder.Services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Sales.Contracts.DealStageChangedIntegration>, Sense.Crm.Modules.Workflows.Application.Triggering.DealStageChangedWorkflowHandler>();
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<WorkflowsDbContext>>();
builder.Services.AddWorkflowDefinitionRegistration();
builder.Services.AddHostedService<Sense.Crm.Worker.Workflows.ConductorTaskPollingService>();
builder.Services.AddHostedService<Sense.Crm.Worker.Workflows.ExecutionStatusSyncService>();
// Konteyner HEALTHCHECK için canlılık sinyali (HTTP ucu yok).
builder.Services.AddHostedService<Sense.Crm.Worker.HeartbeatService>();

// Marketing (M6C): outbox'ı boşaltır (bugün olay üretmez) ve Sales'in LeadConverted olayının tüketicisini barındırır
// (dönüşen lead'in kampanya üyelikleri kendiliğinden "converted" olur). Worker Application assembly'lerini taramadığı için elle kayıtlıdır.
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext>(builder.Configuration, Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Marketing.Domain.ICampaignRepository).Assembly,
    typeof(Sense.Crm.Modules.Marketing.Contracts.MarketingPermissions).Assembly);
builder.Services.AddMarketingContractServices();
builder.Services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Sales.Contracts.LeadConverted>, Sense.Crm.Modules.Marketing.Application.Members.LeadConvertedMarketingHandler>();
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<Sense.Crm.Modules.Marketing.Infrastructure.Persistence.MarketingDbContext>>();

// Commerce (Milestone 6A): outbox'ı boşaltır (QuoteAccepted, SalesOrderCreated; M6A'da tüketici yok, sonraki kartlar/workflow dinler).
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Commerce.Infrastructure.Persistence.CommerceDbContext>(builder.Configuration, Sense.Crm.Modules.Commerce.Infrastructure.Persistence.CommerceDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    Sense.Crm.Modules.Commerce.Infrastructure.Persistence.CommerceDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Commerce.Domain.IProductRepository).Assembly,
    typeof(Sense.Crm.Modules.Commerce.Contracts.CommercePermissions).Assembly);
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<Sense.Crm.Modules.Commerce.Infrastructure.Persistence.CommerceDbContext>>();

// Service (Milestone 6B): outbox'ı boşaltır (CaseResolved; bugün tüketicisi yok, bildirim/workflow için hazır); ayrıca Identity'nin
// OrganizationCreated olayının ikinci tüketicisi (varsayılan SLA politikaları tohumlama) burada elle kayıtlıdır.
builder.Services.AddModuleDbContext<ServiceDbContext>(builder.Configuration, ServiceDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    ServiceDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Service.Domain.ICaseRepository).Assembly,
    typeof(Sense.Crm.Modules.Service.Contracts.ServicePermissions).Assembly);
builder.Services.AddScoped<Sense.Crm.Modules.Service.Application.IDefaultSlaPolicySeeder, Sense.Crm.Modules.Service.Infrastructure.Provisioning.DefaultSlaPolicySeeder>();
builder.Services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Identity.Contracts.OrganizationCreated>, Sense.Crm.Modules.Service.Application.Sla.OrganizationCreatedSlaHandler>();
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<ServiceDbContext>>();

// Kullanım ölçümü (M7): Commerce/Service/Workflows'un IUsageReporter'ları (Sales/Activities/Marketing/Identity yukarıda kayıtlı) ve Workflows'un KVKK Conductor imha adımı.
builder.Services.AddCommerceContractServices();
builder.Services.AddServiceContractServices();
builder.Services.AddWorkflowsContractServices();

// Platform (M7): kiracı hesabı olayları (OrganizationCreated/Updated tüketicisi), outbox (TenantSuspended … olayları), günlük kullanım anlık görüntüsü ve KVKK imha işi.
// Domain + Contracts assembly'leri outbox olay tipi kaydı ve UnitOfWork çözümlemesi içindir; Worker Application assembly'lerini taramadığı için işleyiciler elle kayıtlıdır.
builder.Services.AddModuleDbContext<PlatformDbContext>(builder.Configuration, PlatformDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    PlatformDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Platform.Domain.Accounts.TenantAccount).Assembly,
    typeof(Sense.Crm.Modules.Platform.Contracts.TenantSuspended).Assembly);
builder.Services.AddPlatformContractServices(builder.Configuration);
builder.Services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Identity.Contracts.OrganizationCreated>, Sense.Crm.Modules.Platform.Application.Provisioning.OrganizationCreatedAccountHandler>();
builder.Services.AddScoped<Sense.Crm.Shared.Contracts.Events.IIntegrationEventHandler<Sense.Crm.Modules.Identity.Contracts.OrganizationUpdated>, Sense.Crm.Modules.Platform.Application.Provisioning.OrganizationUpdatedAccountHandler>();
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<PlatformDbContext>>();
builder.Services.AddHostedService<Sense.Crm.Worker.Platform.UsageSnapshotService>();
builder.Services.AddHostedService<Sense.Crm.Worker.Platform.TenantErasureService>();

// Gözlemlenebilirlik (C-OPS1, K20): Observability:Metrics:Enabled=true ise ayrı portta Prometheus /metrics + outbox/workflow/silme örnekleyicisi (varsayılan kapalı).
builder.Services.AddCrmObservability(builder.Configuration, "crm-worker");
builder.Services.AddWorkerMetricsSampler(builder.Configuration);

// Integrations (M8B): giden webhook teslimatı. Fan-out olay işleyicileri (Sales/Commerce/Service olaylarının tüketicileri) burada kayıtlıdır; dispatcher (dış çağrıyı YALNIZ Worker yapar), saklama ve KVKK imha
// adımları da burada. Varsayılan Integrations:Webhooks:Enabled=false → dispatcher boşta (fan-out satır yazmaz). Worker Application assembly'lerini taramaz: yalnız olay işleyicileri hedefli kaydedilir.
builder.Services.AddModuleDbContext<Sense.Crm.Modules.Integrations.Infrastructure.Persistence.IntegrationsDbContext>(builder.Configuration, Sense.Crm.Modules.Integrations.Infrastructure.Persistence.IntegrationsDbContext.SchemaName);
builder.Services.AddModuleHandlers(
    Sense.Crm.Modules.Integrations.Infrastructure.Persistence.IntegrationsDbContext.SchemaName,
    typeof(Sense.Crm.Modules.Integrations.Domain.IWebhookSubscriptionRepository).Assembly,
    typeof(Sense.Crm.Modules.Integrations.Contracts.IntegrationsPermissions).Assembly);
builder.Services.AddIntegrationsContractServices(builder.Configuration);
builder.Services.AddIntegrationsWorkerServices();
builder.Services.AddIntegrationsEventHandlers();
builder.Services.AddHostedService<Sense.Crm.Worker.OutboxPollingService<Sense.Crm.Modules.Integrations.Infrastructure.Persistence.IntegrationsDbContext>>();
builder.Services.AddHostedService<Sense.Crm.Worker.Integrations.WebhookDispatcherService>();
builder.Services.AddHostedService<Sense.Crm.Worker.Integrations.IntegrationsRetentionService>();

await builder.Build().RunAsync();

namespace Sense.Crm.Worker
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
                    Sense.Crm.Shared.Contracts.Observability.CrmMetrics.OutboxPollFailed(typeof(TContext).Name.Replace("DbContext", string.Empty, StringComparison.Ordinal).ToLowerInvariant());
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
