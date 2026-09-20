using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Perf;

/// <summary>
/// Q4 (devam) — sahip kümesini SQL'e taşıma biçimleri: <c>= ANY(uuid[])</c> (önerilen), <c>JOIN unnest</c>, <c>IN (SELECT unnest)</c>, kalıcı özet tablo,
/// sabit (literal) IN listesi (EF "Constant" çevirisi) ve istek başına geçici tablo. 6 / 201 / 2001 / 20001 sahip; liste + COUNT. Ayrıca "seyrek sahip" (needle) kötü senaryosu.
/// </summary>
[Collection(PgCollection.Name)]
[Trait("Category", "Perf")]
public sealed class Q4_AnyVsJoinTests(PgFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ListSelect = "d.id, d.name, d.amount, d.stage_id, d.owner_user_id, d.created_at";

    private static Guid[] OwnerSet(int n) =>
        [.. Enumerable.Range(0, n).Select(i => i < PerfDb.UserCount ? PerfDb.User(i) : new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes($"dummy{i}"))))];

    [Fact]
    public async Task OwnerSetTransport_AnyArray_VsJoinUnnest_VsPersistedSet_VsLiteralIn_VsTempTable()
    {
        var cs = await PerfDb.EnsureAsync(pg);
        await pg.ExecAsyncOn(cs, "CREATE TABLE IF NOT EXISTS public.h0_owner_sets(tenant_id uuid NOT NULL, user_id uuid NOT NULL, owner_id uuid NOT NULL, PRIMARY KEY (tenant_id, user_id, owner_id))");

        var report = new StringBuilder();
        report.AppendLine("| owners | variant | list25 wall p50 ms | list25 wall p95 ms | list25 plan exec ms | count wall p50 ms | count plan exec ms | rows(count) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");
        var ratios = new List<string>();

        foreach (var n in new[] { 6, 201, 2001, 20001 })
        {
            var owners = OwnerSet(n);
            var setUser = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes($"setuser{n}")));
            await pg.ExecAsyncOn(cs, $"DELETE FROM public.h0_owner_sets WHERE user_id = '{setUser}'");
            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync(Ct);
                await using var ins = new NpgsqlCommand("INSERT INTO public.h0_owner_sets SELECT @t, @u, x FROM unnest(@o) x", conn);
                ins.Parameters.AddWithValue("t", PerfDb.T1);
                ins.Parameters.AddWithValue("u", setUser);
                ins.Parameters.AddWithValue("o", owners);
                await ins.ExecuteNonQueryAsync(Ct);
                await using var an = new NpgsqlCommand("ANALYZE public.h0_owner_sets", conn);
                await an.ExecuteNonQueryAsync(Ct);
            }

            var literal = string.Join(",", owners.Select(o => $"'{o}'"));
            var variants = new (string Name, string Where, string From, bool Temp)[]
            {
                ("A = ANY(@o)  [recommended]", "d.owner_user_id = ANY(@o)", "sales.deals d", false),
                ("B JOIN unnest(@o)", "true", "sales.deals d JOIN unnest(@o) AS u(id) ON u.id = d.owner_user_id", false),
                ("C IN (SELECT unnest(@o))", "d.owner_user_id IN (SELECT unnest(@o))", "sales.deals d", false),
                ("D persisted set table", "d.owner_user_id IN (SELECT owner_id FROM public.h0_owner_sets WHERE tenant_id=@t AND user_id=@u)", "sales.deals d", false),
                ("E literal IN (EF Constant mode)", $"d.owner_user_id IN ({literal})", "sales.deals d", false),
                ("F per-request TEMP table", "d.owner_user_id IN (SELECT id FROM h0_tmp_owners)", "sales.deals d", true),
            };

            foreach (var (name, where, from, temp) in variants)
            {
                if (name.StartsWith("E ", StringComparison.Ordinal) && n > 2001)
                {
                    continue; // 20k literal: SQL metni ~800 KB; her istekte ayrıştırma — ölçmeye değmez, tabloda "n/a"
                }

                var listSql = $"SELECT {ListSelect} FROM {from} WHERE d.tenant_id=@t AND NOT d.is_deleted AND {where} ORDER BY d.created_at DESC, d.id LIMIT 25";
                var countSql = $"SELECT count(*) FROM {from} WHERE d.tenant_id=@t AND NOT d.is_deleted AND {where}";

                var listT = await Bench.TimeAsync(() => RunAsync(cs, listSql, owners, setUser, temp), warmup: 3, runs: 25);
                var countT = await Bench.TimeAsync(() => RunAsync(cs, countSql, owners, setUser, temp), warmup: 3, runs: 25);
                var listExec = await ExplainMsAsync(cs, listSql, owners, setUser, temp);
                var countExec = await ExplainMsAsync(cs, countSql, owners, setUser, temp);
                var rows = await CountAsync(cs, countSql, owners, setUser, temp);
                report.AppendLine($"| {n} | {name} | {listT.P50Ms:F2} | {listT.P95Ms:F2} | {listExec:F2} | {countT.P50Ms:F2} | {countExec:F2} | {rows} |");
                if (name.StartsWith("A ", StringComparison.Ordinal) && n == 201)
                {
                    listT.P95Ms.ShouldBeLessThan(400, "plan eşiği (201 sahip, liste p95)");
                }
            }
        }

        Evidence.Write("perf-transport.md", report.ToString());
        _ = ratios;
    }

    [Fact]
    public async Task SparseOwner_NeedleListOrderedByCreatedAt_IsThePlannerRisk_CompositeIndexMitigates()
    {
        var cs = await PerfDb.EnsureAsync(pg);
        var needle = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
        await pg.ExecAsyncOn(cs, $"""
            DELETE FROM sales.deals WHERE owner_user_id = '{needle}';
            INSERT INTO sales.deals(id, name, account_id, pipeline_id, stage_id, amount, currency, owner_user_id, is_deleted, created_at, tenant_id)
            SELECT gen_random_uuid(), 'needle '||g, d.account_id, d.pipeline_id, d.stage_id, 1, 'TRY', '{needle}', false, now() - interval '400 days' - (g||' minutes')::interval, d.tenant_id
            FROM generate_series(1,20) g, (SELECT account_id, pipeline_id, stage_id, tenant_id FROM sales.deals WHERE tenant_id='{PerfDb.T1}' LIMIT 1) d;
            ANALYZE sales.deals;
            """);

        var report = new StringBuilder();
        report.AppendLine("| index set | scenario | list25 wall p50 ms | list25 wall p95 ms | plan exec ms | plan |");
        report.AppendLine("|---|---|---|---|---|---|");

        async Task Measure(string label)
        {
            foreach (var (scen, owners) in new[] { ("needle (20 rows, oldest)", new[] { needle }), ("common owner (user 3)", new[] { PerfDb.User(3) }), ("owner with ZERO rows (new hire)", new[] { Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b") }) })
            {
                var sql = $"SELECT {ListSelect} FROM sales.deals d WHERE d.tenant_id=@t AND NOT d.is_deleted AND d.owner_user_id = ANY(@o) ORDER BY d.created_at DESC, d.id LIMIT 25";
                var t = await Bench.TimeAsync(() => RunAsync(cs, sql, owners, Guid.Empty, false), warmup: 3, runs: 25);
                var plan = await ExplainAsync(cs, sql, owners);
                var exec = plan.Split('\n').First(l => l.StartsWith("Execution Time", StringComparison.Ordinal));
                report.AppendLine($"| {label} | {scen} | {t.P50Ms:F2} | {t.P95Ms:F2} | {exec} | {Bench.Summarise(plan).Split('|')[0].Replace("Sort Method", "SM")} |");
            }
        }

        await Measure("existing indexes");
        await pg.ExecAsyncOn(cs, "CREATE INDEX ix_deals_perf_owner_created ON sales.deals (tenant_id, owner_user_id, created_at DESC, id); ANALYZE sales.deals;");
        try
        {
            await Measure("+ (tenant_id, owner_user_id, created_at DESC, id)");
        }
        finally
        {
            await pg.ExecAsyncOn(cs, "DROP INDEX sales.ix_deals_perf_owner_created; ANALYZE sales.deals;");
        }

        // Hafifletme: owner_user_id için istatistik hedefi 1000 → 250 sahibin tümü MCV listesinde; needle/sıfır-satır sahip için tahmin doğrulanır.
        await pg.ExecAsyncOn(cs, "ALTER TABLE sales.deals ALTER COLUMN owner_user_id SET STATISTICS 1000; ANALYZE sales.deals;");
        try
        {
            await Measure("existing indexes + STATISTICS 1000 on owner_user_id");
        }
        finally
        {
            await pg.ExecAsyncOn(cs, "ALTER TABLE sales.deals ALTER COLUMN owner_user_id SET STATISTICS -1; DELETE FROM sales.deals WHERE owner_user_id = '" + needle + "'; ANALYZE sales.deals;");
        }

        Evidence.Write("perf-sparse-owner.md", report.ToString());
    }

    private static NpgsqlCommand Build(NpgsqlConnection c, string sql, Guid[] owners, Guid setUser)
    {
        var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("t", PerfDb.T1);
        if (sql.Contains("@o", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("o", owners);
        }

        if (sql.Contains("@u", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("u", setUser);
        }

        return cmd;
    }

    private static async Task<long> RunAsync(string cs, string sql, Guid[] owners, Guid setUser, bool temp)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        if (temp)
        {
            await PrepareTempAsync(c, owners);
        }

        await using var cmd = Build(c, sql, owners, setUser);
        long n = 0;
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct))
        {
            n++;
        }

        return n;
    }

    private static async Task<long> CountAsync(string cs, string sql, Guid[] owners, Guid setUser, bool temp)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        if (temp)
        {
            await PrepareTempAsync(c, owners);
        }

        await using var cmd = Build(c, sql, owners, setUser);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task PrepareTempAsync(NpgsqlConnection c, Guid[] owners)
    {
        await using var create = new NpgsqlCommand("CREATE TEMP TABLE h0_tmp_owners (id uuid PRIMARY KEY) ON COMMIT DROP", c);
        // ON COMMIT DROP autocommit'te hemen düşer; oturum temp tablosu olarak tut
        create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS h0_tmp_owners (id uuid PRIMARY KEY)";
        await create.ExecuteNonQueryAsync(Ct);
        await using var ins = new NpgsqlCommand("TRUNCATE h0_tmp_owners; INSERT INTO h0_tmp_owners SELECT DISTINCT x FROM unnest(@o) x; ANALYZE h0_tmp_owners", c);
        ins.Parameters.AddWithValue("o", owners);
        await ins.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> ExplainAsync(string cs, string sql, Guid[] owners)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        var lines = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            lines.Clear();
            await using var cmd = Build(c, "EXPLAIN (ANALYZE, BUFFERS) " + sql, owners, Guid.Empty);
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct))
            {
                lines.Add(r.GetString(0));
            }
        }

        return string.Join('\n', lines);
    }

    private static async Task<double> ExplainMsAsync(string cs, string sql, Guid[] owners, Guid setUser, bool temp)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        if (temp)
        {
            await PrepareTempAsync(c, owners);
        }

        double last = 0;
        for (var i = 0; i < 3; i++)
        {
            await using var cmd = Build(c, "EXPLAIN (ANALYZE) " + sql, owners, setUser);
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct))
            {
                var line = r.GetString(0);
                if (line.StartsWith("Execution Time:", StringComparison.Ordinal))
                {
                    last = double.Parse(line.Split(':')[1].Replace("ms", string.Empty).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }

        return last;
    }
}
