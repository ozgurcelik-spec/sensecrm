using System.Reflection;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Shouldly;
using Xunit;

namespace Crm.Tests.Architecture;

/// <summary>
/// M6 — yetkilendirme kapsama testleri. (1) Her <c>ICommand</c>/<c>IQuery</c> tipi ya <see cref="RequiresPermissionAttribute"/> ya da bilinçli
/// <see cref="AnyAuthenticatedUserAttribute"/> taşır: yeni bir istek işaretsiz eklenirse test KIRMIZI olur (izin unutulamaz).
/// (2) Her controller eylemi kimlik doğrulama ister; anonim yalnızca <c>AuthController</c>'ın kimlik akışı uçlarıdır.
/// Yeni modüller (Commerce, Service, Marketing …) otomatik kapsanır.
/// </summary>
public sealed class RequestAuthorizationTests
{
    private const string Prefix = "Crm.";
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
        var unannotated = RequestTypes()
            .Where(t => !t.IsDefined(typeof(RequiresPermissionAttribute), inherit: true) && !t.IsDefined(typeof(AnyAuthenticatedUserAttribute), inherit: false))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        unannotated.ShouldBeEmpty(
            "Her ICommand/IQuery tipine [RequiresPermission(...)] ya da gerekçeli [AnyAuthenticatedUser(\"...\")] eklenmelidir. İşaretsiz tipler: "
            + string.Join(", ", unannotated));
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
