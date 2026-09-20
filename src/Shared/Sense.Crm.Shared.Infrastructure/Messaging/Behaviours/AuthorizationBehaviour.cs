using System.Reflection;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Infrastructure.Messaging.Behaviours;

/// <summary>
/// [RequiresPermission] ile işaretli isteklerde çağıranın aktif organizasyondaki izni denetlenir (K7, basit RBAC).
/// API dışından (outbox/event) çağrılan use case'ler için de aynı kural geçerlidir; yalnız in-process sistem bağlamı
/// (<see cref="WellKnownRoles.System"/>, kullanıcı kimliği yok) denetimden muaftır.
/// </summary>
public sealed class AuthorizationBehaviour<TRequest, TResponse>(ICurrentUser user, IPermissionService permissions, IPlatformAdminVerifier platformAdmins)
    : IPipelineBehaviour<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private const string PermissionArg = "permission";

    private static readonly RequiresPermissionAttribute[] Required =
        typeof(TRequest).GetCustomAttributes<RequiresPermissionAttribute>(inherit: true).ToArray();

    /// <summary>M7 (D5): platform yöneticisi isteği — bayrak JWT'den değil her istekte veritabanından doğrulanır (iki katmanlı savunma).</summary>
    private static readonly bool PlatformOnly = typeof(TRequest).IsDefined(typeof(PlatformAdminOnlyAttribute), inherit: true);

    /// <summary>M8B: API anahtarı çağrılarında izinsiz (<c>[AnyAuthenticatedUser]</c>) istek yalnız bu bilinçli istisnayla çalışır.</summary>
    private static readonly bool ApiKeyAllowed = typeof(TRequest).IsDefined(typeof(ApiKeyAllowedAttribute), inherit: false);

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // M8B (D9/D10): makine kimliği (API anahtarı) platform yönetimi yapamaz (oluşturan platform yöneticisi olsa da) ve izinsiz (yalnız kimlik) istek çağıramaz.
        if (user.ApiKey is not null && !IsSystemContext(user))
        {
            if (PlatformOnly)
            {
                return ResultFactory.Fail<TResponse>(Error.Forbidden(ErrorCodes.Forbidden));
            }

            if (Required.Length == 0 && !ApiKeyAllowed)
            {
                return ResultFactory.Fail<TResponse>(Error.Forbidden(ApiKeyErrorCodes.NotAllowed));
            }
        }

        if (PlatformOnly && !IsSystemContext(user))
        {
            if (!user.IsAuthenticated || user.UserId is null)
            {
                return ResultFactory.Fail<TResponse>(Error.Unauthorized(ErrorCodes.Unauthenticated));
            }

            if (!await platformAdmins.IsPlatformAdminAsync(user.UserId.Value, cancellationToken).ConfigureAwait(false))
            {
                return ResultFactory.Fail<TResponse>(Error.Forbidden(ErrorCodes.Forbidden));
            }
        }

        if (Required.Length == 0 || IsSystemContext(user))
        {
            return await next().ConfigureAwait(false);
        }

        if (!user.IsAuthenticated || user.UserId is null)
        {
            return ResultFactory.Fail<TResponse>(Error.Unauthorized(ErrorCodes.Unauthenticated));
        }

        foreach (var attr in Required)
        {
            if (!await permissions.HasAsync(user.UserId.Value, attr.Permission, cancellationToken).ConfigureAwait(false))
            {
                return ResultFactory.Fail<TResponse>(Error.Forbidden(ErrorCodes.Forbidden, (PermissionArg, attr.Permission)));
            }
        }

        return await next().ConfigureAwait(false);
    }

    /// <summary>Arka plan/sistem bağlamı: kimliksiz, platform bayraklı kullanıcı (JWT'den asla üretilmez; middleware her zaman kullanıcı kimliği atar).</summary>
    private static bool IsSystemContext(ICurrentUser user) => user.IsAuthenticated && user.IsPlatformAdmin && user.UserId is null;
}

/// <summary>Pipeline davranışlarının <c>Result</c>/<c>Result&lt;T&gt;</c> türünden bağımsız hata sonucu üretmesi için.</summary>
public static class ResultFactory
{
    public static TResponse Fail<TResponse>(Error error)
        where TResponse : Result
    {
        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)Result.Failure(error);
        }

        var valueType = typeof(TResponse).GetGenericArguments()[0];
        var method = typeof(Result).GetMethod(nameof(Result.Failure), 1, [typeof(Error)])!.MakeGenericMethod(valueType);
        return (TResponse)method.Invoke(null, [error])!;
    }
}
