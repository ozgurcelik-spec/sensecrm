using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.Architecture;

/// <summary>
/// M6 — yetkilendirme kapsama testleri. (1) Her <c>ICommand</c>/<c>IQuery</c> tipi ya <see cref="RequiresPermissionAttribute"/> ya da bilinçli
/// <see cref="AnyAuthenticatedUserAttribute"/> taşır: yeni bir istek işaretsiz eklenirse test KIRMIZI olur (izin unutulamaz).
/// (2) Her controller eylemi kimlik doğrulama ister; anonim yalnızca <c>AuthController</c>'ın kimlik akışı uçlarıdır.
/// Yeni modüller (Commerce, Service, Marketing …) otomatik kapsanır.
/// </summary>
public sealed class RequestAuthorizationTests
{
    private const string Prefix = "Sense.Crm.";
    private const string AuthControllerName = "AuthController";

    /// <summary>Anonim kalabilen tek eylemler: kayıt/giriş/yenileme/çıkış + istemci yapılandırması (hepsi AuthController'da).</summary>
    private static readonly HashSet<string> AnonymousAuthActions = new(StringComparer.Ordinal)
    {
        "GetConfig", "SignUp", "Login", "Refresh", "Logout",
    };

    [Fact]
    public void EveryCommandAndQuery_DeclaresAPermissionOrAnExplicitAnyAuthenticatedUserMarker()
    {
        RequestTypes().Count().ShouldBeGreaterThan(30, "istek tipleri bulunamadı: test boş geçmemeli");
        // M7 (D5): her istek TAM OLARAK birini taşır: [RequiresPermission] | [AnyAuthenticatedUser] | [PlatformAdminOnly].
        var offenders = RequestTypes()
            .Where(t => new[]
            {
                t.IsDefined(typeof(RequiresPermissionAttribute), inherit: true),
                t.IsDefined(typeof(AnyAuthenticatedUserAttribute), inherit: false),
                t.IsDefined(typeof(PlatformAdminOnlyAttribute), inherit: false),
            }.Count(x => x) != 1)
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "Her ICommand/IQuery tipi tam olarak birini taşımalıdır: [RequiresPermission(...)] | gerekçeli [AnyAuthenticatedUser(\"...\")] | [PlatformAdminOnly]. Aykırı tipler: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void PlatformConsoleRequests_ArePlatformAdminOnly_AndTenantSideRequestsRequirePermissions()
    {
        var console = RequestTypes().Where(t => t.Namespace == "Sense.Crm.Modules.Platform.Application.Console").ToList();
        var tenantSide = RequestTypes().Where(t => t.Namespace == "Sense.Crm.Modules.Platform.Application.Tenant").ToList();
        console.Count.ShouldBeGreaterThan(8, "Platform konsol istekleri bulunamadı: test boş geçmemeli");
        tenantSide.Count.ShouldBeGreaterThan(2, "Platform kiracı tarafı istekleri bulunamadı: test boş geçmemeli");

        console.Where(t => !t.IsDefined(typeof(PlatformAdminOnlyAttribute), inherit: false)).ShouldBeEmpty("Application.Console istekleri [PlatformAdminOnly] olmak zorundadır");
        tenantSide.Where(t => !t.IsDefined(typeof(RequiresPermissionAttribute), inherit: true)).ShouldBeEmpty("Application.Tenant istekleri [RequiresPermission] olmak zorundadır");
        tenantSide.Where(t => t.IsDefined(typeof(PlatformAdminOnlyAttribute), inherit: false)).ShouldBeEmpty("Kiracı tarafı istekleri [PlatformAdminOnly] olamaz");
    }

    [Fact]
    public void EveryControllerWithAPlatformRoute_CarriesThePlatformAdminPolicy()
    {
        var platformControllers = Controllers()
            .Where(c => c.GetCustomAttributes<RouteAttribute>(inherit: true).Any(r => r.Template.Contains("/platform", StringComparison.Ordinal)))
            .ToList();
        platformControllers.Count.ShouldBeGreaterThanOrEqualTo(3, "/platform rotalı denetleyiciler bulunamadı: test boş geçmemeli");

        var offenders = platformControllers
            .Where(c => !c.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any(a => a.Policy == "PlatformAdmin"))
            .Select(c => c.FullName)
            .ToList();
        offenders.ShouldBeEmpty("/platform rotalı her denetleyici sınıf düzeyinde [Authorize(Policy = \"PlatformAdmin\")] taşımalıdır: " + string.Join(", ", offenders));
    }

    [Fact]
    public void PermissionCatalog_HasNoPlatformKeys_BecausePlatformAdminIsAnAccountFlagNotATenantPermission()
    {
        var keys = Load().SelectMany(SafeTypes)
            .Where(t => t.IsAbstract && t.IsSealed)
            .Select(t => t.GetProperty("All", BindingFlags.Public | BindingFlags.Static))
            .Where(p => p is not null && typeof(IEnumerable<Permission>).IsAssignableFrom(p.PropertyType))
            .SelectMany(p => (IEnumerable<Permission>)p!.GetValue(null)!)
            .Select(p => p.Key)
            .ToList();

        keys.ShouldNotBeEmpty();
        keys.Where(k => k.StartsWith("platform.", StringComparison.Ordinal)).ShouldBeEmpty("İzin kataloğunda platform.* anahtarı olamaz (isPlatformAdmin kiracı izni değildir)");
    }

    /// <summary>
    /// Kiracı durumundan (askı, deneme bitişi, engel) bağımsız çalışan istekler: onaylı listeyle BİREBİR eşit olmalıdır. Ekleme bilinçli inceleme ister.
    /// Plan listesine ek: kimliksiz akışlar (giriş/kayıt/yenileme/çıkış) bir Bearer başlığı taşıyabildiğinden (JWT kimlik doğrulaması <c>[AllowAnonymous]</c> uçlarda da
    /// çalışır) kiracı bağlamı kurulabilir; askıdaki kiracının kullanıcısı çıkış yapabilmeli ve başka kiracıya giriş yapabilmelidir.
    /// </summary>
    [Fact]
    public void TenantStatusExemptRequests_AreExactlyTheApprovedList()
    {
        var approved = new[]
        {
            "GetMeQuery", "UpdateMeCommand", "ChangePasswordCommand", "ListMyInvitationsQuery", "AcceptInvitationCommand", "DeclineInvitationCommand",
            "SwitchOrganizationCommand", "GetSubscriptionQuery",
            "LoginCommand", "RefreshTokenCommand", "LogoutCommand", "SignUpCommand",
        };

        var actual = RequestTypes().Where(t => t.IsDefined(typeof(TenantStatusExemptAttribute), inherit: true)).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        actual.ShouldBe(approved.Order(StringComparer.Ordinal).ToList(), "[TenantStatusExempt] taşıyan isteklerin kümesi onaylı listeyle birebir eşit olmalıdır");
        RequestTypes().Where(t => t.IsDefined(typeof(TenantStatusExemptAttribute), inherit: true))
            .ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.GetCustomAttribute<TenantStatusExemptAttribute>()!.Reason));
    }

    [Fact]
    public void EveryCreateCommandOfABusinessModule_DeclaresAPlanLimitDecision()
    {
        var businessCommands = RequestTypes()
            .Where(t => t.Name.StartsWith("Create", StringComparison.Ordinal) && t.Name.EndsWith("Command", StringComparison.Ordinal))
            .Where(t => t.Namespace is { } ns && ns.StartsWith("Sense.Crm.Modules.", StringComparison.Ordinal)
                && !ns.StartsWith("Sense.Crm.Modules.Identity.", StringComparison.Ordinal) && !ns.StartsWith("Sense.Crm.Modules.Platform.", StringComparison.Ordinal))
            .ToList();
        businessCommands.Count.ShouldBeGreaterThanOrEqualTo(12, "Create*Command tipleri bulunamadı: test boş geçmemeli");

        var offenders = businessCommands
            .Where(t => !t.IsDefined(typeof(ConsumesLimitAttribute), inherit: true) && !t.IsDefined(typeof(NoPlanLimitAttribute), inherit: true))
            .Select(t => t.FullName)
            .ToList();
        offenders.ShouldBeEmpty("Her Create*Command ya [ConsumesLimit] ya da gerekçeli [NoPlanLimit] taşımalıdır: " + string.Join(", ", offenders));
        businessCommands.Where(t => t.IsDefined(typeof(NoPlanLimitAttribute), inherit: true))
            .ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.GetCustomAttribute<NoPlanLimitAttribute>()!.Reason));
    }

    [Fact]
    public void ConsumesLimitCommands_AreExactlyTheApprovedList()
    {
        var approved = new[]
        {
            "AddMemberCommand", "CreateAccountCommand", "CreateContactCommand", "CreateLeadCommand", "CreateDealCommand", "CreateActivityCommand", "CreateRuleCommand",
            "CreateProductCommand", "CreateQuoteCommand", "CreateOrderCommand", "ConvertQuoteCommand", "CreateCaseCommand", "CreateCampaignCommand",
        };

        RequestTypes().Where(t => t.IsDefined(typeof(ConsumesLimitAttribute), inherit: true)).Select(t => t.Name).Order(StringComparer.Ordinal)
            .ShouldBe(approved.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void AnyAuthenticatedUserMarker_RequiresAReason_AndIsNeverCombinedWithAPermission()
    {
        foreach (var type in RequestTypes().Where(t => t.IsDefined(typeof(AnyAuthenticatedUserAttribute), inherit: false)))
        {
            type.GetCustomAttribute<AnyAuthenticatedUserAttribute>()!.Reason.ShouldNotBeNullOrWhiteSpace($"{type.FullName}: gerekçe zorunlu");
            type.IsDefined(typeof(RequiresPermissionAttribute), inherit: true).ShouldBeFalse($"{type.FullName}: izin ve AnyAuthenticatedUser birlikte olamaz");
        }
    }

    [Fact]
    public void EveryControllerAction_RequiresAuthorization_ExceptTheAuthFlowEndpoints()
    {
        Controllers().SelectMany(Actions).Count().ShouldBeGreaterThan(30, "controller eylemleri bulunamadı: test boş geçmemeli");
        var offenders = new List<string>();
        foreach (var controller in Controllers())
        {
            var classAuthorized = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any();
            var classAnonymous = controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

            foreach (var action in Actions(controller))
            {
                var authorized = classAuthorized || action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any();
                var anonymous = classAnonymous || action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();
                var name = $"{controller.Name}.{action.Name}";

                if (anonymous)
                {
                    if (controller.Name != AuthControllerName || !AnonymousAuthActions.Contains(action.Name))
                    {
                        offenders.Add($"{name} (izinsiz anonim uç; yalnız AuthController kimlik akışı olabilir)");
                    }
                }
                else if (!authorized)
                {
                    offenders.Add($"{name} ([Authorize] yok)");
                }
            }
        }

        offenders.ShouldBeEmpty("Kimlik doğrulaması istemeyen controller eylemleri: " + string.Join("; ", offenders));
    }

    private static IEnumerable<Type> RequestTypes() =>
        Load().SelectMany(SafeTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && IsRequest(t))
            .Distinct();

    private static bool IsRequest(Type type) =>
        type.GetInterfaces().Any(i =>
            i == typeof(ICommand)
            || (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICommand<>) || i.GetGenericTypeDefinition() == typeof(IQuery<>))));

    private static IEnumerable<Type> Controllers() =>
        Load().SelectMany(SafeTypes).Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t));

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());

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

    private static Assembly[] Load() =>
        Directory.GetFiles(AppContext.BaseDirectory, Prefix + "*.dll")
            .Where(f => !Path.GetFileName(f).EndsWith("Tests.dll", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToArray();
}
