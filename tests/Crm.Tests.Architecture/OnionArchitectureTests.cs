using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Crm.Tests.Architecture;

/// <summary>ADR 0002 (Onion) ve ADR 0001 (modül sınırları) kuralları. Yeni modül eklendikçe otomatik kapsanır.</summary>
public sealed class OnionArchitectureTests
{
    private const string Prefix = "Crm.";
    private const string ModulesPrefix = "Crm.Modules.";
    private const string DomainSuffix = ".Domain";
    private const string ApplicationSuffix = ".Application";
    private const string InfrastructureSuffix = ".Infrastructure";
    private const string ContractsSuffix = ".Contracts";
    private const string ApiSuffix = ".Api";
    private const string KernelAssembly = "Crm.Shared.Kernel";
    private const string SharedContracts = "Crm.Shared.Contracts";
    private const string SharedInfrastructure = "Crm.Shared.Infrastructure";
    private const string SharedWeb = "Crm.Shared.Web";
    private const string SystemPrefix = "System";
    private const string MicrosoftPrefix = "Microsoft";

    private static readonly Assembly[] All = LoadSolutionAssemblies();

    public static IEnumerable<object[]> DomainAssemblies() => Named(DomainSuffix).Select(a => new object[] { a.GetName().Name! });

    public static IEnumerable<object[]> ApplicationAssemblies() => Named(ApplicationSuffix).Select(a => new object[] { a.GetName().Name! });

    [Theory]
    [MemberData(nameof(DomainAssemblies))]
    public void Domain_DependsOnlyOnKernel(string assemblyName)
    {
        var asm = Find(assemblyName);
        var references = asm.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        references.ShouldAllBe(n => n == KernelAssembly, $"{assemblyName} yalnız {KernelAssembly} referans alabilir");

        var external = asm.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(n => !n.StartsWith(SystemPrefix, StringComparison.Ordinal) && !n.StartsWith(MicrosoftPrefix, StringComparison.Ordinal) && !n.StartsWith(Prefix, StringComparison.Ordinal) && n != "netstandard" && n != "mscorlib")
            .ToList();
        external.ShouldBeEmpty($"{assemblyName} NuGet paketi referans alamaz");
    }

    [Theory]
    [MemberData(nameof(ApplicationAssemblies))]
    public void Application_DoesNotDependOnInfrastructureOrApi(string assemblyName)
    {
        var asm = Find(assemblyName);
        var forbidden = asm.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(n => n.EndsWith(InfrastructureSuffix, StringComparison.Ordinal) || n.EndsWith(ApiSuffix, StringComparison.Ordinal) || n == SharedWeb)
            .ToList();
        forbidden.ShouldBeEmpty($"{assemblyName} Infrastructure/Api katmanına bağımlı olamaz");
    }

    [Fact]
    public void Modules_TalkOnlyThroughContracts()
    {
        foreach (var asm in All.Where(a => a.GetName().Name!.StartsWith(ModulesPrefix, StringComparison.Ordinal)))
        {
            var own = ModuleOf(asm.GetName().Name!);
            var crossModule = asm.GetReferencedAssemblies().Select(r => r.Name!)
                .Where(n => n.StartsWith(ModulesPrefix, StringComparison.Ordinal) && ModuleOf(n) != own)
                .ToList();
            crossModule.ShouldAllBe(n => n.EndsWith(ContractsSuffix, StringComparison.Ordinal),
                $"{asm.GetName().Name} başka modülün yalnız Contracts projesine bağımlı olabilir");
        }
    }

    [Fact]
    public void Contracts_DoNotDependOnDomainOrApplication()
    {
        foreach (var asm in Named(ContractsSuffix).Where(a => a.GetName().Name != SharedContracts))
        {
            var forbidden = asm.GetReferencedAssemblies().Select(r => r.Name!)
                .Where(n => n.EndsWith(DomainSuffix, StringComparison.Ordinal) || n.EndsWith(ApplicationSuffix, StringComparison.Ordinal) || n.EndsWith(InfrastructureSuffix, StringComparison.Ordinal))
                .ToList();
            forbidden.ShouldBeEmpty($"{asm.GetName().Name} yalnız Kernel/Shared.Contracts referans alabilir");
        }
    }

