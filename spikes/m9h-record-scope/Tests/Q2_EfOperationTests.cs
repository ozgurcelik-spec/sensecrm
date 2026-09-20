using Microsoft.EntityFrameworkCore;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Sense.Crm.Spikes.M9h.Model;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Tests;

/// <summary>
/// Q2 (b) — Filtrenin EF işlemleri boyunca davranışı: AsNoTracking, Include, projeksiyon, Count/Any/GroupBy, ExecuteUpdate/ExecuteDelete,
/// FromSql, split query, Find, açık yükleme, alt kayıt (EXISTS) ve çift sahip yolu + sahipsiz havuz. Stil A ve B için aynı sonuç beklenir.
/// </summary>
[Collection(PgCollection.Name)]
public sealed class Q2_EfOperationTests(PgFixture pg) : IAsyncLifetime
{
    private readonly World _w = new();

    public async ValueTask InitializeAsync() => await _w.SeedAsync(pg);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<FilterStyle> Styles => new() { FilterStyle.ContextMember, FilterStyle.StaticWithContextArg };

    private SpikeDbContextBase Create(FilterStyle style, SqlCapture? capture = null) =>
        pg.Create(style, capture);

    private IDisposable AsUser(RecordScopeSnapshot s) => new Both(new TenantContext().BeginScope(_w.T1), RecordScopeContext.Use(s));

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose()
        {
            b.Dispose();
            a.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task AsNoTracking_Tracking_Projection_Count_Any_GroupBy_AreAllScoped(FilterStyle style)
    {
        using var _ = AsUser(World.OwnAndSubs(_w.M1, _w.M1Subordinates)); // M1, R1, R2 → 6 fırsat
        await using var db = Create(style);

        (await db.Deals.AsNoTracking().ToListAsync(Ct)).Count.ShouldBe(6);
        (await db.Deals.ToListAsync(Ct)).Count.ShouldBe(6);
        (await db.Deals.Select(d => new { d.Name, d.Amount }).ToListAsync(Ct)).Count.ShouldBe(6);
        (await db.Deals.CountAsync(Ct)).ShouldBe(6);
        (await db.Deals.LongCountAsync(Ct)).ShouldBe(6);
        (await db.Deals.SumAsync(d => d.Amount, Ct)).ShouldBe(6 * 150m);
        (await db.Deals.AnyAsync(d => d.OwnerUserId == _w.X, Ct)).ShouldBeFalse("görünmeyen sahibin kaydı Any ile bile sezilemez");
        (await db.Deals.CountAsync(d => d.OwnerUserId == _w.X, Ct)).ShouldBe(0);

        var grouped = await db.Deals.GroupBy(d => d.OwnerUserId).Select(g => new { g.Key, Total = g.Sum(x => x.Amount) }).ToListAsync(Ct);
        grouped.Select(g => g.Key).ToHashSet().SetEquals([_w.M1, _w.R1, _w.R2]).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task Include_AppliesScopeToIncludedChildren_AndCollectionCountsAreVisibleOnly(FilterStyle style)
    {
        // R1'in firmasına ek olarak X'in sahip olduğu bir kişi bağlanır (firma R1'in, kişi görünmez).
        Guid accountId;
        using (new TenantContext().BeginScope(_w.T1))
        {
            await using var seed = pg.CreateMember(); // Unset = sınırsız (plan D8: HTTP dışı)
            accountId = (await seed.Accounts.SingleAsync(a => a.OwnerUserId == _w.R1, Ct)).Id;
            seed.Contacts.Add(new SContact { Id = Guid.NewGuid(), TenantId = _w.T1, OwnerUserId = _w.X, AccountId = accountId, Name = "hidden-contact" });
            await seed.SaveChangesAsync(Ct);
        }

        using var _ = AsUser(World.Own(_w.R1));
        await using var db = Create(style);

        var account = await db.Accounts.Include(a => a.Contacts).SingleAsync(Ct);
        account.Contacts.Select(c => c.Name).ShouldBe(["own-contact"], "Include edilen alt kayıtlar da kapsamdan geçer (hidden-contact yok)");

        var counts = await db.Accounts.Select(a => new { a.Id, ContactCount = a.Contacts.Count }).SingleAsync(Ct);
        counts.ContactCount.ShouldBe(1, "sayaçlar yalnız GÖRÜNÜR alt kayıtları sayar (plan: contactCount/dealCount)");

        // Split query: her iki SQL de kapsamı taşır.
        var capture = new SqlCapture();
        await using var db2 = Create(style, capture);
        var split = await db2.Accounts.Include(a => a.Contacts).AsSplitQuery().SingleAsync(Ct);
        split.Contacts.Count.ShouldBe(1);
        capture.Commands.Count.ShouldBe(2);
        capture.Commands.ShouldAllBe(c => c.Sql.Contains("= ANY ("));

        // Açık yükleme (Entry.Collection.Query) da filtreden geçer.
        var acc = await db.Accounts.AsNoTracking().SingleAsync(Ct);
        await using var db3 = Create(style);
        var tracked = await db3.Accounts.SingleAsync(a => a.Id == acc.Id, Ct);
        var loaded = await db3.Entry(tracked).Collection(a => a.Contacts).Query().ToListAsync(Ct);
        loaded.Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task ExecuteUpdate_And_ExecuteDelete_TouchOnlyVisibleRows(FilterStyle style)
    {
        using (var _ = AsUser(World.Own(_w.R1)))
        {
            await using var db = Create(style);
            var updated = await db.Deals.ExecuteUpdateAsync(s => s.SetProperty(d => d.Amount, 999m), Ct);
            updated.ShouldBe(2, "yalnız R1'in 2 silinmemiş fırsatı; başkaları, silinmiş ve T2 dokunulmaz");

            var deleted = await db.Contacts.ExecuteDeleteAsync(Ct);
            deleted.ShouldBe(1);

            (await db.Deals.Where(d => d.OwnerUserId == _w.X).ExecuteUpdateAsync(s => s.SetProperty(d => d.Amount, 1m), Ct)).ShouldBe(0);
        }

        using (new TenantContext().BeginScope(_w.T1))
        {
            await using var check = pg.CreateMember();
            (await check.Deals.CountAsync(d => d.Amount == 999m, Ct)).ShouldBe(2);
            (await check.Deals.CountAsync(d => d.OwnerUserId == _w.X && d.Amount != 1m, Ct)).ShouldBe(2);
            (await check.Contacts.CountAsync(Ct)).ShouldBe(6, "7 kişiden yalnız R1'inki silindi");
        }

        using (new TenantContext().BeginScope(_w.T2))
        {
            await using var check = pg.CreateMember();
            (await check.Deals.CountAsync(d => d.Amount == 999m, Ct)).ShouldBe(0);
        }
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task FromSql_Composes_FiltersApplyOnTopOfRawRoot_AndIgnoreRecordScopeWorks(FilterStyle style)
    {
        using var _ = AsUser(World.Own(_w.R1));
        var capture = new SqlCapture();
        await using var db = Create(style, capture);

        var rows = await db.Deals.FromSql($"SELECT * FROM spike.deals").ToListAsync(Ct);
        rows.Count.ShouldBe(2, "ham SQL kökü de kiracı+silinmiş+kapsam filtresinden geçer (alt sorgu olarak sarmalanır)");
        Evidence.Log($"[Q2 FromSql/{style}] {capture.Last.Sql}");

        var noScope = await db.Deals.FromSql($"SELECT * FROM spike.deals").IgnoreRecordScope().ToListAsync(Ct);
        noScope.Count.ShouldBe(14);

        // Kaçış: Database.SqlQuery<T> ve ExecuteSql filtre uygulamaz → bilinçli ham SQL envanteri (TenantFilterBypassInventoryTests) yakalar.
        var leak = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM spike.deals").ToListAsync(Ct);
        leak.Count.ShouldBeGreaterThan(14, "SqlQuery<T> hiçbir filtreyi uygulamaz (ham SQL envanteri şart)");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task Find_RespectsFilter_ButReturnsTrackedHiddenEntity_IdentityMapLeak(FilterStyle style)
    {
        var hiddenId = _w.DealIds[(_w.T1, _w.X)][0];
        using var _ = AsUser(World.Own(_w.R1));
        await using var db = Create(style);

        (await db.Deals.FindAsync([hiddenId], Ct)).ShouldBeNull("Find de sorgu filtresinden geçer (izlenmiyorsa)");

        // Aynı bağlamda BİLİNÇLİ atlamayla yüklenmiş kayıt izleniyorsa Find kimlik haritasından döndürür (filtre çalışmaz).
        await db.Deals.IgnoreRecordScope().SingleAsync(d => d.Id == hiddenId, Ct);
        (await db.Deals.FindAsync([hiddenId], Ct)).ShouldNotBeNull("belgelenen sızıntı yolu: IgnoreRecordScope ile yüklenen izlenen kayıt Find ile görünür kalır");

        // Sorgu yolu (FirstOrDefault) izlenen kayıtta bile filtreyi uygular.
        (await db.Deals.FirstOrDefaultAsync(d => d.Id == hiddenId, Ct)).ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task ThroughParent_ChildInheritsParentVisibility_ExistsSubquery(FilterStyle style)
    {
        var capture = new SqlCapture();
        using (var _ = AsUser(World.Own(_w.R1)))
        {
            await using var db = Create(style, capture);
            var comments = await db.CaseComments.Select(c => c.Body).ToListAsync(Ct);
            comments.ShouldBe(["c1"], "R1 yalnız kendi (atanan) talebinin yorumunu görür; havuz talebinin yorumu yok");
            Evidence.Log($"[Q2 ThroughParent/{style}] {capture.Last.Sql}");
            capture.Last.Sql.ShouldContain("EXISTS");
            capture.Last.Sql.ShouldContain("ANY (", Case.Sensitive); // iç sorguda üstün RecordScope filtresi de uygulanır (özyinelemeli)
        }

        using (var _ = AsUser(World.OwnAndSubs(_w.R1, [], includeUnowned: true)))
        {
            await using var db = Create(style);
            (await db.CaseComments.Select(c => c.Body).ToListAsync(Ct)).OrderBy(x => x).ShouldBe(["c1", "pool-c"]);
        }
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public async Task Case_AssignedOrCreatedPath_PlusUnownedPool(FilterStyle style)
    {
        async Task<string[]> TitlesAs(RecordScopeSnapshot s)
        {
            using var _ = AsUser(s);
            await using var db = Create(style);
            return (await db.Cases.Select(c => c.Title).ToListAsync(Ct)).OrderBy(t => t).ToArray();
        }

        // X (own): kendine atanmış + oluşturduğu çapraz talep + oluşturduğu havuz talebi
        (await TitlesAs(World.Own(_w.X))).ShouldBe(["assigned", "cross", "pool"]);

        // R3 (own): kendine atanmış + atandığı çapraz talep (havuz yok)
        (await TitlesAs(World.Own(_w.R3))).ShouldBe(["assigned", "cross"]);

        // R1 (own, havuz açık): kendi + sahipsiz havuz talebi ("pool" oluşturan X olsa bile atanmamış → herkese açık)
        (await TitlesAs(World.OwnAndSubs(_w.R1, [], includeUnowned: true))).ShouldBe(["assigned", "pool"]);

        // M2 + R3 (ekip): M2'nin, R3'ün ve çapraz talep
        (await TitlesAs(World.OwnAndSubs(_w.M2, [_w.R3]))).ShouldBe(["assigned", "assigned", "cross"]);
    }
}
