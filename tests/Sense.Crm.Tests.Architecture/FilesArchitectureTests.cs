using System.Reflection;
using System.Text.Json;
using Sense.Crm.Api;
using Sense.Crm.Modules.Files.Application;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Files;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.Architecture;

/// <summary>
/// M8C (Files) mimari kuralları: bağımlılık yönü (iş modülleri Files'a, Files iş modüllerine bağlanmaz — yalnız <c>Shared.Contracts.Files.IAttachmentTarget</c>), 9 kayıt türünün her birinin
/// tam bir hedefi ve gerçek izin anahtarları, her Files isteğinin tek koruma sınıfı <c>IAttachmentAccess</c>'i kullanması, yapılandırma eşitliği.
/// </summary>
public sealed class FilesArchitectureTests
{
    private const string Prefix = "Sense.Crm.";
    private const string FilesPrefix = "Sense.Crm.Modules.Files.";

    /// <summary>Plan tablosu (bağlayıcı): kayıt türü → (modül, okuma, yazma).</summary>
    private static readonly Dictionary<string, (string Module, string Read, string Write)> ExpectedTargets = new(StringComparer.Ordinal)
    {
        ["account"] = ("sales", "crm.accounts.read", "crm.accounts.write"),
        ["contact"] = ("sales", "crm.contacts.read", "crm.contacts.write"),
        ["lead"] = ("sales", "crm.leads.read", "crm.leads.write"),
        ["deal"] = ("sales", "crm.deals.read", "crm.deals.write"),
        ["activity"] = ("activities", "crm.activities.read", "crm.activities.write"),
        ["case"] = ("service", "crm.cases.read", "crm.cases.write"),
        ["quote"] = ("commerce", "crm.quotes.read", "crm.quotes.write"),
        ["order"] = ("commerce", "crm.orders.read", "crm.orders.write"),
        ["campaign"] = ("marketing", "crm.campaigns.read", "crm.campaigns.write"),
    };

    [Fact]
    public void NoBusinessModuleReferencesFiles_AndFilesReferencesNoBusinessModule()
    {
        var assemblies = LoadProductAssemblies();
        assemblies.Count.ShouldBeGreaterThan(20, "çözüm assembly'leri bulunamadı: test boş geçmemeli");
        assemblies.Count(a => a.GetName().Name!.StartsWith(FilesPrefix, StringComparison.Ordinal)).ShouldBe(5, "Files.{Domain,Application,Contracts,Infrastructure,Api}");

        var offenders = new List<string>();
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name!;
            var isFiles = name.StartsWith(FilesPrefix, StringComparison.Ordinal);
            var isHost = name is "Sense.Crm.Api" or "Sense.Crm.Worker" or "Sense.Crm.Migrator";
            foreach (var reference in assembly.GetReferencedAssemblies().Select(r => r.Name!))
            {
                if (!isFiles && !isHost && reference.StartsWith(FilesPrefix, StringComparison.Ordinal))
                {
                    offenders.Add($"{name} -> {reference} (iş modülü Files'a bağlanamaz)");
                }

                if (isFiles && reference.StartsWith("Sense.Crm.Modules.", StringComparison.Ordinal)
                    && !reference.StartsWith(FilesPrefix, StringComparison.Ordinal) && reference != "Sense.Crm.Modules.Identity.Contracts")
                {
                    offenders.Add($"{name} -> {reference} (Files yalnız Shared.* ve Identity.Contracts kullanır)");
                }
            }
        }

