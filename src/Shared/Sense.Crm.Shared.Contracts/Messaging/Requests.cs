using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Contracts.Messaging;

/// <summary>Dispatcher üzerinden yürütülen tüm isteklerin işaretleyicisi.</summary>
public interface IRequest<TResult>
{
}

/// <summary>Durum değiştiren istek; UnitOfWork ve outbox pipeline'ından geçer.</summary>
public interface ICommand : IRequest<Result>
{
}

public interface ICommand<TResult> : IRequest<Result<TResult>>
{
}

/// <summary>Salt okuma isteği; transaction açılmaz.</summary>
public interface IQuery<TResult> : IRequest<Result<TResult>>
{
}

/// <summary>Önbelleğe alınabilir isteğin generic olmayan tabanı; CachingBehaviour istekleri bununla yakalar.</summary>
public interface ICachedRequest
{
    /// <summary>Kiracı ön eki CachingBehaviour tarafından eklenir; burada yalnız isteğe özgü kısım verilir.</summary>
    string CacheKey { get; }

    TimeSpan? Expiration => null;

    /// <summary>Tag bazlı invalidation için (ör. "accounts").</summary>
    IReadOnlyCollection<string> Tags => [];
}

/// <summary>HybridCache ile önbelleklenen sorgu. Yalnız başarılı sonucun değeri önbelleğe yazılır.</summary>
public interface ICachedQuery<TResult> : IQuery<TResult>, ICachedRequest
{
}

public interface ICommandHandler<in TCommand> where TCommand : ICommand
{
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>Pipeline davranışı (logging, validation, authorization, unit of work, caching).</summary>
public interface IPipelineBehaviour<in TRequest, TResponse>
    where TRequest : notnull
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

public interface IDispatcher
{
    Task<Result> Send(ICommand command, CancellationToken cancellationToken = default);

    Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}
