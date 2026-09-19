using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sense.Crm.Shared.Contracts.Configuration;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Infrastructure.Messaging.Behaviours;

/// <summary>İstek adı, süre, kiracı ve kullanıcı bilgisiyle yapısal log; başarısız sonuçları Warning olarak yazar.</summary>
public sealed partial class LoggingBehaviour<TRequest, TResponse>(
    ILogger<LoggingBehaviour<TRequest, TResponse>> logger,
    IOptions<DiagnosticsOptions> options,
    ITenantContext tenant,
    ICurrentUser user) : IPipelineBehaviour<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            [LogProperties.Request] = name,
            [LogProperties.TenantId] = tenant.IsResolved ? tenant.TenantId : null,
            [LogProperties.UserId] = user.UserId,
        });

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await next().ConfigureAwait(false);
            sw.Stop();

            if (response.IsFailure)
            {
                Log.RequestFailed(logger, name, sw.ElapsedMilliseconds, response.Error.Code);
            }
            else if (sw.ElapsedMilliseconds > options.Value.SlowRequestThresholdMs)
            {
                Log.RequestSlow(logger, name, sw.ElapsedMilliseconds);
            }
            else
            {
                Log.RequestCompleted(logger, name, sw.ElapsedMilliseconds);
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RequestCrashed(logger, ex, name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1000, Level = LogLevel.Warning, Message = "{Request} failed in {ElapsedMs} ms: {ErrorCode}")]
        public static partial void RequestFailed(ILogger logger, string request, long elapsedMs, string errorCode);

        [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "{Request} completed slowly in {ElapsedMs} ms")]
        public static partial void RequestSlow(ILogger logger, string request, long elapsedMs);

        [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "{Request} completed in {ElapsedMs} ms")]
        public static partial void RequestCompleted(ILogger logger, string request, long elapsedMs);

        [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "{Request} threw after {ElapsedMs} ms")]
        public static partial void RequestCrashed(ILogger logger, Exception exception, string request, long elapsedMs);
    }
}

/// <summary>Yapısal log alan adları.</summary>
public static class LogProperties
{
    public const string Request = "Request";
    public const string TenantId = "TenantId";
    public const string UserId = "UserId";
    public const string CorrelationId = "CorrelationId";
    public const string Module = "Module";
}
