using System.Reflection;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Contracts.Entitlements;
using Sense.Crm.Shared.Contracts.Messaging;
using Sense.Crm.Shared.Contracts.Observability;
using Sense.Crm.Shared.Contracts.Security;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Shared.Kernel.Results;

namespace Sense.Crm.Shared.Infrastructure.Messaging.Behaviours;

/// <summary>
/// Plan, kiracı yaşam döngüsü ve limit zorlamasının <b>tek</b> noktası (M7, docs/plan/m7-saas-hazirlik.md D4): her istek zaten
/// <see cref="IDispatcher"/>'dan geçtiği için HTTP'ye ayrı kontrol eklenmez. Sıra: Logging → Validation → Authorization → UnitOfWork →
/// <b>Entitlement</b> → Caching (yetkisiz çağıran plan bilgisi öğrenmez; sert limit kilidi açık transaction içinde alınır).
/// Algoritma:
/// <list type="number">
/// <item><c>[PlatformAdminOnly]</c>, kiracı bağlamsız (anonim) ya da sistem bağlamı (<c>UserId == null</c>) istekleri atlanır.</item>
/// <item>Erişim <c>None</c> ve istek <c>[TenantStatusExempt]</c> değilse → <c>403 tenant.suspended</c> (<c>args.reason</c> = etkin durum).</item>
/// <item>Erişim <c>ReadOnly</c> ve istek komut ve muaf değilse → <c>403 tenant.suspended</c>.</item>
/// <item>Modül (isteğin assembly'sinden) kapı modülü ve planda kapalıysa → <c>403 plan.module_disabled</c> (okuma dahil).</item>
/// <item>Komutta her <c>[ConsumesLimit]</c> için <see cref="ILimitGuard"/> → aşımda <c>402 plan.limit_exceeded</c>.</item>
/// </list>
/// Modül kararı komut başına bayrak gerektirmez (assembly'den türer) → unutulamaz.
/// </summary>
public sealed class EntitlementBehaviour<TRequest, TResponse>(
    ITenantContext tenant,
    ICurrentUser user,
    ITenantEntitlements entitlements,
    ILimitGuard limits,
    TimeProvider clock) : IPipelineBehaviour<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private static readonly bool PlatformOnly = typeof(TRequest).IsDefined(typeof(PlatformAdminOnlyAttribute), inherit: true);

    private static readonly bool StatusExempt = typeof(TRequest).IsDefined(typeof(TenantStatusExemptAttribute), inherit: true);

    private static readonly bool IsCommand = typeof(TRequest).GetInterfaces()
        .Any(i => i == typeof(ICommand) || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));

    private static readonly ConsumesLimitAttribute[] Consumes = typeof(TRequest).GetCustomAttributes<ConsumesLimitAttribute>(inherit: true).ToArray();

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (PlatformOnly || !tenant.IsResolved || user.UserId is null)
        {
            return await next().ConfigureAwait(false);
        }

        var snapshot = await entitlements.GetAsync(tenant.TenantId, cancellationToken).ConfigureAwait(false);
        var (status, access) = snapshot.Evaluate(clock.GetUtcNow());

        if (!StatusExempt && (access == AccessLevel.None || (access == AccessLevel.ReadOnly && IsCommand)))
        {
            CrmMetrics.EntitlementRejected(status, ModuleUnitOfWorkResolver.ModuleOf(typeof(TRequest).Assembly), snapshot.PlanCode);
            return ResultFactory.Fail<TResponse>(EntitlementErrors.Suspended(status));
        }

        var module = ModuleUnitOfWorkResolver.ModuleOf(typeof(TRequest).Assembly);
        if (module is not null && GatedModules.IsGated(module) && !snapshot.IsModuleEnabled(module))
        {
            CrmMetrics.EntitlementRejected("module_disabled", module, snapshot.PlanCode);
            return ResultFactory.Fail<TResponse>(EntitlementErrors.DisabledModule(module));
        }

        if (IsCommand)
        {
            foreach (var consume in Consumes)
            {
                var demandModule = string.Equals(consume.Key, LimitKeys.Users, StringComparison.Ordinal) ? module ?? "identity" : module;
                var check = await limits.EnsureAsync(new LimitDemand(consume.Key, demandModule, consume.Amount), cancellationToken).ConfigureAwait(false);
                if (check.IsFailure)
                {
                    CrmMetrics.EntitlementRejected("limit_exceeded", demandModule, snapshot.PlanCode);
                    return ResultFactory.Fail<TResponse>(check.Error);
                }
            }
        }

        return await next().ConfigureAwait(false);
    }
}
