using Microsoft.EntityFrameworkCore;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Tests;

/// <summary>
/// Q3 — Bağlam kurulmadığında davranış (plan D8): HTTP dışı/UseSystem sınırsız, HTTP'de kimliksiz = deny; AsyncLocal akışının sınırları.
/// <see cref="RecordScopeContext.FailClosedWhenUnset"/> süreç geneli statik olduğundan bu sınıf diğerleriyle paralel koşmaz (aynı koleksiyon) ve her testte sıfırlar.
/// </summary>
[Collection(PgCollection.Name)]
public sealed class Q3_FailClosedContextTests(PgFixture pg) : IAsyncLifetime
{
    private readonly World _w = new();

    public async ValueTask InitializeAsync()
    {
        RecordScopeContext.FailClosedWhenUnset = false;
        await _w.SeedAsync(pg);
    }

    public ValueTask DisposeAsync()
    {
        RecordScopeContext.FailClosedWhenUnset = false;
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<int> CountDealsAsync()
    {
        await using var db = pg.CreateMember();
        return await db.Deals.CountAsync(Ct);
    }

    [Fact]
    public async Task Unset_DefaultMode_IsUnrestricted_ThisIsThePlanD8NonHttpBehaviour()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        RecordScopeContext.IsSet.ShouldBeFalse();
        (await CountDealsAsync()).ShouldBe(14);
    }

    [Fact]
    public async Task Unset_FailClosedMode_ReturnsNothing_SoALostAsyncLocalCannotFailOpen()
    {
        RecordScopeContext.FailClosedWhenUnset = true;
        using var _t = new TenantContext().BeginScope(_w.T1);
        (await CountDealsAsync()).ShouldBe(0);

        // Açık sistem bağlamı hâlâ çalışır (Worker/outbox/seeder = UseSystem).
        using (RecordScopeContext.UseSystem())
        {
            (await CountDealsAsync()).ShouldBe(14);
        }

        (await CountDealsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task UseSystem_InsideUserScope_IsUnrestricted_AndRestoresUserScopeOnDispose()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _u = RecordScopeContext.Use(World.Own(_w.R1));
        (await CountDealsAsync()).ShouldBe(2);
        using (RecordScopeContext.UseSystem())
        {
            (await CountDealsAsync()).ShouldBe(14);
            using (RecordScopeContext.Use(World.Own(_w.R2)))
            {
                (await CountDealsAsync()).ShouldBe(2);
            }

            (await CountDealsAsync()).ShouldBe(14);
        }

        (await CountDealsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task AsyncLocal_FlowsIntoAwaitAndTaskRun_AndParallelScopesDoNotBleed()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _u = RecordScopeContext.Use(World.Own(_w.R1));
        (await Task.Run(CountDealsAsync, Ct)).ShouldBe(2);

        var results = await Task.WhenAll(_w.AllUsers.Select(async u =>
        {
            using var inner = RecordScopeContext.Use(World.Own(u));
            await Task.Yield();
            return (u, count: await CountDealsAsync(), snapshotUser: RecordScopeContext.Snapshot.UserId);
        }));
        results.ShouldAllBe(r => r.count == 2 && r.snapshotUser == r.u);
        (await CountDealsAsync()).ShouldBe(2, "kardeş görevlerin kapsamı ana akışa sızmaz");
    }

    [Fact]
    public async Task SuppressedExecutionContextFlow_LosesScope_DefaultModeFailsOpen_StrictModeFailsClosed()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _u = RecordScopeContext.Use(World.Own(_w.R1));

        // Tenant bağlamı da AsyncLocal'dir: akışı bastırınca o da kaybolur (Guid.Empty → kiracı filtresi sıfır satır döndürür = doğal kapalı-başarısız).
        // Kapsam ayrımını görmek için kiracıyı içeride yeniden kuruyoruz.
        async Task<int> InnerCountAsync()
        {
            using var tenant = new TenantContext().BeginScope(_w.T1);
            return await CountDealsAsync();
        }

        Task<int> lost;
        using (ExecutionContext.SuppressFlow())
        {
            lost = Task.Run(InnerCountAsync, Ct);
        }

        (await lost).ShouldBe(14, "VARSAYILAN kip: akış kaybolunca kapsam kurulmamış sayılır → sınırsız (fail-open) — plan D8'in kabul ettiği risk");

        RecordScopeContext.FailClosedWhenUnset = true;
        Task<int> strict;
        using (ExecutionContext.SuppressFlow())
        {
            strict = Task.Run(InnerCountAsync, Ct);
        }

        (await strict).ShouldBe(0, "SIKI kip: kurulmamış bağlam = hiçbir şey");
    }

    [Fact]
    public async Task FireAndForgetStartedInsideRequestScope_InheritsAndOutlivesIt_StaleButNotWider()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        var gate = new TaskCompletionSource();
        Task<int> background;
        using (RecordScopeContext.Use(World.Own(_w.R1)))
        {
            background = Task.Run(async () =>
            {
                await gate.Task;
                using var tenant = new TenantContext().BeginScope(_w.T1);
                return await CountDealsAsync();
            }, Ct);
        }

        // İstek bitti (scope Dispose). Arka plan görevi yakalanmış anlık görüntüyle devam eder: eski kullanıcının kapsamı (dar) — genişlemez.
        gate.SetResult();
        (await background).ShouldBe(2);
    }
}
