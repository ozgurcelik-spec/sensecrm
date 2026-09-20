using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sense.Crm.Modules.Sales.Domain.Deals;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Perf;

/// <summary>
/// Q4 — Postgres ölçümü. GERÇEK SalesDbContext + gerçek migration + 300k satır (T1: 200k fırsat, 250 kullanıcı), gerçek indeksler:
/// <c>(tenant_id, owner_user_id)</c>, <c>(tenant_id, created_at)</c>, <c>(tenant_id, stage_id)</c> …
/// Senaryolar: filtresiz taban çizgisi, ReadAll (yönetici), 0 / 5 / 200 ast. Sorgular: liste (25 satır, created_at sıralı), aşamaya göre liste, COUNT, detay (görünür/görünmez).
/// </summary>
[Collection(PgCollection.Name)]
[Trait("Category", "Perf")]
public sealed class Q4_PerfTests(PgFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Scenario(string Name, Func<IQueryable<Deal>, IQueryable<Deal>> Shape, RecordScopeSnapshot? Scope);

    private static RecordScopeSnapshot Owners(int subordinates) =>
        new(PerfDb.User(0), false, new Dictionary<string, ResourceScope>
        {
            ["deal"] = ResourceScope.OwnAnd(Enumerable.Range(0, subordinates + 1).Select(PerfDb.User)),
            ["account"] = ResourceScope.OwnAnd(Enumerable.Range(0, subordinates + 1).Select(PerfDb.User)),
        });

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new("baseline: no RecordScope filter (today)", q => q.IgnoreRecordScope(), null);
        yield return new("ReadAll (admin) through filter", q => q, RecordScopeSnapshot.System);
        yield return new("own (0 subordinates, 1 owner)", q => q, Owners(0));
        yield return new("5 subordinates (6 owners)", q => q, Owners(5));
        yield return new("200 subordinates (201 owners)", q => q, Owners(200));
    }

    [Fact]
    public async Task RealSalesContext_GetsThirdNamedFilter_WithoutProductChanges_AndScopedResultsMatchSql()
    {
        var cs = await PerfDb.EnsureAsync(pg);
        using var _t = new TenantContext().BeginScope(PerfDb.T1);
        await using var db = PerfDb.Create(cs);

        var deal = db.Model.FindEntityType(typeof(Deal))!;
        deal.GetDeclaredQueryFilters().Select(f => f.Key).ShouldBe(["Tenant", "SoftDelete", "RecordScope"], ignoreOrder: true);

        foreach (var subs in new[] { 0, 5, 200 })
        {
            using var _s = RecordScopeContext.Use(Owners(subs));
            var expected = await ScalarAsync(cs, "SELECT count(*) FROM sales.deals WHERE tenant_id=@t AND NOT is_deleted AND owner_user_id = ANY(@o)",
                ("t", PerfDb.T1), ("o", Enumerable.Range(0, subs + 1).Select(PerfDb.User).ToArray()));
            (await db.Deals.CountAsync(Ct)).ShouldBe((int)(long)expected!);
        }
    }

    [Fact]
    public async Task ListCountDetail_Matrix_IsWithinPlanThresholds_AndUsesOwnerIndexForSmallSets()
    {
        var cs = await PerfDb.EnsureAsync(pg);
        var stageId = (Guid)(await ScalarAsync(cs, "SELECT id FROM sales.pipeline_stages WHERE tenant_id=@t ORDER BY \"order\" LIMIT 1", ("t", PerfDb.T1)))!;
        var ownedId = (Guid)(await ScalarAsync(cs, "SELECT id FROM sales.deals WHERE tenant_id=@t AND owner_user_id=@u AND NOT is_deleted LIMIT 1", ("t", PerfDb.T1), ("u", PerfDb.User(0))))!;
        var hiddenId = (Guid)(await ScalarAsync(cs, "SELECT id FROM sales.deals WHERE tenant_id=@t AND owner_user_id=@u AND NOT is_deleted LIMIT 1", ("t", PerfDb.T1), ("u", PerfDb.User(249))))!;

        var report = new StringBuilder();
        var plans = new StringBuilder();
        report.AppendLine("| scenario | query | rows | EF p50 ms | EF p95 ms | plan (root nodes) |");
        report.AppendLine("|---|---|---|---|---|---|");
        var p95 = new Dictionary<string, double>();

        using var _t = new TenantContext().BeginScope(PerfDb.T1);
        foreach (var sc in Scenarios())
        {
            using var _s = sc.Scope is null ? RecordScopeContext.UseSystem() : RecordScopeContext.Use(sc.Scope);
            var queries = new (string Name, Func<Microsoft.EntityFrameworkCore.DbContext, Task<int>> Run)[]
            {
                ("list25", async d => (await sc.Shape(((Sense.Crm.Modules.Sales.Infrastructure.Persistence.SalesDbContext)d).Deals).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(25)
                    .Select(x => new { x.Id, x.Name, x.Amount, x.StageId, x.OwnerUserId, x.CreatedAt }).ToListAsync(Ct)).Count),
                ("list25+stage", async d => (await sc.Shape(((Sense.Crm.Modules.Sales.Infrastructure.Persistence.SalesDbContext)d).Deals).Where(x => x.StageId == stageId).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(25)
                    .Select(x => new { x.Id, x.Name, x.Amount, x.StageId, x.OwnerUserId, x.CreatedAt }).ToListAsync(Ct)).Count),
                ("count", async d => await sc.Shape(((Sense.Crm.Modules.Sales.Infrastructure.Persistence.SalesDbContext)d).Deals).CountAsync(Ct)),
                ("detail(visible)", async d => (await sc.Shape(((Sense.Crm.Modules.Sales.Infrastructure.Persistence.SalesDbContext)d).Deals).FirstOrDefaultAsync(x => x.Id == ownedId, Ct)) is null ? 0 : 1),
                ("detail(hidden)", async d => (await sc.Shape(((Sense.Crm.Modules.Sales.Infrastructure.Persistence.SalesDbContext)d).Deals).FirstOrDefaultAsync(x => x.Id == hiddenId, Ct)) is null ? 0 : 1),
            };

            foreach (var (name, run) in queries)
            {
                var capture = new SqlCapture();
                await using var db = PerfDb.Create(cs, capture);
                var rows = await run(db);
                var timing = await Bench.TimeAsync(async () =>
                {
                    await using var fresh = PerfDb.Create(cs);
                    await run(fresh);
                });

                var plan = await Bench.ExplainAsync(cs, capture.Last);
                plans.AppendLine($"=== {sc.Name} / {name} ===\n{capture.Last.Sql}\n{plan}\n");
                report.AppendLine($"| {sc.Name} | {name} | {rows} | {timing.P50Ms:F2} | {timing.P95Ms:F2} | {Bench.Summarise(plan).Replace("|", "/")} |");
                p95[$"{sc.Name}/{name}"] = timing.P95Ms;
            }
        }

        Evidence.Write("perf-matrix.md", report.ToString());
        Evidence.Write("perf-plans.txt", plans.ToString());

        // Plan eşikleri (1M satır/10k üye için yazılmıştı; burada 200k): own liste p95 < 150 ms, 201 sahipli küme liste p95 < 400 ms.
        p95["own (0 subordinates, 1 owner)/list25"].ShouldBeLessThan(150);
        p95["200 subordinates (201 owners)/list25"].ShouldBeLessThan(400);
        p95["own (0 subordinates, 1 owner)/detail(hidden)"].ShouldBeLessThan(50);
    }

    [Fact]
    public async Task CompositeOwnerCreatedIndex_ChangesPlansForSmallOwnerSets_Measured()
    {
        var cs = await PerfDb.EnsureAsync(pg);
        var report = new StringBuilder();
        report.AppendLine("| index set | scenario | list25 EF p50 ms | list25 EF p95 ms | plan |");
        report.AppendLine("|---|---|---|---|---|");

        async Task Measure(string label)
        {
            using var _t = new TenantContext().BeginScope(PerfDb.T1);
            foreach (var subs in new[] { 0, 5, 200 })
            {
                using var _s = RecordScopeContext.Use(Owners(subs));
                var capture = new SqlCapture();
                await using (var one = PerfDb.Create(cs, capture))
                {
                    await one.Deals.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(25).Select(x => new { x.Id, x.Name, x.CreatedAt }).ToListAsync(Ct);
                }

                var timing = await Bench.TimeAsync(async () =>
                {
                    await using var fresh = PerfDb.Create(cs);
                    await fresh.Deals.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(25).Select(x => new { x.Id, x.Name, x.CreatedAt }).ToListAsync(Ct);
                });
                var plan = await Bench.ExplainAsync(cs, capture.Last);
                report.AppendLine($"| {label} | {subs} subs | {timing.P50Ms:F2} | {timing.P95Ms:F2} | {Bench.Summarise(plan).Replace("|", "/")} |");
            }
        }

        await Measure("existing: (tenant,owner) + (tenant,created_at)");
        await pg.ExecAsyncOn(cs, "CREATE INDEX ix_deals_perf_owner_created ON sales.deals (tenant_id, owner_user_id, created_at DESC, id); ANALYZE sales.deals;");
        try
        {
            await Measure("+ (tenant,owner,created_at DESC,id)");
        }
        finally
        {
            await pg.ExecAsyncOn(cs, "DROP INDEX sales.ix_deals_perf_owner_created; ANALYZE sales.deals;");
        }

        Evidence.Write("perf-index-variants.md", report.ToString());
    }

    private static async Task<object?> ScalarAsync(string cs, string sql, params (string Name, object Value)[] ps)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v);
        }

        return await cmd.ExecuteScalarAsync(Ct);
    }
}
