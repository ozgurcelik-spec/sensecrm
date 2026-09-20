using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Workflows.Application;
using Sense.Crm.Modules.Workflows.Domain.Rules;
using Sense.Crm.Modules.Workflows.Infrastructure.Persistence;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Retention;
using Sense.Crm.Shared.Contracts.Usage;

namespace Sense.Crm.Modules.Workflows.Infrastructure;

/// <summary>
/// Workflows'un başka bileşenlere sunduğu kayıtlar (M7): <c>IUsageReporter</c> (Platform kullanım anlık görüntüsü ve limit denetimi) ve KVKK imhasının Conductor adımı.
/// Hem API host'u hem de Worker aynı kaydı kullanır (<c>IWorkflowEngine</c> çağıranın kayıtlarından gelir: <c>AddWorkflowsRuntime</c>).
/// </summary>
public static class WorkflowsContractServices
{
    public static IServiceCollection AddWorkflowsContractServices(this IServiceCollection services)
    {
        services.AddScoped<IUsageReporter, WorkflowsUsageReporter>();
        services.AddScoped<ITenantDataEraser, WorkflowsConductorEraser>();
        return services;
    }
}

/// <summary>
/// KVKK imhası, Conductor adımı (sıra 90 — veritabanı satırlarından <b>önce</b>): kiracının yürütmelerinin motor kimlikleri kiracı kapsamında
/// (<c>BeginScope</c>, filtre atlamadan) okunur, her biri <see cref="IWorkflowEngine.RemoveAsync"/> ile Conductor'dan silinir (girdilerde konu adı/<c>tenantId</c>
/// kalmasın). Conductor hatası adımı <c>failed</c> yapar (istisna yayılır; iş yeniden dener). Idempotenttir.
/// </summary>
public sealed class WorkflowsConductorEraser(WorkflowsDbContext db, IWorkflowEngine engine, ITenantContextSetter tenantSetter) : ITenantDataEraser
{
    public string Name => "workflows-conductor";

    public int Order => 90;

    public async Task<EraseReport> EraseAsync(Guid tenantId, int chunkSize, CancellationToken ct = default)
    {
        List<string> engineIds;
        List<(Guid Id, WorkflowRuleKind Kind)> orphans;
        using (tenantSetter.BeginScope(tenantId))
        {
            engineIds = await db.Executions.AsNoTracking()
                .Where(e => e.EngineWorkflowId != null)
                .Select(e => e.EngineWorkflowId!)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // C-SEC2 L2: motor çağrısı başarılı ama engine_workflow_id yazılamamış ("yetim") yürütmeler; motorda CRM yürütme kimliğiyle (correlationId) aranır.
            orphans = (await db.Executions.AsNoTracking()
                .Where(e => e.EngineWorkflowId == null)
                .Select(e => new { e.Id, e.Kind })
                .ToListAsync(ct)
                .ConfigureAwait(false)).Select(e => (e.Id, e.Kind)).ToList();
        }

        var removed = 0;
        foreach (var engineId in engineIds)
        {
            await engine.RemoveAsync(engineId, ct).ConfigureAwait(false);
            removed++;
        }

        var orphanRemoved = 0;
        foreach (var (id, kind) in orphans)
        {
            foreach (var engineId in await engine.FindIdsByCorrelationAsync(WorkflowNames.WorkflowFor(kind), id.ToString(), ct).ConfigureAwait(false))
            {
                if (!engineIds.Contains(engineId, StringComparer.Ordinal))
                {
                    await engine.RemoveAsync(engineId, ct).ConfigureAwait(false);
                    orphanRemoved++;
                }
            }
        }

        return new EraseReport(new Dictionary<string, long> { ["conductor.executions"] = removed, ["conductor.orphan_executions"] = orphanRemoved });
    }
}
