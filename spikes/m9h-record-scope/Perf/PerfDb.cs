using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Sense.Crm.Modules.Sales.Domain.Accounts;
using Sense.Crm.Modules.Sales.Domain.Deals;
using Sense.Crm.Modules.Sales.Infrastructure.Persistence;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.DependencyInjection;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;

namespace Sense.Crm.Spikes.M9h.Perf;

/// <summary>
/// GERÇEK <see cref="SalesDbContext"/> (mühürlü, gerçek migration'lar: sales.deals + 6 gerçek indeks) üzerine ürün kodu DEĞİŞTİRİLMEDEN üçüncü adlı filtreyi ekler:
/// <c>ReplaceService&lt;IModelCustomizer&gt;</c> → <c>Entity&lt;Deal&gt;().HasQueryFilter("RecordScope", …)</c> (stil C). Kanıt: gerçek kod tabanına dokunmadan filtre eklenebilir.
/// </summary>
public sealed class ScopeModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        var ctx = Expression.Constant(context);
        modelBuilder.Entity<Deal>().HasQueryFilter(RecordScopeModelExtensions.FilterName,
            RecordScopeModelExtensions.OwnerFilter<Deal>(FilterStyle.StaticWithContextArg, ctx, "deal", d => d.OwnerUserId));
        modelBuilder.Entity<Account>().HasQueryFilter(RecordScopeModelExtensions.FilterName,
            RecordScopeModelExtensions.OwnerFilter<Account>(FilterStyle.StaticWithContextArg, ctx, "account", a => a.OwnerUserId));
        modelBuilder.Entity<Sense.Crm.Modules.Sales.Domain.Contacts.Contact>().HasQueryFilter(RecordScopeModelExtensions.FilterName,
            RecordScopeModelExtensions.OwnerFilter<Sense.Crm.Modules.Sales.Domain.Contacts.Contact>(FilterStyle.StaticWithContextArg, ctx, "contact", a => a.OwnerUserId));
        modelBuilder.Entity<Sense.Crm.Modules.Sales.Domain.Leads.Lead>().HasQueryFilter(RecordScopeModelExtensions.FilterName,
            RecordScopeModelExtensions.OwnerFilter<Sense.Crm.Modules.Sales.Domain.Leads.Lead>(FilterStyle.StaticWithContextArg, ctx, "lead", a => a.OwnerUserId));
    }
}

/// <summary>Ayrı veritabanı <c>h0perf</c>: gerçek Sales migration'ları + sunucu tarafı tohumlama (generate_series). Süreç başına bir kez.</summary>
public static class PerfDb
{
    public const int T1Deals = 200_000;
    public const int UserCount = 250;

    public static readonly Guid T1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid T2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid T3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _connectionString;

