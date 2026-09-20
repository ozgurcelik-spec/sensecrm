using Sense.Crm.Modules.Activities.Api;
using Sense.Crm.Modules.Commerce.Api;
using Sense.Crm.Modules.Identity.Api;
using Sense.Crm.Modules.Marketing.Api;
using Sense.Crm.Modules.Platform.Api;
using Sense.Crm.Modules.Sales.Api;
using Sense.Crm.Modules.Service.Api;
using Sense.Crm.Modules.Workflows.Api;
using Sense.Crm.Shared.Contracts.Modules;

namespace Sense.Crm.Api;

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
        new ServiceModule(),
        new PlatformModule(),
    ];
}
