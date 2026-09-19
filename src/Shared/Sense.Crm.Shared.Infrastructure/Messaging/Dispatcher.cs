using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Infrastructure.Messaging;

/// <summary>Lightweight command/query dispatcher (ADR 0004). Resolves handlers and pipeline behaviours from DI.</summary>
public sealed class Dispatcher(IServiceProvider provider) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, Type> HandlerTypeCache = new();

    public Task<Result> Send(ICommand command, CancellationToken cancellationToken = default) =>
        Execute<Result>(command, typeof(ICommandHandler<>).MakeGenericType(command.GetType()), cancellationToken);

    public Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) =>
        Execute<Result<TResult>>(command, typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult)), cancellationToken);

    public Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
        Execute<Result<TResult>>(query, typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult)), cancellationToken);

    private Task<TResponse> Execute<TResponse>(object request, Type handlerInterface, CancellationToken cancellationToken)
    {
        var handler = provider.GetService(handlerInterface)
            ?? throw new InvalidOperationException($"No handler registered for '{request.GetType().Name}' ({handlerInterface.Name}).");

        var handleMethod = HandlerTypeCache.GetOrAdd(handlerInterface, t => t).GetMethod("Handle")
            ?? throw new InvalidOperationException("Handle method not found.");

        RequestHandlerDelegate<TResponse> invokeHandler = () =>
            (Task<TResponse>)handleMethod.Invoke(handler, [request, cancellationToken])!;

        var behaviourType = typeof(IPipelineBehaviour<,>).MakeGenericType(request.GetType(), typeof(TResponse));
        var behaviours = provider.GetServices(behaviourType).Cast<object>().Reverse().ToList();

        var pipeline = invokeHandler;
        foreach (var behaviour in behaviours)
        {
            var next = pipeline;
            var method = behaviourType.GetMethod("Handle")!;
            pipeline = () => (Task<TResponse>)method.Invoke(behaviour, [request, next, cancellationToken])!;
        }

        return pipeline();
    }
}