    /// <summary>Kullanıcı i'nin kimliği (SQL'deki <c>md5('user'||i)::uuid</c> ile aynı).</summary>
    public static Guid User(int i)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"user{i}"));
        return new Guid(Convert.ToHexString(hash));
    }

    public static async Task<string> EnsureAsync(PgFixture pg)
    {
        await Gate.WaitAsync();
        try
        {
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            var cs = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = "h0perf" }.ConnectionString;
            await using (var admin = await pg.OpenAsync())
            {
                await using var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname='h0perf'", admin);
                if ((long)(await exists.ExecuteScalarAsync())! > 0)
                {
                    // H0_KEEP=1 ile yeniden kullanım: tohum eksiksizse atla.
                    try
                    {
                        await using var probe = new NpgsqlConnection(cs);
                        await probe.OpenAsync();
                        await using var cnt = new NpgsqlCommand("SELECT count(*) FROM sales.deals", probe);
                        if ((long)(await cnt.ExecuteScalarAsync())! >= 300_000)
                        {
                            _connectionString = cs;
                            return cs;
                        }
                    }
                    catch (PostgresException)
                    {
                        // tablo yok: aşağıda yeniden kur
                    }

                    await using var drop = new NpgsqlCommand("DROP DATABASE h0perf WITH (FORCE)", admin);
                    await drop.ExecuteNonQueryAsync();
                }

                await using var create = new NpgsqlCommand("CREATE DATABASE h0perf", admin);
                await create.ExecuteNonQueryAsync();
            }

            await using (var ctx = Create(cs))
            {
                await ctx.Database.MigrateAsync();
            }

            var sw = Stopwatch.StartNew();
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync();
            foreach (var (tenant, deals) in new[] { (T1, T1Deals), (T2, 60_000), (T3, 40_000) })
            {
                await SeedTenantAsync(c, tenant, deals);
            }

            await using (var analyze = new NpgsqlCommand("ANALYZE sales.deals; ANALYZE sales.accounts;", c))
            {
                await analyze.ExecuteNonQueryAsync();
            }

            Evidence.Log($"[Q4 seed] 300k deals seeded in {sw.Elapsed.TotalSeconds:F1}s (T1={T1Deals}, T2=60000, T3=40000; {UserCount} users)", "perf.txt");
            _connectionString = cs;
            return cs;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task SeedTenantAsync(NpgsqlConnection c, Guid tenant, int deals)
    {
        var pipeline = Guid.NewGuid();
        const string sql = """
            INSERT INTO sales.pipelines(id, name, is_default, created_at, tenant_id) VALUES (@pipe, 'p', true, now(), @t);
            INSERT INTO sales.pipeline_stages(id, pipeline_id, name, "order", probability, kind, is_deleted, created_at, tenant_id)
              SELECT gen_random_uuid(), @pipe, 'stage'||g, g, g*10, 'open', false, now(), @t FROM generate_series(1,6) g;
            INSERT INTO sales.accounts(id, name, owner_user_id, is_deleted, created_at, tenant_id)
              SELECT gen_random_uuid(), 'acc'||g, md5('user'||((g*31) % 250))::uuid, false, now(), @t FROM generate_series(1,5000) g;
            INSERT INTO sales.deals(id, name, account_id, pipeline_id, stage_id, amount, currency, owner_user_id, is_deleted, created_at, tenant_id)
              SELECT gen_random_uuid(), 'deal '||g, a.ids[1 + (g % 5000)], @pipe, s.ids[1 + (g % 6)], (g % 1000)::numeric, 'TRY',
                     md5('user'||((g*7919) % 250))::uuid, (g % 47 = 0), now() - (g || ' seconds')::interval, @t
              FROM generate_series(1, @n) g,
                   (SELECT array_agg(id) AS ids FROM sales.accounts WHERE tenant_id = @t) a,
                   (SELECT array_agg(id) AS ids FROM sales.pipeline_stages WHERE pipeline_id = @pipe) s;
            """;
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("pipe", pipeline);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("n", deals);
        await cmd.ExecuteNonQueryAsync();
    }

    public static SalesDbContext Create(string cs, SqlCapture? capture = null, bool withScope = true)
    {
        var b = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql(cs, npgsql => npgsql.MigrationsHistoryTable(InfrastructureServiceCollectionExtensions.MigrationsHistoryTable, SalesDbContext.SchemaName))
            .UseSnakeCaseNamingConvention();
        if (withScope)
        {
            b.ReplaceService<IModelCustomizer, ScopeModelCustomizer>();
        }

        if (capture is not null)
        {
            b.AddInterceptors(capture);
        }

        return new SalesDbContext(b.Options, new TenantContext());
    }
}

/// <summary>Zamanlama + EXPLAIN (ANALYZE, BUFFERS) yardımcıları.</summary>
public static class Bench
{
    public sealed record Timing(double P50Ms, double P95Ms, double MinMs);

    public static async Task<Timing> TimeAsync(Func<Task> action, int warmup = 5, int runs = 40)
    {
        for (var i = 0; i < warmup; i++)
        {
            await action();
        }

        var samples = new List<double>(runs);
        for (var i = 0; i < runs; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await action();
            samples.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        samples.Sort();
        return new Timing(samples[runs / 2], samples[(int)(runs * 0.95)], samples[0]);
    }

    public static async Task<string> ExplainAsync(string cs, CapturedCommand cmdInfo)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        string last = string.Empty;
        for (var i = 0; i < 3; i++)
        {
            await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, SETTINGS) " + cmdInfo.Sql, c);
            foreach (var (name, value) in cmdInfo.Parameters)
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var lines = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                lines.Add(r.GetString(0));
            }

            last = string.Join('\n', lines);
        }

        return last;
    }

    /// <summary>Plan metninden tek satırlık özet: düğüm türleri + Execution Time + Buffers.</summary>
    public static string Summarise(string plan)
    {
        var nodes = plan.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("Scan") || l.Contains("Join") || l.Contains("Sort") || l.Contains("Aggregate") || l.Contains("Limit") || l.Contains("Append"))
            .Select(l => l.Split("  (")[0].TrimStart('-', '>', ' '))
            .Take(6);
        var exec = plan.Split('\n').FirstOrDefault(l => l.StartsWith("Execution Time", StringComparison.Ordinal)) ?? "?";
        var hits = plan.Split('\n').FirstOrDefault(l => l.Trim().StartsWith("Buffers:", StringComparison.Ordinal))?.Trim() ?? string.Empty;
        return $"{string.Join(" > ", nodes)} | {exec} | {hits}";
    }
}
