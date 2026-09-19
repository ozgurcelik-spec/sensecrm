using System.Text.Json;
using Crm.Modules.Workflows.Application;
using Crm.Modules.Workflows.Application.Executions;
using Crm.Modules.Workflows.Application.Rules;
using Crm.Modules.Workflows.Application.Tasks;
using Crm.Modules.Workflows.Application.Triggering;
using Crm.Modules.Workflows.Domain;
using Crm.Modules.Workflows.Infrastructure.Conductor;
using Crm.Modules.Workflows.Infrastructure.Persistence;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Context;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Modules.Workflows.Infrastructure;

/// <summary>
/// Workflows modülünün çalışma zamanı kayıtları (handler taraması hariç): depolar, okuma deposu, motor (Conductor), tetikleme,
/// görev işleyicileri, durum senkronu. Hem API host'u (<c>WorkflowsModule</c>) hem de Worker aynı kaydı kullanır; Worker
/// Application assembly'sini taramaz (yalnız burada listelenenleri ve olay tüketicilerini elle kaydeder).
/// </summary>
public static class WorkflowsRuntime
{
    public static IServiceCollection AddWorkflowsRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ConductorOptions>().Bind(configuration.GetSection(ConductorOptions.SectionName));

        services.AddScoped<IWorkflowsUnitOfWork>(sp => sp.GetRequiredService<WorkflowsDbContext>());
        services.AddScoped<IWorkflowRuleRepository, WorkflowRuleRepository>();
        services.AddScoped<IWorkflowExecutionRepository, WorkflowExecutionRepository>();
        services.AddScoped<IApprovalRepository, ApprovalRepository>();
        services.AddScoped<IWorkflowReadStore, WorkflowReadStore>();
        services.AddScoped<IRunningExecutionSource, RunningExecutionSource>();

        // Conductor: tipli HttpClient + dayanıklılık (yeniden deneme yalnız güvenli yöntemlerde: POST'lar iş üretir, körü körüne tekrarlanmaz).
        services.AddHttpClient<ConductorClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<ConductorOptions>>().Value;
                if (Uri.TryCreate(options.BaseUrl.TrimEnd('/') + "/api/", UriKind.Absolute, out var baseAddress) && !string.IsNullOrWhiteSpace(options.BaseUrl))
                {
                    http.BaseAddress = baseAddress;
                }

                http.Timeout = TimeSpan.FromSeconds(Math.Max(options.RequestTimeoutSeconds, 5) * 3);
            })
            .AddStandardResilienceHandler(o =>
            {
                o.Retry.DisableForUnsafeHttpMethods();
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
            });
        services.AddSingleton<IWorkflowDefinitionRegistrar, ConductorDefinitionRegistrar>();
        services.AddScoped<IWorkflowEngine, ConductorWorkflowEngine>();

        services.AddScoped<WorkflowStarter>();
        services.AddScoped<WorkflowTrigger>();
        services.AddScoped<WorkflowTexts>();
        services.AddScoped<RuleRoleVerifier>();
        services.AddScoped<ExecutionStatusSynchronizer>();

        services.AddScoped<IWorkflowTaskWorker, AssignLeadOwnerTaskHandler>();
        services.AddScoped<IWorkflowTaskWorker, CreateFollowUpTaskHandler>();
        services.AddScoped<IWorkflowTaskWorker, CreateApprovalsTaskHandler>();
        services.AddScoped<IWorkflowTaskWorker, RecordDecisionTaskHandler>();
        services.AddScoped<IWorkflowTaskWorker, CancelPendingApprovalsTaskHandler>();
        services.AddSingleton<WorkflowTaskRunner>();
        services.AddSingleton<ExecutionSyncRunner>();
        return services;
    }

    /// <summary>API başlangıcında tanımları Conductor'a arka planda kaydeder (hata başlatmayı durdurmaz).</summary>
    public static IServiceCollection AddWorkflowDefinitionRegistration(this IServiceCollection services) =>
        services.AddHostedService<WorkflowDefinitionRegistrationService>();
}

