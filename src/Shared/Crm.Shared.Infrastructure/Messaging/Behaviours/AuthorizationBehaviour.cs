using System.Reflection;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Kernel.Results;

namespace Crm.Shared.Infrastructure.Messaging.Behaviours;

/// <summary>
/// [RequiresPermission] ile işaretli isteklerde çağıranın aktif organizasyondaki izni denetlenir (K7, basit RBAC).
/// API dışından (outbox/event) çağrılan use case'ler için de aynı kural geçerlidir; yalnız in-process sistem bağlamı
/// (<see cref="WellKnownRoles.System"/>, kullanıcı kimliği yok) denetimden muaftır.
/// </summary>
public sealed class AuthorizationBehaviour<TRequest, TResponse>(ICurrentUser user, IPermissionService permissions)
    : IPipelineBehaviour<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private const string PermissionArg = "permission";

    private static readonly RequiresPermissionAttribute[] Required =
        typeof(TRequest).GetCustomAttributes<RequiresPermissionAttribute>(inherit: true).ToArray();

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
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