        offenders.ShouldBeEmpty(string.Join("; ", offenders));
    }

    [Fact]
    public void EveryRecordType_HasExactlyOneAttachmentTarget_WithTheDocumentedModuleAndRealPermissionKeys()
    {
        AttachmentRecordTypes.All.OrderBy(t => t, StringComparer.Ordinal).ShouldBe(ExpectedTargets.Keys.OrderBy(t => t, StringComparer.Ordinal));

        var implementations = LoadProductAssemblies().SelectMany(SafeTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAttachmentTarget).IsAssignableFrom(t))
            .ToList();

        var instances = implementations.Select(t => (IAttachmentTarget)Activator.CreateInstance(t, args: t.GetConstructors().Single().GetParameters().Select(_ => (object?)null).ToArray())!).ToList();
        instances.Count.ShouldBe(AttachmentRecordTypes.All.Count, "her tür için tam bir hedef: " + string.Join(", ", implementations.Select(i => i.Name)));

        var catalog = ModuleCatalog.Modules.SelectMany(m => m.Permissions).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var moduleNames = ModuleCatalog.Modules.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var target in instances)
        {
            ExpectedTargets.ShouldContainKey(target.RecordType);
            var expected = ExpectedTargets[target.RecordType];
            target.Module.ShouldBe(expected.Module, target.RecordType);
            target.ReadPermission.ShouldBe(expected.Read, target.RecordType);
            target.WritePermission.ShouldBe(expected.Write, target.RecordType);
            catalog.ShouldContain(target.ReadPermission, $"{target.RecordType} okuma izni kataloğa ait olmalı");
            catalog.ShouldContain(target.WritePermission, $"{target.RecordType} yazma izni kataloğa ait olmalı");
            moduleNames.ShouldContain(target.Module, $"{target.RecordType}: modül ModuleCatalog'da olmalı");
        }

        instances.Select(i => i.RecordType).Distinct(StringComparer.Ordinal).Count().ShouldBe(AttachmentRecordTypes.All.Count, "aynı tür için birden çok hedef olamaz");
        implementations.ShouldAllBe(t => !t.Assembly.GetName().Name!.StartsWith(FilesPrefix, StringComparison.Ordinal), "hedefleri sahip modül uygular, Files değil");
        catalog.Where(k => k.Contains("files", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty("crm.files.* izni yoktur (D3)");
    }

    [Fact]
    public void EveryFilesRequest_IsAuthorizedAtRuntimeByTheSingleAccessGuard_OrCarriesAStaticPermission()
    {
        var assembly = typeof(IAttachmentAccess).Assembly;
        var handlers = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(IsHandlerInterface))
            .ToList();
        handlers.Count.ShouldBeGreaterThanOrEqualTo(8, "Files handler'ları bulunamadı: test boş geçmemeli");

        var requests = assembly.GetTypes().Where(t => t.GetInterfaces().Any(i => i == typeof(ICommand) || (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICommand<>) || i.GetGenericTypeDefinition() == typeof(IQuery<>)))) && t.IsClass).ToList();
        requests.Count.ShouldBeGreaterThanOrEqualTo(8);

        foreach (var request in requests)
        {
            var permission = request.IsDefined(typeof(RequiresPermissionAttribute), inherit: true);
            var anyUser = request.GetCustomAttribute<AnyAuthenticatedUserAttribute>();
            (permission ^ anyUser is not null).ShouldBeTrue($"{request.Name}: tam olarak biri: [RequiresPermission] | gerekçeli [AnyAuthenticatedUser]");
            if (anyUser is not null)
            {
                anyUser.Reason.ShouldNotBeNullOrWhiteSpace($"{request.Name}: gerekçe zorunlu");
            }

            request.IsDefined(typeof(PlatformAdminOnlyAttribute), inherit: false).ShouldBeFalse();
        }

        // Kaydın kendi izniyle korunan (AnyAuthenticatedUser) her isteğin handler'ı tek koruma sınıfına bağımlıdır; tek istisna kiracı-bağımsız sınırlar sorgusudur.
        var exemptRequests = new HashSet<string>(StringComparer.Ordinal) { "GetFilesLimitsQuery" };
        foreach (var request in requests.Where(r => r.IsDefined(typeof(AnyAuthenticatedUserAttribute), inherit: false) && !exemptRequests.Contains(r.Name)))
        {
            var handler = handlers.Single(h => h.GetInterfaces().Any(i => IsHandlerInterface(i) && i.GetGenericArguments()[0] == request));
            handler.GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
                .ShouldContain(typeof(IAttachmentAccess), $"{handler.Name}: ilk iş IAttachmentAccess çağrılmalı (tek koruma sınıfı)");
        }

        handlers.ShouldAllBe(h => h.IsSealed && h.Name.EndsWith("Handler", StringComparison.Ordinal));
    }

    [Fact]
    public void AttachFile_DeclaresAJustifiedNoPlanLimit_BecauseTheStorageQuotaIsEnforcedInTheHandler()
    {
        var attach = typeof(IAttachmentAccess).Assembly.GetTypes().Single(t => t.Name == "AttachFileCommand");
        attach.GetCustomAttribute<NoPlanLimitAttribute>()!.Reason.ShouldNotBeNullOrWhiteSpace();
        attach.IsDefined(typeof(ConsumesLimitAttribute), inherit: false).ShouldBeFalse("[ConsumesLimit] sabit miktar taşır; depolama kotası handler'da ILimitGuard ile zorlanır");
    }

    [Fact]
    public void FilesConfiguration_IsIdenticalInApiWorkerAndMigratorAppSettings_AndHasNoSecrets()
    {
        var root = FindSolutionRoot();
        var api = ReadFilesSection(Path.Combine(root, "src", "Sense.Crm.Api", "appsettings.json"));
        api.ShouldBe(ReadFilesSection(Path.Combine(root, "src", "Sense.Crm.Worker", "appsettings.json")), "Files yapılandırması Api ve Worker'da aynı olmalı");
        api.ShouldBe(ReadFilesSection(Path.Combine(root, "src", "Sense.Crm.Migrator", "appsettings.json")), "Files yapılandırması Api ve Migrator'da aynı olmalı");
        api.ShouldContain("\"Bucket\":\"crm-files\"");
        api.ShouldNotContain("AccessKey", Case.Insensitive);
        api.ShouldNotContain("SecretKey", Case.Insensitive);
    }

    private static bool IsHandlerInterface(Type i) =>
        i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICommandHandler<>) || i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) || i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>));

    private static string ReadFilesSection(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.GetProperty("Files").GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
    }

    private static List<Assembly> LoadProductAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, Prefix + "*.dll")
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToList();

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