/// <summary>
/// Bir workflow görevini kiracı + sistem bağlamında çalıştırır (Worker ve sahte motor ortak kullanır): kiracı <c>input.tenantId</c>'den,
/// her görev kendi DI kapsamında. Beklenmeyen istisna geçici hata (<see cref="WorkflowTaskResult.Retry"/>) sayılır.
/// </summary>
public sealed partial class WorkflowTaskRunner(IServiceScopeFactory scopes, ILogger<WorkflowTaskRunner> logger)
{
    public async Task<WorkflowTaskResult> ExecuteAsync(string taskType, JsonElement inputData, CancellationToken ct)
    {
        var input = new TaskInput(inputData);
        if (input.TenantId == Guid.Empty)
        {
            return WorkflowTaskResult.Failed("invalid_input");
        }

        await using var scope = scopes.CreateAsyncScope();
        using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().BeginScope(input.TenantId);
        using var systemScope = CurrentUserAccessor.UseSystem();

        var handler = scope.ServiceProvider.GetServices<IWorkflowTaskWorker>().FirstOrDefault(h => string.Equals(h.TaskType, taskType, StringComparison.Ordinal));
        if (handler is null)
        {
            return WorkflowTaskResult.Failed("unknown_task");
        }

        try
        {
            return await handler.ExecuteAsync(input, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTaskFailed(logger, ex, taskType, input.TenantId);
            return WorkflowTaskResult.Retry(ex.GetType().Name + ": " + ex.Message);
        }
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Workflow task {TaskType} failed for tenant {TenantId}")]
    private static partial void LogTaskFailed(ILogger logger, Exception exception, string taskType, Guid tenantId);
}

/// <summary>
/// Çalışan yürütmelerin durumunu Conductor'dan <c>workflow_executions</c>'a yansıtır (bir tur). Kiracılar arası tek okuma dar
/// projeksiyondur (<see cref="IRunningExecutionSource"/>); her yürütme kendi kiracı + sistem bağlamında senkronlanır.
/// </summary>
public sealed partial class ExecutionSyncRunner(IServiceScopeFactory scopes, IOptions<ConductorOptions> options, ILogger<ExecutionSyncRunner> logger)
{
    /// <returns>Durumu değişen yürütme sayısı.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<RunningExecutionRef> running;
        await using (var listScope = scopes.CreateAsyncScope())
        {
            running = await listScope.ServiceProvider.GetRequiredService<IRunningExecutionSource>()
                .ListRunningAsync(options.Value.StatusSyncBatchSize, ct).ConfigureAwait(false);
        }

        var changed = 0;
        foreach (var item in running)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().BeginScope(item.TenantId);
                using var systemScope = CurrentUserAccessor.UseSystem();
                if (await scope.ServiceProvider.GetRequiredService<ExecutionStatusSynchronizer>().SyncAsync(item.ExecutionId, ct).ConfigureAwait(false))
                {
                    changed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSyncFailed(logger, ex, item.ExecutionId);
            }
        }

        return changed;
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "Workflow execution {ExecutionId} status sync failed; will retry")]
    private static partial void LogSyncFailed(ILogger logger, Exception exception, Guid executionId);
}

/// <summary>Uygulama başlarken workflow tanımlarını arka planda motora kaydeder; motor hazır değilse birkaç kez dener, sonra vazgeçer (ilk başlatma yeniden dener).</summary>
public sealed partial class WorkflowDefinitionRegistrationService(IWorkflowDefinitionRegistrar registrar, ILogger<WorkflowDefinitionRegistrationService> logger) : BackgroundService
{
    private const int MaxAttempts = 6;
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await registrar.EnsureRegisteredAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRegistrationFailed(logger, ex.Message, attempt, MaxAttempts);
                await Task.Delay(Delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Workflow definitions could not be registered with the engine ({Reason}); attempt {Attempt}/{MaxAttempts}")]
    private static partial void LogRegistrationFailed(ILogger logger, string reason, int attempt, int maxAttempts);
}
