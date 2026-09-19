using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Shared.Contracts.Modules;

/// <summary>Modülün kompozisyon kökü. Host, açık listeden (ModuleCatalog) her modülü kaydeder.</summary>
public interface IModule : IPermissionProvider
{
    /// <summary>Kısa ad: "identity", "leave" … URL öneki ve PostgreSQL şeması buradan türer.</summary>
    string Name { get; }

    /// <summary>Handler/validator taraması için assembly'ler (Application + Infrastructure).</summary>
    IReadOnlyList<Assembly> Assemblies { get; }

    void AddModule(IServiceCollection services, IConfiguration configuration);
}
