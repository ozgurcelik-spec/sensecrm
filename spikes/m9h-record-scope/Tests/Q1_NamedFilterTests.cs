using Microsoft.EntityFrameworkCore;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Spikes.M9h.Core;
using Sense.Crm.Spikes.M9h.Infra;
using Sense.Crm.Spikes.M9h.Model;
using Shouldly;
using Xunit;

namespace Sense.Crm.Spikes.M9h.Tests;

/// <summary>
/// Q1 — EF Core 10: aynı varlıkta ÜÇ adlı sorgu filtresi (Tenant, SoftDelete, RecordScope) ve <c>IgnoreQueryFilters(["ad"])</c> ile tek tek atlama.
/// Kanıt hedefi gerçek <see cref="ModuleDbContext"/> tabanıdır (repoda "Tenant"/"SoftDelete" zaten adlı filtre).
/// </summary>
[Collection(PgCollection.Name)]
public sealed class Q1_NamedFilterTests(PgFixture pg) : IAsyncLifetime
{
    private readonly World _w = new();

    public async ValueTask InitializeAsync() => await _w.SeedAsync(pg);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Model_HasThreeNamedFilters_OnScopedEntities_AndOnlyTwoOrOneElsewhere()
    {
        using var db = pg.CreateMember();
        var deal = db.Model.FindEntityType(typeof(SDeal))!;
        deal.GetDeclaredQueryFilters().Select(f => f.Key).ShouldBe(["Tenant", "SoftDelete", "RecordScope"], ignoreOrder: true);

        // Kapsamsız katalog: yalnız Tenant; alt kayıt: Tenant + RecordScope (üst üzerinden); soft-delete yok.
        db.Model.FindEntityType(typeof(SProduct))!.GetDeclaredQueryFilters().Select(f => f.Key).ShouldBe(["Tenant"]);
        db.Model.FindEntityType(typeof(SCaseComment))!.GetDeclaredQueryFilters().Select(f => f.Key).ShouldBe(["Tenant", "RecordScope"], ignoreOrder: true);
    }

    [Fact]
    public async Task IgnoreQueryFilters_ByName_DisablesOnlyRecordScope_TenantAndSoftDeleteRemain()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _s = RecordScopeContext.Use(World.Own(_w.R1));
        await using var db = pg.CreateMember();

        // Kapsamlı: yalnız R1'in 2 silinmemiş fırsatı.
        (await db.Deals.Select(d => d.OwnerUserId).Distinct().ToListAsync(TestContext.Current.CancellationToken)).ShouldBe([_w.R1]);
        (await db.Deals.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);

        // Yalnız RecordScope atlanır: T1'in 7 kullanıcı x 2 = 14 silinmemiş fırsatı (silinmiş ve T2 hâlâ yok).
        var noScope = await db.Deals.IgnoreRecordScope().ToListAsync(TestContext.Current.CancellationToken);
        noScope.Count.ShouldBe(14);
        noScope.ShouldAllBe(d => d.TenantId == _w.T1);
        noScope.ShouldNotContain(d => d.IsDeleted);
    }

    [Fact]
    public async Task IgnoreQueryFilters_TenantOnly_KeepsRecordScopeAndSoftDelete()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _s = RecordScopeContext.Use(World.Own(_w.R1));
        await using var db = pg.CreateMember();

        // Kiracı filtresi kapalı: R1 T2'de de var (aynı kimlik) → 2 (T1) + 2 (T2); kapsam ve silinmiş filtreleri kalır.
        var rows = await db.Deals.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).ToListAsync(TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(4);
        rows.ShouldAllBe(d => d.OwnerUserId == _w.R1 && !d.IsDeleted);
    }

    [Fact]
    public async Task IgnoreQueryFilters_Unnamed_DisablesEverything_IncludingRecordScope_DocumentedHazard()
    {
        // Mevcut Workflows deposundaki parametresiz IgnoreQueryFilters() ÇAĞRISI yeni filtreyi de kapatır: envanter testi bunu yakalamalı (plan D10).
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _s = RecordScopeContext.Use(World.Own(_w.R1));
        await using var db = pg.CreateMember();

        var all = await db.Deals.IgnoreQueryFilters().Where(d => d.TenantId == _w.T1 || d.TenantId == _w.T2).ToListAsync(TestContext.Current.CancellationToken);
        all.Count.ShouldBe(2 * 15, "T1+T2, silinmiş dahil, kapsam dışı dahil: 7 kullanıcı x 2 + 1 silinmiş = 15 / kiracı");
    }

    [Fact]
    public async Task IgnoreQueryFilters_UnknownName_IsSilentNoOp_TypoFailsSafe_NeverOpens()
    {
        using var _t = new TenantContext().BeginScope(_w.T1);
        using var _s = RecordScopeContext.Use(World.Own(_w.R1));
        await using var db = pg.CreateMember();

        // ÖLÇÜLDÜ: bilinmeyen ad HATA FIRLATMAZ, hiçbir filtre atlanmaz (yazım hatası güvenli yönde başarısız olur: kapsam SÜRER).
        // Sonuç: bypass çağrıları yalnız sabit (const) adla yazılmalı; "atlamadım" hatası testte görünür, sızıntı olmaz.
        var rows = await db.Deals.IgnoreQueryFilters(["RecordScoop"]).ToListAsync(TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(d => d.OwnerUserId == _w.R1);
    }

    [Fact]
    public async Task ApiSurface_HasQueryFilterNamed_GetDeclaredQueryFilters_AreAvailableOnInstalledEfVersion()
    {
        var version = typeof(DbContext).Assembly.GetName().Version!;
        version.Major.ShouldBeGreaterThanOrEqualTo(10);
        await using var db = pg.CreateMember();
        db.Model.FindEntityType(typeof(SDeal))!.FindDeclaredQueryFilter(RecordScopeModelExtensions.FilterName).ShouldNotBeNull();
    }

    [Fact]
    public void EveryTenantEntity_StillHasTenantFilter_AndScopedEntitiesHaveAnnotation()
    {
        // TenantQueryFilterConventionTests'in yaptığı denetimin aynısı (gerçek repo testi): her ITenantEntity "Tenant" filtresi taşır.
        using var db = pg.CreateMember();
        foreach (var et in db.Model.GetEntityTypes().Where(e => !e.IsOwned() && typeof(Sense.Crm.Shared.Kernel.Domain.ITenantEntity).IsAssignableFrom(e.ClrType)))
        {
            et.GetDeclaredQueryFilters().Select(f => f.Key).ShouldContain(ModuleDbContext.TenantFilter, et.ClrType.Name);
        }

        // Plan mimari testi 1'in spike hâli: sahip özelliği olan her varlık HasRecordScope taşır (kayıtsız aday = kırmızı).
        foreach (var et in db.Model.GetEntityTypes().Where(e => e.ClrType.GetProperties().Any(p => p.Name is "OwnerUserId" or "AssignedUserId")))
        {
            et.FindAnnotation(RecordScopeModelExtensions.AnnotationName).ShouldNotBeNull($"{et.ClrType.Name} sahip özelliği var ama HasRecordScope yok");
        }
    }
}
