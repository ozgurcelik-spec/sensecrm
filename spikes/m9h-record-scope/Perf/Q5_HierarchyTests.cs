using System.Diagnostics;
using System.Text;
using Npgsql;
using Sense.Crm.Spikes.M9h.Infra;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Perf;

/// <summary>
/// Q5 — Astları bulma: özyinelemeli CTE (her istekte) vs kiracı grafiğini bellekte tutup BFS/Euler-turu vs kapanış (closure) tablosu vs materyalize yol (path).
/// Şekiller: 250 kullanıcı derinlik 8; 10 000 kullanıcı derinlik 10; 10 000 kullanıcı düz (kökün 9 999 doğrudan astı). Okuma gecikmesi + yeniden bağlama (alt ağaç taşıma) maliyeti.
/// </summary>
[Collection(PgCollection.Name)]
[Trait("Category", "Perf")]
public sealed class Q5_HierarchyTests(PgFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Shape(string Name, Guid Tenant, Guid[] Users, int?[] Parent, int[] Depth);

    private static Shape Generate(string name, int n, int maxDepth, bool flat, Guid tenant)
    {
        var rnd = new Random(42);
        var users = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();
        var parent = new int?[n];
        var depth = new int[n];
        if (flat)
        {
            for (var i = 1; i < n; i++)
            {
                parent[i] = 0;
                depth[i] = 1;
            }

            return new Shape(name, tenant, users, parent, depth);
        }

        // Derinliği garanti et: 0 → 1 → … → maxDepth zinciri; gerisi rastgele (derinliği < maxDepth olan) düğüme bağlanır.
        for (var i = 1; i <= maxDepth && i < n; i++)
        {
            parent[i] = i - 1;
            depth[i] = i;
        }

        for (var i = maxDepth + 1; i < n; i++)
        {
            int p;
            do
            {
                p = rnd.Next(0, i);
            }
            while (depth[p] >= maxDepth);
            parent[i] = p;
            depth[i] = depth[p] + 1;
        }

        return new Shape(name, tenant, users, parent, depth);
    }

    [Fact]
    public async Task SubordinateSets_RecursiveCte_VsInMemoryGraph_VsClosureTable_VsMaterializedPath()
    {
        var cs = await PerfDb.EnsureAsync(pg);
        await pg.ExecAsyncOn(cs, """
            DROP TABLE IF EXISTS public.h0_members, public.h0_closure, public.h0_paths;
            CREATE TABLE public.h0_members(tenant_id uuid NOT NULL, user_id uuid NOT NULL, manager_id uuid NULL, PRIMARY KEY (tenant_id, user_id));
            CREATE INDEX ix_h0_members_mgr ON public.h0_members(tenant_id, manager_id);
            CREATE TABLE public.h0_closure(tenant_id uuid NOT NULL, ancestor_id uuid NOT NULL, descendant_id uuid NOT NULL, depth int NOT NULL, PRIMARY KEY (tenant_id, ancestor_id, descendant_id));
            CREATE INDEX ix_h0_closure_desc ON public.h0_closure(tenant_id, descendant_id);
            CREATE TABLE public.h0_paths(tenant_id uuid NOT NULL, user_id uuid NOT NULL, path text NOT NULL, PRIMARY KEY (tenant_id, user_id));
            CREATE INDEX ix_h0_paths_path ON public.h0_paths(tenant_id, path text_pattern_ops);
            """);

        var shapes = new[]
        {
            Generate("250 users, depth 8", 250, 8, false, Guid.NewGuid()),
            Generate("10k users, depth 10", 10_000, 10, false, Guid.NewGuid()),
            Generate("10k users, flat (9999 direct reports)", 10_000, 1, true, Guid.NewGuid()),
        };

        var read = new StringBuilder();
        read.AppendLine("| shape | who (subtree size) | CTE wall p50 ms | closure wall p50 ms | path LIKE wall p50 ms | in-memory BFS p50 µs (graph cached) |");
        read.AppendLine("|---|---|---|---|---|---|");
        var load = new StringBuilder();
        load.AppendLine("| shape | edge load from DB (ms, cold cache miss) | BFS all users total (ms) | closure rows | closure build (ms) | move-subtree: adjacency+cycle check (ms) | move-subtree: closure rewrite (ms) | move-subtree: path rewrite (ms) | moved subtree size |");
        load.AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var s in shapes)
        {
            await LoadShapeAsync(cs, s);
            var children = BuildChildren(s);
            var sizes = SubtreeSizes(s, children);
            var root = 0;
            var mid = Enumerable.Range(1, s.Users.Length - 1).OrderBy(i => Math.Abs(sizes[i] - Math.Max(2, sizes[0] / 20))).First();
            var leaf = Enumerable.Range(0, s.Users.Length).First(i => sizes[i] == 1);

            foreach (var (who, idx) in new[] { ("root", root), ("mid", mid), ("leaf", leaf) })
            {
                var expected = Bfs(children, idx).Count;
                var cte = await Bench.TimeAsync(async () => (await CteAsync(cs, s, idx)).ShouldBe(expected), warmup: 3, runs: 20);
                var clo = await Bench.TimeAsync(async () => (await ClosureAsync(cs, s, idx)).ShouldBe(expected), warmup: 3, runs: 20);
                var pth = await Bench.TimeAsync(async () => (await PathAsync(cs, s, idx)).ShouldBe(expected), warmup: 3, runs: 20);
                var bfs = BfsMicros(children, idx);
                read.AppendLine($"| {s.Name} | {who} ({expected}) | {cte.P50Ms:F2} | {clo.P50Ms:F2} | {pth.P50Ms:F2} | {bfs:F1} |");
            }

            // Grafiğin bellekte yüklenmesi (önbellek ıskası) ve tüm kullanıcıların alt kümelerinin tek geçişte hesabı.
            var loadT = await Bench.TimeAsync(async () => await LoadEdgesAsync(cs, s.Tenant), warmup: 2, runs: 10);
            var sw = Stopwatch.StartNew();
            long total = 0;
            for (var i = 0; i < s.Users.Length; i++)
            {
                total += Bfs(children, i).Count;
            }

            var bfsAll = sw.Elapsed.TotalMilliseconds;
            var closureRows = await ScalarAsync(cs, $"SELECT count(*) FROM public.h0_closure WHERE tenant_id='{s.Tenant}'");

            // Yeniden bağlama: 'mid' düğümünü kök yerine 'leaf' olmayan başka bir düğümün altına taşı (döngü olmasın: root'un doğrudan altına).
            var newParent = root;
            var adj = await TimeTxAsync(cs, async (c, tx) =>
            {
                // döngü denetimi: yeni yöneticinin ata zincirinde 'mid' var mı (özyinelemeli CTE, derinlik ≤ 10)
                await using var chk = new NpgsqlCommand("""
                    WITH RECURSIVE up AS (SELECT user_id, manager_id, 0 d FROM public.h0_members WHERE tenant_id=@t AND user_id=@np
                                          UNION ALL SELECT m.user_id, m.manager_id, up.d+1 FROM public.h0_members m JOIN up ON m.user_id = up.manager_id WHERE m.tenant_id=@t AND up.d < 12)
                    SELECT count(*) FROM up WHERE user_id=@u
                    """, c, tx);
                chk.Parameters.AddWithValue("t", s.Tenant);
                chk.Parameters.AddWithValue("np", s.Users[newParent]);
                chk.Parameters.AddWithValue("u", s.Users[mid]);
                (await chk.ExecuteScalarAsync(Ct)).ShouldBe(0L);
                await using var upd = new NpgsqlCommand("UPDATE public.h0_members SET manager_id=@np WHERE tenant_id=@t AND user_id=@u", c, tx);
                upd.Parameters.AddWithValue("t", s.Tenant);
                upd.Parameters.AddWithValue("np", s.Users[newParent]);
                upd.Parameters.AddWithValue("u", s.Users[mid]);
                await upd.ExecuteNonQueryAsync(Ct);
            });

            var clo2 = await TimeTxAsync(cs, async (c, tx) =>
            {
                // kapanış tablosu: alt ağacın eski üst-atalarla bağlarını sil, yeni atalarla ekle
                await ExecAsync(c, tx, """
                    DELETE FROM public.h0_closure WHERE tenant_id=@t AND descendant_id IN (SELECT descendant_id FROM public.h0_closure WHERE tenant_id=@t AND ancestor_id=@u)
                      AND ancestor_id IN (SELECT ancestor_id FROM public.h0_closure WHERE tenant_id=@t AND descendant_id=@u AND ancestor_id <> @u);
                    INSERT INTO public.h0_closure(tenant_id, ancestor_id, descendant_id, depth)
                      SELECT @t, sup.ancestor_id, sub.descendant_id, sup.depth + sub.depth + 1
                      FROM public.h0_closure sup CROSS JOIN public.h0_closure sub
                      WHERE sup.tenant_id=@t AND sup.descendant_id=@np AND sub.tenant_id=@t AND sub.ancestor_id=@u;
                    """, s.Tenant, s.Users[newParent], s.Users[mid]);
            });

            var pth2 = await TimeTxAsync(cs, async (c, tx) =>
            {
                await ExecAsync(c, tx, """
                    WITH old AS (SELECT path AS p FROM public.h0_paths WHERE tenant_id=@t AND user_id=@u),
                         np  AS (SELECT path AS p FROM public.h0_paths WHERE tenant_id=@t AND user_id=@np)
                    UPDATE public.h0_paths x SET path = (SELECT p FROM np) || substr(x.path, length((SELECT p FROM old)) - 36)
                    WHERE x.tenant_id=@t AND x.path LIKE (SELECT p FROM old) || '%';
                    """, s.Tenant, s.Users[newParent], s.Users[mid]);
            });

            load.AppendLine($"| {s.Name} | {loadT.P50Ms:F2} | {bfsAll:F1} (sum={total}) | {closureRows} | {closureBuildMs[s.Name]:F0} | {adj:F2} | {clo2:F2} | {pth2:F2} | {sizes[mid]} |");
        }

        Evidence.Write("perf-hierarchy-read.md", read.ToString());
        Evidence.Write("perf-hierarchy-write.md", load.ToString());
    }

    private readonly Dictionary<string, double> closureBuildMs = [];

    private async Task LoadShapeAsync(string cs, Shape s)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        await using (var ins = new NpgsqlCommand("INSERT INTO public.h0_members SELECT @t, u, m FROM unnest(@us, @ms) AS x(u, m)", c))
        {
            ins.Parameters.AddWithValue("t", s.Tenant);
            ins.Parameters.AddWithValue("us", s.Users);
            ins.Parameters.Add(new NpgsqlParameter("ms", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = s.Parent.Select(p => p is null ? (Guid?)null : s.Users[p.Value]).ToArray() });
            await ins.ExecuteNonQueryAsync(Ct);
        }

        // Yol (path): '/<id>/<id>/…/' ; kök '/<id0>/'
        var paths = new string[s.Users.Length];
        for (var i = 0; i < s.Users.Length; i++)
        {
            paths[i] = s.Parent[i] is null ? $"/{s.Users[i]}/" : $"{paths[s.Parent[i]!.Value]}{s.Users[i]}/";
        }

        await using (var ins = new NpgsqlCommand("INSERT INTO public.h0_paths SELECT @t, u, p FROM unnest(@us, @ps) AS x(u, p)", c))
        {
            ins.Parameters.AddWithValue("t", s.Tenant);
            ins.Parameters.AddWithValue("us", s.Users);
            ins.Parameters.AddWithValue("ps", paths);
            await ins.ExecuteNonQueryAsync(Ct);
        }

        // Kapanış (closure): kendisi dahil tüm (ata, torun) çiftleri (önce istatistik: aksi hâlde CTE planı seq-scan'e düşer)
        await using (var pre = new NpgsqlCommand("ANALYZE public.h0_members", c))
        {
            await pre.ExecuteNonQueryAsync(Ct);
        }

        var sw = Stopwatch.StartNew();
        await using (var clo = new NpgsqlCommand("""
            INSERT INTO public.h0_closure(tenant_id, ancestor_id, descendant_id, depth)
            WITH RECURSIVE t AS (
              SELECT user_id AS a, user_id AS d, 0 AS depth FROM public.h0_members WHERE tenant_id=@t
              UNION ALL
              SELECT t.a, m.user_id, t.depth+1 FROM t JOIN public.h0_members m ON m.manager_id = t.d AND m.tenant_id=@t WHERE t.depth < 12)
            SELECT @t, a, d, depth FROM t
            """, c) { CommandTimeout = 300 })
        {
            clo.Parameters.AddWithValue("t", s.Tenant);
            await clo.ExecuteNonQueryAsync(Ct);
        }

        closureBuildMs[s.Name] = sw.Elapsed.TotalMilliseconds;
        await using var an = new NpgsqlCommand("ANALYZE public.h0_members; ANALYZE public.h0_closure; ANALYZE public.h0_paths;", c);
        await an.ExecuteNonQueryAsync(Ct);
    }

    private static List<int>[] BuildChildren(Shape s)
    {
        var ch = Enumerable.Range(0, s.Users.Length).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < s.Users.Length; i++)
        {
            if (s.Parent[i] is { } p)
            {
                ch[p].Add(i);
            }
        }

        return ch;
    }

    private static int[] SubtreeSizes(Shape s, List<int>[] children)
    {
        var sizes = new int[s.Users.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            sizes[i] = Bfs(children, i).Count;
        }

        return sizes;
    }

    private static List<int> Bfs(List<int>[] children, int start)
    {
        var result = new List<int> { start };
        for (var i = 0; i < result.Count; i++)
        {
            result.AddRange(children[result[i]]);
        }

        return result;
    }

    private static double BfsMicros(List<int>[] children, int start)
    {
        for (var i = 0; i < 50; i++)
        {
            Bfs(children, start);
        }

        var t = Stopwatch.GetTimestamp();
        const int runs = 500;
        for (var i = 0; i < runs; i++)
        {
            Bfs(children, start);
        }

        return Stopwatch.GetElapsedTime(t).TotalMilliseconds * 1000 / runs;
    }

    private static async Task<int> CteAsync(string cs, Shape s, int idx)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("""
            WITH RECURSIVE sub AS (SELECT user_id, 0 AS d FROM public.h0_members WHERE tenant_id=@t AND user_id=@u
                                   UNION ALL SELECT m.user_id, sub.d+1 FROM public.h0_members m JOIN sub ON m.manager_id = sub.user_id WHERE m.tenant_id=@t AND sub.d < 12)
            SELECT user_id FROM sub
            """, c);
        cmd.Parameters.AddWithValue("t", s.Tenant);
        cmd.Parameters.AddWithValue("u", s.Users[idx]);
        return await DrainAsync(cmd);
    }

    private static async Task<int> ClosureAsync(string cs, Shape s, int idx)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT descendant_id FROM public.h0_closure WHERE tenant_id=@t AND ancestor_id=@u", c);
        cmd.Parameters.AddWithValue("t", s.Tenant);
        cmd.Parameters.AddWithValue("u", s.Users[idx]);
        return await DrainAsync(cmd);
    }

    private static async Task<int> PathAsync(string cs, Shape s, int idx)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT user_id FROM public.h0_paths WHERE tenant_id=@t AND path LIKE (SELECT path FROM public.h0_paths WHERE tenant_id=@t AND user_id=@u) || '%'", c);
        cmd.Parameters.AddWithValue("t", s.Tenant);
        cmd.Parameters.AddWithValue("u", s.Users[idx]);
        return await DrainAsync(cmd);
    }

    private static async Task<int> LoadEdgesAsync(string cs, Guid tenant)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT user_id, manager_id FROM public.h0_members WHERE tenant_id=@t", c);
        cmd.Parameters.AddWithValue("t", tenant);
        var n = 0;
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        var map = new Dictionary<Guid, List<Guid>>();
        while (await r.ReadAsync(Ct))
        {
            n++;
            if (!r.IsDBNull(1))
            {
                var m = r.GetGuid(1);
                if (!map.TryGetValue(m, out var l))
                {
                    map[m] = l = [];
                }

                l.Add(r.GetGuid(0));
            }
        }

        return n;
    }

    private static async Task<int> DrainAsync(NpgsqlCommand cmd)
    {
        var n = 0;
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct))
        {
            n++;
        }

        return n;
    }

    private static async Task<object?> ScalarAsync(string cs, string sql)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync(Ct);
    }

    private static async Task ExecAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, Guid tenant, Guid newParent, Guid user)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("np", newParent);
        cmd.Parameters.AddWithValue("u", user);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>İşlemi çalıştırır, süreyi ölçer, ROLLBACK eder (veri sabit kalır).</summary>
    private static async Task<double> TimeTxAsync(string cs, Func<NpgsqlConnection, NpgsqlTransaction, Task> action)
    {
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(Ct);
        await using var tx = await c.BeginTransactionAsync(Ct);
        var sw = Stopwatch.StartNew();
        await action(c, tx);
        var ms = sw.Elapsed.TotalMilliseconds;
        await tx.RollbackAsync(Ct);
        return ms;
    }
}
