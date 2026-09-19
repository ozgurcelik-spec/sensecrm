using Crm.Shared.Contracts.Security;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Crm.Shared.Infrastructure.Observability;

/// <summary>
/// Kimliği doğrulanmış istekte HTTP bağlamındaki JWT claim'lerinden UserId ve UserEmail ekler.
/// IHttpContextAccessor tekil (singleton) olduğundan, Serilog logger'ı kurulurken kök IServiceProvider'dan
/// tek seferlik çözümlenip <c>.Enrich.With(new UserEnricher(...))</c> ile kaydedilebilir; iç akışı AsyncLocal
/// tabanlıdır (arka plan işlerinde HttpContext olmadığından bu enricher sessizce hiçbir şey eklemez).
/// </summary>
public sealed class UserEnricher(IHttpContextAccessor httpContextAccessor) : ILogEventEnricher
{
    public const string UserIdProperty = "UserId";
    public const string UserEmailProperty = "UserEmail";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var userId = user.FindFirst(ClaimNames.Subject)?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(UserIdProperty, userId));
        }

        var email = user.FindFirst(ClaimNames.Email)?.Value;
        if (!string.IsNullOrEmpty(email))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(UserEmailProperty, email));
        }
    }
}
