using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Identity.Contracts;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Contracts;
using Sense.Crm.Modules.Workflows.Domain;
using Sense.Crm.Modules.Workflows.Infrastructure;
using Sense.Crm.Modules.Workflows.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Modules;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;

namespace Sense.Crm.Modules.Workflows.Api;

/// <summary>
/// Workflows modülü kompozisyon kökü (Milestone 4): kural (<c>workflow_rules</c>), yürütme (<c>workflow_executions</c>) ve onay
/// (<c>approvals</c>) yönetimi; workflow motoru Conductor OSS (<see cref="IWorkflowEngine"/> portu arkasında). Sales/Activities/Identity'ye
/// yalnız <c>*.Contracts</c> üzerinden bağlıdır. <c>org.workflows.manage</c> ve <c>crm.approvals.decide</c> izinlerini kataloğa katar.
/// Lead/fırsat olaylarının tüketicileri (<c>LeadCreated</c>, <c>DealStageChangedIntegration</c>) Application assembly'sinden taranır.
/// </summary>
public sealed class WorkflowsModule : IModule
{
    public string Name => WorkflowsDbContext.SchemaName;

    public IReadOnlyList<Assembly> Assemblies { get; } =
    [
        typeof(WorkflowsModule).Assembly,
        typeof(WorkflowNames).Assembly,
        typeof(WorkflowsPermissions).Assembly,
        typeof(WorkflowsDbContext).Assembly,
    ];

    public IEnumerable<Permission> Permissions => WorkflowsPermissions.All;

    public void AddModule(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WorkflowsDbContext>(configuration, WorkflowsDbContext.SchemaName);

        // Domain + Contracts assembly'leri olay tipi kaydı (outbox) için.
        services.AddModuleHandlers(
            WorkflowsDbContext.SchemaName,
            typeof(WorkflowNames).Assembly,
            typeof(WorkflowsDbContext).Assembly,
            typeof(IWorkflowRuleRepository).Assembly,
            typeof(WorkflowsPermissions).Assembly);

        services.AddWorkflowsRuntime(configuration);
        services.AddWorkflowsContractServices();
        services.AddWorkflowDefinitionRegistration();
        services.AddSingleton<IAuditEntityPermissions, WorkflowsAuditEntityPermissions>();
    }
}

/// <summary>Workflows varlık türlerinin kayıt bazlı denetim okuma izinleri (<c>GET /audit?entityType&amp;entityId</c>).</summary>
public sealed class WorkflowsAuditEntityPermissions : IAuditEntityPermissions
{
    public IReadOnlyDictionary<string, string> ReadPermissionsByEntityType => WorkflowsAuditEntities.ReadPermissions;
}
