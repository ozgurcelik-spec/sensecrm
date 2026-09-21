using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Sense.Crm.Api;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Persistence.Audit;
using Sense.Crm.Shared.Infrastructure.Persistence.Retention;
using Sense.Crm.Shared.Kernel.Domain;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.Architecture;

/// <summary>
/// C-SEC2 M2: KVKK imhasının kapsamı elle kayıtlı DbContext listesine dayanır (<c>Worker/Program.cs</c>, <c>Migrator/Program.cs</c>). Yeni bir modül (M8 …) API kataloğuna eklenip bu iki
/// kompozisyon köküne <b>unutulursa</b> o modülün tabloları imhada atlanırdı. Bu testler unutmayı derleme/test zamanında kırmızıya çevirir: <c>ModuleCatalog</c>'daki her modülün DbContext'i
/// hem Worker'da hem Migrator'da <c>AddModuleDbContext</c> ile kayıtlı olmalı (imha adımı bu çağrıyla kendiliğinden gelir) ve modülün her <see cref="ITenantEntity"/> tablosu imha planında yer almalıdır.
/// </summary>
public sealed class ErasureRegistrationArchitectureTests
{
    [Fact]
    public void EveryModuleInTheCatalog_HasItsDbContextRegisteredInWorkerAndMigrator()
    {
        var contexts = ModuleContexts();
        contexts.Count.ShouldBeGreaterThan(5, "modül kataloğu boş görünüyor: test boş geçmemeli");

        var root = FindSolutionRoot();
        var worker = File.ReadAllText(Path.Combine(root, "src", "Sense.Crm.Worker", "Program.cs"));
        var migrator = File.ReadAllText(Path.Combine(root, "src", "Sense.Crm.Migrator", "Program.cs"));

        var missing = new List<string>();
        foreach (var (module, context) in contexts)
        {
            var registration = new Regex($@"AddModuleDbContext<[^>]*\b{Regex.Escape(context.Name)}>");
            if (!registration.IsMatch(worker))
            {
                missing.Add($"{module}: {context.Name} is not registered in src/Sense.Crm.Worker/Program.cs (AddModuleDbContext<{context.Name}>)");
            }

            if (!registration.IsMatch(migrator))
            {
                missing.Add($"{module}: {context.Name} is not registered in src/Sense.Crm.Migrator/Program.cs (AddModuleDbContext<{context.Name}>)");
            }
        }

        missing.ShouldBeEmpty(
            "Her modülün DbContext'i Worker ve Migrator'a da kaydedilmelidir; aksi halde KVKK imhası (Worker) ve migration/backfill (Migrator) o modülün tablolarını atlar:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void EveryTenantScopedEntity_OfEveryModule_IsCoveredByTheGenericEraserPlan()
    {
        var checkedEntities = 0;
        foreach (var (module, contextType) in ModuleContexts())
        {
            using var context = CreateDesignTimeContext(contextType);
            var plan = TenantDataEraser<ModuleDbContext>.BuildPlan(context).Select(t => (t.Schema, t.Table)).ToHashSet();

            foreach (var entity in context.Model.GetEntityTypes())
            {
                if (entity.IsOwned() || entity.ClrType == typeof(AuditLogEntry) || !typeof(ITenantEntity).IsAssignableFrom(entity.ClrType) || entity.GetTableName() is not { } table)
                {
                    continue;
                }

                var schema = entity.GetSchema() ?? context.Model.GetDefaultSchema() ?? "public";
                plan.ShouldContain((schema, table), $"{module}: {entity.ClrType.Name} ({schema}.{table}) is tenant scoped but has no eraser target");
                checkedEntities++;
            }
        }

        checkedEntities.ShouldBeGreaterThan(20, "kiracı varlıkları bulunamadı: test boş geçmemeli");
    }

    /// <summary>(modül adı, DbContext tipi): katalogdaki modüllerin assembly'lerinden.</summary>
    private static List<(string Module, Type Context)> ModuleContexts()
    {
        var result = new List<(string, Type)>();
        foreach (var module in ModuleCatalog.Modules)
        {
            var contexts = module.Assemblies
                .SelectMany(a => SafeTypes(a))
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ModuleDbContext).IsAssignableFrom(t))
                .Distinct()
                .ToList();
            contexts.Count.ShouldBe(1, $"{module.Name}: modülün tam bir ModuleDbContext'i olmalı (Assemblies listesi Infrastructure'ı içermeli)");
            result.Add((module.Name, contexts[0]));
        }

        return result;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    /// <summary>Her modülün <c>IDesignTimeDbContextFactory&lt;T&gt;</c> uygulaması bağlantısız model üretir.</summary>
    private static ModuleDbContext CreateDesignTimeContext(Type contextType)
    {
        // EF Design paketi bu test projesine akmaz: arayüz ad ile aranır (IDesignTimeDbContextFactory<T>, T = DbContext tipi).
        var factoryType = contextType.Assembly.GetTypes().SingleOrDefault(t =>
            t is { IsClass: true, IsAbstract: false }
            && t.GetInterfaces().Any(i => i.IsGenericType && i.Name.StartsWith("IDesignTimeDbContextFactory", StringComparison.Ordinal) && i.GenericTypeArguments[0] == contextType));
        factoryType.ShouldNotBeNull($"{contextType.Name} için IDesignTimeDbContextFactory bulunamadı (dotnet ef migrations için de gereklidir)");
        var factory = Activator.CreateInstance(factoryType)!;
        var create = factoryType.GetMethod("CreateDbContext")!;
        return (ModuleDbContext)create.Invoke(factory, [Array.Empty<string>()])!;
    }

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
