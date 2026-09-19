using System.Reflection;
using Crm.Shared.Contracts.Context;
using Crm.Shared.Infrastructure.Context;
using Crm.Shared.Infrastructure.Persistence;
using Crm.Shared.Kernel.Domain;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Crm.Tests.TenantIsolation;

/// <summary>
/// K2 (satır bazlı kiracı ayrımı) için veritabanısız, EF modeli üzerinden kesin denetim: her modül DbContext'inde
/// <see cref="ITenantEntity"/> uygulayan her varlığın "Tenant" adlı global query filter'ı ve TenantId indeksi vardır;
/// kiracıya ait olmayan tablolar yalnız bilinçli olarak listelenenlerdir (yeni küresel tablo = bilinçli karar).
/// </summary>
public sealed class TenantQueryFilterConventionTests
{
    /// <summary>Kiracı filtresi taşımayan, bilinçli olarak küresel tablolar (K1: hesaplar küresel, üyelik kiracıya ait).</summary>
    private static readonly HashSet<string> GlobalEntities = new(StringComparer.Ordinal)
    {
        "Tenant",           // organizasyonun kendisi
        "User",             // küresel kullanıcı hesabı (organizasyonlara Membership ile bağlanır)
        "RefreshToken",     // yalnız hash ile bulunur; organizasyon bağlamı kolon olarak tutulur
        "OutboxMessage",    // altyapı: TenantId kolon olarak taşınır, işleyici kiracı kapsamını kendisi kurar
        "InboxMessage",
    };

    public static IEnumerable<object[]> ModuleContexts() => ProductAssemblies()
        .SelectMany(a => a.GetTypes())
        .Where(t => !t.IsAbstract && typeof(ModuleDbContext).IsAssignableFrom(t))
        .Select(t => new object[] { t.FullName! });

    [Theory]
    [MemberData(nameof(ModuleContexts))]
    public void EveryTenantEntity_HasTenantQueryFilter_AndTenantIndex(string contextTypeName)
    {
        using var context = CreateContext(contextTypeName);

        foreach (var entityType in context.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            var clr = entityType.ClrType;
            if (typeof(ITenantEntity).IsAssignableFrom(clr))
            {
                entityType.GetDeclaredQueryFilters().Select(f => f.Key).ShouldContain(ModuleDbContext.TenantFilter, $"{clr.Name} kiracı filtresi taşımıyor");
                entityType.GetIndexes().Any(i => i.Properties[0].Name == nameof(ITenantEntity.TenantId))
                    .ShouldBeTrue($"{clr.Name} için TenantId ile başlayan indeks yok (K3)");
            }
            else
            {
                GlobalEntities.ShouldContain(clr.Name, $"{clr.Name} kiracıya ait değil; bilinçli küresel tablo ise listeye ekleyin, değilse ITenantEntity uygulayın");
            }
        }
    }

    [Fact]
    public void ModuleContexts_AreDiscovered() => ModuleContexts().ShouldNotBeEmpty();

    private static ModuleDbContext CreateContext(string contextTypeName)
    {
        var type = ProductAssemblies().SelectMany(a => a.GetTypes()).Single(t => t.FullName == contextTypeName);
        var method = typeof(TenantQueryFilterConventionTests).GetMethod(nameof(Create), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
        return (ModuleDbContext)method.Invoke(null, null)!;
    }

    private static TContext Create<TContext>()
        where TContext : ModuleDbContext
    {
        // Model üretimi bağlantı açmaz; bağlantı dizesi yalnız sağlayıcıyı seçmek içindir.
        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql("Host=localhost").UseSnakeCaseNamingConvention().Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options, (ITenantContext)new TenantContext())!;
    }

    internal static Assembly[] ProductAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Crm.*.dll")
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToArray();
}