    [Fact]
    public void Handlers_AreSealed_AndNamedByConvention()
    {
        var result = Types.InAssemblies(Named(ApplicationSuffix))
            .That().HaveNameEndingWith("Handler")
            .Should().BeSealed()
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Controllers_InheritApiControllerBase()
    {
        var result = Types.InAssemblies(Named(ApiSuffix))
            .That().HaveNameEndingWith("Controller")
            .Should().Inherit(typeof(Crm.Shared.Web.Controllers.ApiControllerBase))
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static string ModuleOf(string assemblyName)
    {
        var rest = assemblyName[ModulesPrefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? rest : rest[..dot];
    }

    private static IEnumerable<Assembly> Named(string suffix) => All.Where(a => a.GetName().Name!.EndsWith(suffix, StringComparison.Ordinal));

    private static Assembly Find(string name) => All.Single(a => a.GetName().Name == name);

    /// <summary>
    /// Architecture rule: No hardcoded secret VALUES in source code (scans .cs source under src/, not type names).
    /// Flags literal string assignments to fields/properties named password/secret/apikey.
    /// Allowed locations: appsettings.json, environment variables, secrets.json (git-ignored).
    /// </summary>
    [Fact]
    public void NoPlaintextSecretsInCode()
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\b(?:password|secret|api_?key)\b\s*=\s*""([^""]{4,})""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Claim types / header names (e.g. "api_key", "X-Api-Key") are identifier-like tokens, not
        // credential values, and would otherwise false-positive here (e.g. ClaimNames.ApiKey).
        var identifierLikeValue = new System.Text.RegularExpressions.Regex(@"^[A-Za-z][A-Za-z0-9_-]*$");

        var srcDir = FindRepoRoot() is { } root ? Path.Combine(root, "src") : null;
        srcDir.ShouldNotBeNull("Could not locate repository 'src' directory from test output path.");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir!, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = pattern.Match(lines[i]);
                if (match.Success && !identifierLikeValue.IsMatch(match.Groups[1].Value))
                {
                    violations.Add($"{Path.GetRelativePath(srcDir!, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty(
            "Found potential hardcoded secrets. Move to appsettings.json or environment variables. " +
            string.Join(", ", violations));
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Architecture rule: Handlers must follow naming conventions.
    /// - Convention: `{Action}Handler`, e.g. CreateUserHandler, ListRolesHandler.
    /// </summary>
    [Fact]
    public void Handlers_FollowNamingConventions()
    {
        var result = Types.InAssemblies(Named(ApplicationSuffix))
            .That().HaveNameEndingWith("Handler")
            .Should().HaveNameMatching(@"^[A-Z][A-Za-z0-9]*Handler$")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            "Handlers must follow pattern: {Action}Handler. " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// Architecture rule: Events must be in Domain layer and follow naming conventions.
    /// - Domain events: `*Event` or `*DomainEvent` in Domain assembly
    /// </summary>
    [Fact]
    public void DomainEvents_AreInDomainLayer()
    {
        var result = Types.InAssemblies(Named(DomainSuffix))
            .That().HaveNameEndingWith("Event")
            .Should().ResideInNamespaceContaining("Domain")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            "Domain events must reside in Domain namespace. " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// Architecture rule: Audit/Infrastructure events should NOT be visible to Application layer.
    /// Only Domain events can be published from entities.
    /// </summary>
    [Fact]
    public void AuditEvents_AreNotReferencedByApplicationHandlers()
    {
        var auditEventTypeNames = Types.InAssemblies(Named(InfrastructureSuffix))
            .That().HaveNameEndingWith("AuditEvent")
            .GetTypes()
            .Select(t => t.FullName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

        if (auditEventTypeNames.Length == 0)
        {
            return;
        }

        var result = Types.InAssemblies(Named(ApplicationSuffix))
            .Should().NotHaveDependencyOnAny(auditEventTypeNames)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            "Application handlers must not reference Infrastructure audit events. " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// Architecture rule: Permission keys (declared via [RequiresPermission("...")]) must follow naming convention.
    /// Pattern: {module}.{resource_plural}.{action} in snake_case
    /// Example: identity.users.create, organization.units.read
    /// </summary>
    [Fact]
    public void PermissionKeys_FollowNamingConvention()
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"^[a-z_]+\.[a-z_]+\.[a-z_]+$");

        var violations = Named(ApplicationSuffix)
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetCustomAttributes(typeof(Crm.Shared.Contracts.Security.RequiresPermissionAttribute), inherit: false)
                .Cast<Crm.Shared.Contracts.Security.RequiresPermissionAttribute>()
                .Select(attr => (Type: t, attr.Permission)))
            .Where(x => !pattern.IsMatch(x.Permission))
            .ToList();

        violations.ShouldBeEmpty(
            "Permission keys should follow pattern: module.resource_plural.action in snake_case. " +
            string.Join(", ", violations.Select(v => $"{v.Type.FullName} -> {v.Permission}")));
    }

    /// <summary>
    /// Architecture rule: No Infrastructure layer in public API of Contracts.
    /// Contracts should only expose domain concepts, not infrastructure concerns.
    /// </summary>
    [Fact]
    public void Contracts_DoNotExposeInfrastructureConcerns()
    {
        var infrastructureNamespaces = Types.InAssemblies(Named(InfrastructureSuffix))
            .GetTypes()
            .Select(t => t.Namespace)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct()
            .ToArray();

        if (infrastructureNamespaces.Length == 0)
        {
            return;
        }

        var result = Types.InAssemblies(Named(ContractsSuffix))
            .Should().NotHaveDependencyOnAny(infrastructureNamespaces)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            "Contracts must not depend on Infrastructure. " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// İzin katalogları (Contracts/Application'daki statik <c>All</c> listeleri): anahtar biçimi {grup}.{kaynak}.{eylem}
    /// (snake_case) ve grup yalnız "org" | "crm" (web istemcisiyle sözleşme; GET /api/v1/permissions).
    /// </summary>
    [Fact]
    public void PermissionCatalogs_UseContractKeyFormatAndGroups()
    {
        var keyPattern = new System.Text.RegularExpressions.Regex(@"^[a-z_]+\.[a-z_]+\.[a-z_]+$");
        var groups = new HashSet<string>(StringComparer.Ordinal) { "org", "crm" };

        var permissions = Named(ContractsSuffix)
            .Concat(Named(ApplicationSuffix))
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsAbstract && t.IsSealed)
            .Select(t => t.GetProperty("All", BindingFlags.Public | BindingFlags.Static))
            .Where(p => p is not null && typeof(IEnumerable<Crm.Shared.Contracts.Security.Permission>).IsAssignableFrom(p.PropertyType))
            .SelectMany(p => (IEnumerable<Crm.Shared.Contracts.Security.Permission>)p!.GetValue(null)!)
            .ToList();

        permissions.ShouldNotBeEmpty("permission catalogs should have been discovered via reflection across every module");

        var violations = permissions
            .Where(p => !keyPattern.IsMatch(p.Key) || !groups.Contains(p.Group) || !p.Key.StartsWith(p.Group + ".", StringComparison.Ordinal))
            .Select(p => $"{p.Key} -> Group='{p.Group}'")
            .ToList();

        violations.ShouldBeEmpty(string.Join(", ", violations));
    }

    /// <summary>
    /// Architecture rule: Modules can only have one DbContext per module.
    /// All entities in a module share the same PostgreSQL schema.
    /// </summary>
    [Fact]
    public void Modules_HaveOneDbContextPerModule()
    {
        var dbContextType = typeof(Crm.Shared.Infrastructure.Persistence.ModuleDbContext);
        var dbContextTypes = Types.InAssemblies(Named(InfrastructureSuffix))
            .That().Inherit(dbContextType)
            .GetTypes();

        var duplicatesByAssembly = dbContextTypes
            .GroupBy(t => t.Assembly)
            .Where(g => g.Count() > 1)
            .ToList();

        duplicatesByAssembly.ShouldBeEmpty(
            "Each module should have exactly one DbContext inheriting from ModuleDbContext. " +
            string.Join(", ", duplicatesByAssembly.SelectMany(g => g.Select(t => t.FullName))));
    }

    private static Assembly[] LoadSolutionAssemblies()
    {
        // Test projesi Crm.Api'ye referans verir; tüm Crm.* assembly'leri çıktı klasöründe bulunur.
        var dir = AppContext.BaseDirectory;
        var files = Directory.GetFiles(dir, Prefix + "*.dll").Where(f => !f.EndsWith("Tests.dll", StringComparison.Ordinal));
        return files.Select(f => Assembly.LoadFrom(f)).ToArray();
    }
}
