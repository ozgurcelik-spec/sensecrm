using System.Reflection;
using Sense.Crm.Api;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.Architecture;

/// <summary>
/// M8B mimari kuralları (docs/plan/m8b-entegrasyonlar.md): Integrations yalnız host'lardan (Api/Worker/Migrator) ve testlerden referans edilir; hiçbir modül ona bağlanmaz;
/// yönetim istekleri yalnız <c>org.integrations.manage</c> ister; API anahtarına açık tek izinsiz istek <c>GetCurrentApiKeyQuery</c>'dir; anahtara verilebilir küme <c>org.*</c> içermez.
/// </summary>
public sealed class IntegrationsArchitectureTests
{
    private const string Prefix = "Sense.Crm.";
    private const string IntegrationsPrefix = "Sense.Crm.Modules.Integrations.";

    [Fact]
    public void NoModuleOrSharedAssembly_ReferencesIntegrations_OnlyHostsDo()
    {
        var offenders = new List<string>();
        var assemblies = Products();
        assemblies.Count.ShouldBeGreaterThan(20);
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name!;
            if (name.StartsWith(IntegrationsPrefix, StringComparison.Ordinal) || name is "Sense.Crm.Api" or "Sense.Crm.Worker" or "Sense.Crm.Migrator")
            {
                continue;
            }

            offenders.AddRange(assembly.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith(IntegrationsPrefix, StringComparison.Ordinal)).Select(n => $"{name} -> {n}"));
        }

        offenders.ShouldBeEmpty("Integrations.* yalnız Api/Worker/Migrator ve testler tarafından referans edilebilir: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Integrations_DependsOnlyOnSharedAndTheOtherModulesContracts()
    {
        var offenders = new List<string>();
        foreach (var assembly in Products().Where(a => a.GetName().Name!.StartsWith(IntegrationsPrefix, StringComparison.Ordinal)))
        {
            offenders.AddRange(assembly.GetReferencedAssemblies().Select(r => r.Name!)
                .Where(n => n.StartsWith("Sense.Crm.Modules.", StringComparison.Ordinal) && !n.StartsWith(IntegrationsPrefix, StringComparison.Ordinal) && !n.EndsWith(".Contracts", StringComparison.Ordinal))
                .Select(n => $"{assembly.GetName().Name} -> {n}"));
        }

        offenders.ShouldBeEmpty(string.Join(", ", offenders));
    }

    [Fact]
    public void IntegrationsRequests_AllRequireTheManagePermission_ExceptTheApprovedApiKeyProbe()
    {
        var requests = RequestTypes().Where(t => t.Namespace is { } ns && ns.StartsWith("Sense.Crm.Modules.Integrations.Application", StringComparison.Ordinal)).ToList();
        requests.Count.ShouldBeGreaterThan(20, "istekler bulunamadı: test boş geçmemeli");

        var open = requests.Where(t => !t.IsDefined(typeof(RequiresPermissionAttribute), inherit: true)).Select(t => t.Name).ToList();
        open.ShouldBe(["GetCurrentApiKeyQuery"]);

        requests.Where(t => t.IsDefined(typeof(RequiresPermissionAttribute), inherit: true))
            .ShouldAllBe(t => t.GetCustomAttributes<RequiresPermissionAttribute>(true).All(a => a.Permission == IntegrationsPermissions.Manage));
    }

    [Fact]
    public void ApiKeyAllowedMarker_IsExactlyTheApprovedList_WithAReason_AndOnlyOnAnyAuthenticatedRequests()
    {
        var marked = RequestTypes().Where(t => t.IsDefined(typeof(ApiKeyAllowedAttribute), inherit: false)).ToList();
        marked.Select(t => t.Name).ShouldBe(["GetCurrentApiKeyQuery"]);
        marked.ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.GetCustomAttribute<ApiKeyAllowedAttribute>()!.Reason));
        marked.ShouldAllBe(t => t.IsDefined(typeof(AnyAuthenticatedUserAttribute), inherit: false) && !t.IsDefined(typeof(RequiresPermissionAttribute), inherit: true));

        var integrationsAnyAuth = RequestTypes()
            .Where(t => t.Namespace!.StartsWith("Sense.Crm.Modules.Integrations.", StringComparison.Ordinal) && t.IsDefined(typeof(AnyAuthenticatedUserAttribute), inherit: false))
            .Select(t => t.Name);
        integrationsAnyAuth.ShouldBe(["GetCurrentApiKeyQuery"]);
    }

    [Fact]
    public void TheKeyGrantableScopeSet_NeverContainsAnOrgKey_NorApprovalsDecide_AndMatchesTheCatalog()
    {
        var catalog = ModuleCatalog.Modules.SelectMany(m => m.Permissions).ToList();
        catalog.Select(p => p.Key).ShouldContain(IntegrationsPermissions.Manage);
        catalog.Single(p => p.Key == IntegrationsPermissions.Manage).Group.ShouldBe("org");

        var allowed = ApiKeyScopePolicy.Allowed(catalog);
        allowed.ShouldNotBeEmpty();
        allowed.Where(a => a.StartsWith("org.", StringComparison.Ordinal)).ShouldBeEmpty();
        allowed.ShouldNotContain("crm.approvals.decide");
        allowed.ShouldAllBe(a => a.StartsWith("crm.", StringComparison.Ordinal));
        catalog.Where(p => p.Key.StartsWith("crm.", StringComparison.Ordinal) && p.Key != "crm.approvals.decide").Select(p => p.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            .ShouldBe(allowed.ToList(), "every crm.* key except crm.approvals.decide is grantable");
    }

    [Fact]
    public void WebhookDataMappers_UseAnExplicitAllowList_NoReflection()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "Modules", "Integrations", "Sense.Crm.Modules.Integrations.Application", "Webhooks", "WebhookEvents.cs"));
        source.ShouldNotContain("GetProperties(");
        source.ShouldNotContain("GetFields(");
        source.ShouldNotContain("JsonSerializer.SerializeToNode(integrationEvent");
        source.ShouldNotContain("JsonSerializer.Serialize(integrationEvent");
    }

    [Fact]
    public void OnlyTheWorkerHostRegistersTheOutboundTransport()
    {
        var root = FindRoot();
        var api = File.ReadAllText(Path.Combine(root, "src", "Modules", "Integrations", "Sense.Crm.Modules.Integrations.Api", "IntegrationsModule.cs"));
        api.ShouldNotContain("AddIntegrationsWorkerServices");
        api.ShouldNotContain("SafeWebhookHttpTransport");
        File.ReadAllText(Path.Combine(root, "src", "Sense.Crm.Worker", "Program.cs")).ShouldContain("AddIntegrationsWorkerServices");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sense.Crm.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Sense.Crm.slnx bulunamadı.");
    }

    private static List<Assembly> Products() =>
        Directory.GetFiles(AppContext.BaseDirectory, Prefix + "*.dll")
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToList();

    private static IEnumerable<Type> RequestTypes() =>
        Products().SelectMany(SafeTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i =>
                i == typeof(ICommand) || (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICommand<>) || i.GetGenericTypeDefinition() == typeof(IQuery<>)))))
            .Distinct();

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
