using Microsoft.EntityFrameworkCore;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Sense.Crm.Spikes.M9h.Model;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Tests;

/// <summary>
/// Q2 — ÇÜRÜTÜLEN VARSAYIM: "filtre ifadesi DbContext'e hiç dokunmayan STATİK çağrılarla (<c>RecordScopeContext.ReadOwners("deal")</c>) yazılabilir".
/// Ölçüm: EF Core 10, filtre ifadesindeki statik çağrıyı sorgu DERLENİRKEN bir kez değerlendirip SONUCU (sahip kümesini) SQL'e SABİT olarak gömer ve
/// derlenmiş sorgu önbelleğinde saklar → İLK kullanıcının kapsamı sonraki kullanıcılara UYGULANIR (önbellek zehirlenmesi, kullanıcılar arası sızıntı).
/// Yalnız DbContext ÖRNEK üyesi üzerinden erişim (mevcut <c>CurrentTenantId</c> kalıbı) sorgu parametresi olur (Q2_ParameterisationTests).
/// </summary>
[Collection(PgCollection.Name)]
public sealed class Q2_StaticStyleDisprovedTests(PgFixture pg) : IAsyncLifetime
{
    private readonly World _w = new();

    public async ValueTask InitializeAsync()
    {
        await _w.SeedAsync(pg);
        using var _ = new TenantContext().BeginScope(_w.T1);
        await using var db = pg.CreateStatic();
        db.Deals.Add(new SDeal { Id = Guid.NewGuid(), TenantId = _w.T1, OwnerUserId = _w.R1, Name = "poison-probe-r1", CreatedAt = DateTime.UtcNow });
        db.Deals.Add(new SDeal { Id = Guid.NewGuid(), TenantId = _w.T1, OwnerUserId = _w.X, Name = "poison-probe-x", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IQueryable<string> Probe(DbContext db, string tag) =>
        db.Set<SDeal>().Where(d => d.Name.StartsWith("poison-probe")).Select(d => d.Name + tag);

    [Fact]
    public async Task StaticCallFilter_BakesFirstUsersOwnerSetIntoCachedSql_ProofOfCrossUserLeak()
    {
        var capture = new SqlCapture();
        using var _t = new TenantContext().BeginScope(_w.T1);

        List<string> r1;
        using (RecordScopeContext.Use(World.Own(_w.R1)))
        {
            await using var db = pg.CreateStatic(capture);
            r1 = await Probe(db, "").ToListAsync(Ct);
        }

        List<string> x;
        using (RecordScopeContext.Use(World.Own(_w.X)))
        {
            await using var db = pg.CreateStatic(capture);
            x = await Probe(db, "").ToListAsync(Ct);
        }

        Evidence.Log($"[Q2 static-style SQL#1] {capture.Commands[0].Sql}");
        Evidence.Log($"[Q2 static-style SQL#2] {capture.Commands[1].Sql}");

        r1.ShouldBe(["poison-probe-r1"]);

        // Beklenen (doğru) davranış x == ["poison-probe-x"] olurdu. ÖLÇÜLEN: aynı SQL yeniden kullanıldı → X, R1'in kayıtlarını görür / kendininkini göremez.
        capture.Commands[0].Sql.ShouldBe(capture.Commands[1].Sql);
        capture.Commands[0].Sql.ShouldContain(_w.R1.ToString(), Case.Insensitive, "R1'in kimliği SQL METNİNE sabit olarak gömülmüş");
        x.ShouldBe(["poison-probe-r1"], "KULLANICILAR ARASI SIZINTI: X, R1'in önbelleğe alınmış kapsamıyla sorgulandı");
    }

    [Fact(Skip = "ÇÜRÜTÜLDÜ (Q2): statik çağrılı filtre kullanıcıya göre değişmez — bkz. StaticCallFilter_BakesFirstUsersOwnerSetIntoCachedSql_ProofOfCrossUserLeak. Gerçek kartta DbContext ÖRNEK üyesi (ModuleDbContext.CurrentRecordScope) kullanılmalı.")]
    public async Task StaticCallFilter_ShouldIsolateTwoUsers_DesiredBehaviour()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using (RecordScopeContext.Use(World.Own(_w.R1)))
        {
            await using var db = pg.CreateStatic();
            (await Probe(db, "#a").ToListAsync(Ct)).ShouldBe(["poison-probe-r1#a"]);
        }

        using (RecordScopeContext.Use(World.Own(_w.X)))
        {
            await using var db = pg.CreateStatic();
            (await Probe(db, "#a").ToListAsync(Ct)).ShouldBe(["poison-probe-x#a"]);
        }
    }

    [Fact]
    public async Task ContextMemberFilter_SameProbe_IsolatesTwoUsers()
    {
        var capture = new SqlCapture();
        using var _t = new TenantContext().BeginScope(_w.T1);
        using (RecordScopeContext.Use(World.Own(_w.R1)))
        {
            await using var db = pg.CreateMember(capture);
            (await Probe(db, "").ToListAsync(Ct)).ShouldBe(["poison-probe-r1"]);
        }

        using (RecordScopeContext.Use(World.Own(_w.X)))
        {
            await using var db = pg.CreateMember(capture);
            (await Probe(db, "").ToListAsync(Ct)).ShouldBe(["poison-probe-x"]);
        }

        capture.Commands[0].Sql.ShouldBe(capture.Commands[1].Sql);
        capture.Commands[0].Sql.ShouldNotContain(_w.R1.ToString(), Case.Insensitive);
        Evidence.Log($"[Q2 member-style SQL] {capture.Commands[0].Sql}");
    }
}
