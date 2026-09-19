using Crm.Shared.Contracts.Configuration;
using Crm.Shared.Contracts.Messaging;
using Crm.Shared.Contracts.Security;
using Crm.Shared.Infrastructure.Caching;
using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.Messaging.Behaviours;
using Crm.Shared.Kernel.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Crm.Shared.Infrastructure.Tests;

/// <summary>
/// Regresyon: HybridCache factory'yi ExecutionContext akıtmadan çalıştırdığı için kiracı bağlamı kayboluyor,
/// izin servisi kullanıcıyı bulamayıp boş izin setini önbelleğe yazıyordu (Owner rolü 403 alıyordu).
/// </summary>
public sealed class CachingContextTests
{
    private static HybridCache CreateCache() =>
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();

    [Fact]
    public async Task GetOrCreateInContextAsync_FactorySeesCallerTenantScope()
    {
        var cache = CreateCache();
        var tenant = new TenantContext();
        var tenantId = Guid.CreateVersion7();

        using var scope = tenant.BeginScope(tenantId);
        var seen = await cache.GetOrCreateInContextAsync(
            "probe:" + Guid.NewGuid().ToString("N"),
            _ => ValueTask.FromResult(tenant.IsResolved ? tenant.TenantId : Guid.Empty),
            cancellationToken: TestContext.Current.CancellationToken);

        seen.ShouldBe(tenantId);
    }

    [Fact]
    public async Task GetOrCreateInContextAsync_AsyncFactoryKeepsScopeAcrossAwaits()
    {
        var cache = CreateCache();
        var tenant = new TenantContext();
        var tenantId = Guid.CreateVersion7();

        using var scope = tenant.BeginScope(tenantId);
        var seen = await cache.GetOrCreateInContextAsync(
            "probe:" + Guid.NewGuid().ToString("N"),
            async ct =>
            {
                await Task.Delay(10, ct);
                await Task.Yield();
                return tenant.IsResolved ? tenant.TenantId : Guid.Empty;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        seen.ShouldBe(tenantId);
    }

    [Fact]
    public async Task CachingBehaviour_RunsHandlerInTenantScope_AndCachesSuccessValue()
    {
        var cache = CreateCache();
        var tenant = new TenantContext();
        var tenantId = Guid.CreateVersion7();
        var behaviour = new CachingBehaviour<ProbeQuery, Result<string>>(cache, tenant, Options.Create(new CachingOptions()));
        var calls = 0;

        Task<Result<string>> Handler()
        {
            calls++;
            return Task.FromResult(Result.Success(tenant.IsResolved ? tenant.TenantId.ToString("N") : string.Empty));
        }

        using var scope = tenant.BeginScope(tenantId);
        var query = new ProbeQuery("probe");

        var first = await behaviour.Handle(query, Handler, TestContext.Current.CancellationToken);
        var second = await behaviour.Handle(query, Handler, TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe(tenantId.ToString("N"));
        second.Value.ShouldBe(tenantId.ToString("N"));
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task CachingBehaviour_DoesNotCacheFailures()
    {
        var cache = CreateCache();
        var tenant = new TenantContext();
        var behaviour = new CachingBehaviour<ProbeQuery, Result<string>>(cache, tenant, Options.Create(new CachingOptions()));
        var calls = 0;

        Task<Result<string>> Handler()
        {
            calls++;
            return Task.FromResult(Result.Failure<string>(Error.NotFound(ProbeErrors.Missing)));
        }

        using var scope = tenant.BeginScope(Guid.CreateVersion7());
        var query = new ProbeQuery("missing");

        var first = await behaviour.Handle(query, Handler, TestContext.Current.CancellationToken);
        var second = await behaviour.Handle(query, Handler, TestContext.Current.CancellationToken);

        first.IsFailure.ShouldBeTrue();
        first.Error.Code.ShouldBe(ProbeErrors.Missing);
        second.IsFailure.ShouldBeTrue();
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task CachingBehaviour_IsolatesTenants()
    {
        var cache = CreateCache();
        var tenant = new TenantContext();
        var behaviour = new CachingBehaviour<ProbeQuery, Result<string>>(cache, tenant, Options.Create(new CachingOptions()));
        var query = new ProbeQuery("same-key");

        Task<Result<string>> Handler() => Task.FromResult(Result.Success(tenant.TenantId.ToString("N")));

        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        Result<string> a;
        using (tenant.BeginScope(tenantA))
        {
            a = await behaviour.Handle(query, Handler, TestContext.Current.CancellationToken);
        }

        Result<string> b;
        using (tenant.BeginScope(tenantB))
        {
            b = await behaviour.Handle(query, Handler, TestContext.Current.CancellationToken);
        }

        a.Value.ShouldBe(tenantA.ToString("N"));
        b.Value.ShouldBe(tenantB.ToString("N"));
    }

    public sealed record ProbeQuery(string CacheKey) : ICachedQuery<string>;

    private static class ProbeErrors
    {
        public const string Missing = "probe.missing";
    }
}
