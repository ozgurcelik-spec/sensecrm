using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sense.Crm.Api;
using Sense.Crm.Shared.Contracts.Usage;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.Architecture;

/// <summary>
/// M7 mimari kuralları (docs/plan/m7-saas-hazirlik.md): Platform yalnız host'lardan (Api/Worker/Migrator) ve testlerden referans edilir; her iş modülünün tam bir
/// <c>IUsageReporter</c>'ı vardır; API ve Migrator'daki <c>Platform</c> yapılandırma bölümü (plan kataloğu dahil) aynıdır.
/// </summary>
public sealed class PlatformArchitectureTests
{
    private const string Prefix = "Sense.Crm.";
    private const string PlatformPrefix = "Sense.Crm.Modules.Platform.";

    [Fact]
    public void NoModuleOrSharedAssemblyReferencesPlatform()
    {
        var assemblies = LoadProductAssemblies();
        assemblies.Count.ShouldBeGreaterThan(20, "çözüm assembly'leri bulunamadı: test boş geçmemeli");

        var offenders = new List<string>();
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name!;
            var isHostOrPlatform = name.StartsWith(PlatformPrefix, StringComparison.Ordinal)
                || name is "Sense.Crm.Api" or "Sense.Crm.Worker" or "Sense.Crm.Migrator";
            if (isHostOrPlatform)
            {
                continue;
            }

            offenders.AddRange(assembly.GetReferencedAssemblies()
                .Select(r => r.Name!)
                .Where(n => n.StartsWith(PlatformPrefix, StringComparison.Ordinal))
                .Select(n => $"{name} -> {n}"));
        }

        offenders.ShouldBeEmpty("Platform.* yalnız Sense.Crm.Api, Sense.Crm.Worker, Sense.Crm.Migrator ve testler tarafından referans edilebilir: " + string.Join(", ", offenders));
    }

    [Fact]
    public void PlatformDependsOnlyOnSharedAndIdentityContracts()
    {
        var offenders = new List<string>();
        foreach (var assembly in LoadProductAssemblies().Where(a => a.GetName().Name!.StartsWith(PlatformPrefix, StringComparison.Ordinal)))
        {
            offenders.AddRange(assembly.GetReferencedAssemblies()
                .Select(r => r.Name!)
                .Where(n => n.StartsWith("Sense.Crm.Modules.", StringComparison.Ordinal)
                    && !n.StartsWith(PlatformPrefix, StringComparison.Ordinal)
                    && n != "Sense.Crm.Modules.Identity.Contracts")
                .Select(n => $"{assembly.GetName().Name} -> {n}"));
        }

        offenders.ShouldBeEmpty("Platform yalnız Shared.* ve Identity.Contracts kullanır: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryBusinessModule_HasExactlyOneUsageReporter_WhoseModuleMatchesTheModuleName()
    {
        var assemblies = LoadProductAssemblies();
        var modules = ModuleCatalog.Modules.Where(m => m.Name != "platform").ToList();
        modules.Count.ShouldBeGreaterThanOrEqualTo(7, "ModuleCatalog'daki iş modülleri: identity, sales, activities, workflows, marketing, commerce, service (+ M8C files); toplam sayı sabitlenmez");

        foreach (var module in modules)
        {
            var infrastructure = assemblies.Single(a => a.GetName().Name == $"Sense.Crm.Modules.{char.ToUpperInvariant(module.Name[0])}{module.Name[1..]}.Infrastructure");
            var reporters = infrastructure.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IUsageReporter).IsAssignableFrom(t)).ToList();
            reporters.Count.ShouldBe(1, $"{module.Name}: tam bir IUsageReporter olmalı");

            // Module alanı sabit bir değerdir; kurucu bağımlılıkları (DbContext) olmadan okunur.
            var instance = (IUsageReporter)RuntimeHelpers.GetUninitializedObject(reporters[0]);
            instance.Module.ShouldBe(module.Name, $"{reporters[0].Name}.Module = IModule.Name olmalı");
        }
    }

    [Fact]
    public void PlatformConfiguration_IsIdenticalInApiAndMigratorAppSettings()
    {
        var root = FindSolutionRoot();
        var api = ReadPlatformSection(Path.Combine(root, "src", "Sense.Crm.Api", "appsettings.json"));
        var migrator = ReadPlatformSection(Path.Combine(root, "src", "Sense.Crm.Migrator", "appsettings.json"));
        api.ShouldBe(migrator, "Platform yapılandırması (plan kataloğu dahil) API ve Migrator appsettings.json'da aynı olmalıdır (tek doğruluk kaynağı: sapma = plan/limit tutarsızlığı)");
        api.ShouldContain("\"internal\"");
    }

    private static string ReadPlatformSection(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.GetProperty("Platform").GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    private static List<Assembly> LoadProductAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, Prefix + "*.dll")
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToList();

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sense.Crm.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Sense.Crm.slnx bulunamadı.");
    }
}
