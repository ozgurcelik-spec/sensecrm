using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Persistence;
using Crm.Shared.Kernel.Results;

namespace Crm.Shared.Infrastructure.Messaging.Behaviours;

/// <summary>
/// Yalnızca command'larda: handler başarılıysa modülün UnitOfWork'ünde SaveChanges (domain event'ler aynı transaction'da outbox'a yazılır).
/// Hangi modülün UoW'u olduğu handler assembly'sine göre <see cref="IModuleUnitOfWorkResolver"/> ile bulunur.
/// </summary>
public sealed class UnitOfWorkBehaviour<TRequest, TResponse>(IModuleUnitOfWorkResolver resolver)
    : IPipelineBehaviour<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not (ICommand or ICommand<object>) && !IsCommand(request))
        {
            return await next().ConfigureAwait(false);
        }

        var uow = resolver.Resolve(typeof(TRequest));
        if (uow is null)
        {
            return await next().ConfigureAwait(false);
        }

        TResponse? response = default;
        await uow.ExecuteInTransactionAsync(async ct =>
        {
            response = await next().ConfigureAwait(false);
            if (response.IsSuccess)
            {
                await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);

        return response!;
    }

    private static bool IsCommand(object request) =>
        request.GetType().GetInterfaces().Any(i =>
            i == typeof(ICommand) || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));
}

/// <summary>İstek tipinin ait olduğu modülün UnitOfWork'ünü (assembly → modül eşlemesi) döner.</summary>
public interface IModuleUnitOfWorkResolver
{
    IUnitOfWork? Resolve(Type requestType);
}
