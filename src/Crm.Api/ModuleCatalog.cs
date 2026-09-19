using Crm.Modules.Activities.Api;
using Crm.Modules.Commerce.Api;
using Crm.Modules.Identity.Api;
using Crm.Modules.Marketing.Api;
using Crm.Modules.Sales.Api;
using Crm.Modules.Workflows.Api;
using Crm.Shared.Contracts.Modules;

namespace Crm.Api;

/// <summary>
/// Host'un yüklediği modüllerin açık listesi (assembly taraması yerine derleme zamanı görünürlük).
/// Yeni modül: build/new-module.ps1 ile iskelet açılır, buraya eklenir.
/// </summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<IModule> Modules { get; } =
    [
        new IdentityModule(),
        new SalesModule(),
        new ActivitiesModule(),
        new WorkflowsModule(),
        new MarketingModule(),
        new CommerceModule(),
    ];
}
