using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Sense.Crm.Spikes.M9h.Model;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Tests;

/// <summary>
/// Q2 (a) — RecordScope koşulu istek başına parametreleştirilebilir mi (AsyncLocal), <c>= ANY(@p)</c> olarak çevrilir mi, derlenmiş sorgu önbelleği
/// kullanıcı başına çoğalıyor / kullanıcılar arası zehirleniyor mu, havuzlu bağlamlarda sızıntı var mı.
/// </summary>
[Collection(PgCollection.Name)]
public sealed class Q2_ParameterisationTests(PgFixture pg) : IAsyncLifetime
{
    private readonly World _w = new();

    public async ValueTask InitializeAsync() => await _w.SeedAsync(pg);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<FilterStyle> Styles => new() { FilterStyle.ContextMember, FilterStyle.StaticWithContextArg };

    private SpikeDbContextBase Create(FilterStyle style, SqlCapture? capture = null) =>
        pg.Create(style, capture);

    private IDbContextFactory<SpikeDbContextBase> Pooled(FilterStyle style, SqlCapture? capture = null) => pg.Pooled(style, capture);

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task Filter_IsEvaluatedPerExecution_NotBakedAtCompileTime_TwoUsersInterleavedOnPooledFactory(FilterStyle style)
    {
        var capture = new SqlCapture();
        var factory = Pooled(style, capture);
        using var _t = new TenantContext().BeginScope(_w.T1);
        var own = World.Own(_w.R1);
        var boss = World.OwnAndSubs(_w.Boss, _w.BossSubordinates);
        var before = RecordScopeContext.EvaluationCount;

        for (var i = 0; i < 20; i++)
        {
            using (RecordScopeContext.Use(own))
            {
                await using var db = factory.CreateDbContext();
                var owners = await db.Deals.AsNoTracking().Select(d => d.OwnerUserId).Distinct().ToListAsync(Ct);
                owners.ShouldBe([_w.R1]);
            }

            using (RecordScopeContext.Use(boss))
            {
                await using var db = factory.CreateDbContext();
                var owners = await db.Deals.AsNoTracking().Select(d => d.OwnerUserId).Distinct().ToListAsync(Ct);
                owners.ToHashSet().SetEquals([_w.Boss, .. _w.BossSubordinates]).ShouldBeTrue();
            }
        }

        // 40 yürütme, TEK SQL metni: kullanıcıya göre değişen yalnız parametre değeri.
        capture.Commands.Select(c => c.Sql).Distinct().Count().ShouldBe(1);
        (RecordScopeContext.EvaluationCount - before).ShouldBeGreaterThanOrEqualTo(40, "filtre her yürütmede yeniden değerlendirilir (derleme anında sabitlenmez)");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task Sql_UsesAnyArrayParameter_NoLiteralIds_AndKeepsTenantAndSoftDeletePredicates(FilterStyle style)
    {
        var capture = new SqlCapture();
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _s = RecordScopeContext.Use(World.OwnAndSubs(_w.M1, _w.M1Subordinates));
        await using var db = Create(style, capture);

        await db.Deals.OrderByDescending(d => d.CreatedAt).Take(25).ToListAsync(Ct);

        var cmd = capture.Last;
        Evidence.Log($"[Q2 SQL/{style}] {cmd.Sql}");
        cmd.Sql.ShouldContain("= ANY (");
        cmd.Sql.ShouldContain("tenant_id");
        cmd.Sql.ShouldContain("is_deleted");
        cmd.Sql.ShouldNotContain(_w.M1.ToString());
        cmd.Parameters.Any(p => p.Value is Guid[] g && g.Length == 3).ShouldBeTrue("materialise edilmiş sahip kümesi TEK dizi parametresi");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task OwnerSetSizes_0_1_5_200_20000_ShareOneSqlText_AndOneCompiledQueryEntry(FilterStyle style)
    {
        var capture = new SqlCapture();
        using var _t = new TenantContext().BeginScope(_w.T1);
        var sizes = new[] { 0, 1, 5, 200, 20_000, 1 };
        var cacheCounts = new List<int>();

        foreach (var n in sizes)
        {
            var owners = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
            if (n > 0)
            {
                owners[0] = _w.R1;
            }

            using var _s = RecordScopeContext.Use(new RecordScopeSnapshot(_w.R1, false, new Dictionary<string, ResourceScope> { ["deal"] = ResourceScope.OwnAnd(owners) }));
            await using var db = Create(style, capture);
            var rows = await db.Deals.ToListAsync(Ct);
            rows.Count.ShouldBe(n == 0 ? 0 : 2);

            var cache = db.GetService<IMemoryCache>() as MemoryCache;
            cacheCounts.Add(cache?.Count ?? -1);
        }

        capture.Commands.Select(c => c.Sql).Distinct().Count().ShouldBe(1, "sahip kümesi boyutu SQL metnini değiştirmez (IN (@p1..@pN) genişlemesi yok)");
        Evidence.Log($"[Q2 cache/{style}] EF IMemoryCache.Count after sizes {string.Join(",", sizes)}: {string.Join(",", cacheCounts)}");
        cacheCounts.Distinct().Count().ShouldBeLessThanOrEqualTo(2, "kullanıcı/boyut başına yeni derlenmiş sorgu girdisi oluşmaz");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task ParallelUsersAndTenants_PooledFactory_NoCrossUserOrCrossTenantLeak(FilterStyle style)
    {
        var factory = Pooled(style);
        var users = _w.AllUsers;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var gate = new SemaphoreSlim(24); // Npgsql havuzunu (100) ve paylaşılan makineyi boğmamak için eşzamanlılığı sınırla
        var tasks = Enumerable.Range(0, 240).Select(async i =>
        {
            await gate.WaitAsync(Ct);
            try
            {
            var user = users[i % users.Length];
            var tenant = i % 2 == 0 ? _w.T1 : _w.T2;
            using var _t = new TenantContext().BeginScope(tenant);
            using var _s = RecordScopeContext.Use(World.Own(user));
            await Task.Yield();
            await using var db = factory.CreateDbContext();
            var rows = await db.Deals.AsNoTracking().ToListAsync(Ct);
            if (rows.Count != 2 || rows.Any(r => r.OwnerUserId != user || r.TenantId != tenant))
            {
                failures.Add($"i={i} user={user} tenant={tenant} got={rows.Count}");
            }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        failures.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task ReadAll_Unrestricted_ReturnsAllTenantRows_ButNeverOtherTenantsOrDeleted(FilterStyle style)
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _s = RecordScopeContext.Use(World.Admin(_w.Boss));
        await using var db = Create(style);
        var rows = await db.Deals.ToListAsync(Ct);
        rows.Count.ShouldBe(14);
        rows.ShouldAllBe(d => d.TenantId == _w.T1 && !d.IsDeleted);
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task EmptyOwnerSet_ReturnsNothing_DenySnapshotReturnsNothing(FilterStyle style)
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        await using var db = Create(style);
        using (RecordScopeContext.Use(new RecordScopeSnapshot(_w.R1, false, new Dictionary<string, ResourceScope> { ["deal"] = ResourceScope.OwnAnd([]) })))
        {
            (await db.Deals.CountAsync(Ct)).ShouldBe(0);
        }

        using (RecordScopeContext.Use(RecordScopeSnapshot.Deny))
        {
            (await db.Deals.CountAsync(Ct)).ShouldBe(0);
            (await db.Accounts.CountAsync(Ct)).ShouldBe(0);
            (await db.Cases.CountAsync(Ct)).ShouldBe(0);
            (await db.Products.CountAsync(Ct)).ShouldBe(1, "kapsamsız katalog (yalnız Tenant filtresi) deny'dan etkilenmez");
        }
    }
}
