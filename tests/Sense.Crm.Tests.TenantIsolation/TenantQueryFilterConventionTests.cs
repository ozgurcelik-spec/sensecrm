using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Sense.Crm.Shared.Contracts.Context;
using Sense.Crm.Shared.Infrastructure.Context;
using Sense.Crm.Shared.Infrastructure.Persistence;
using Sense.Crm.Shared.Kernel.Domain;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.TenantIsolation;

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

        // M7 Platform şeması: ürünü işleten tarafın küresel tabloları (kiracı filtresi/ITenantEntity YOK: aksi hâlde filtre platform yöneticisinin kiracısına daraltırdı).
        "Plan",
        "TenantAccount",
        "DeletionRequest",
        "UsageSnapshot",
        "PlatformAuditEntry",

        // M8B Integrations: kuresel teknik teslimat kuyrugu (outbox gibi; dispatcher tum kiracilarin isini tek taramayla alir, sonra kiraci kapsamina girer; ITenantEntity DEGIL).
        "DeliveryQueueItem",
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

    /// <summary>
    /// M7 (D5): Platform tabloları küresel olmalıdır — <see cref="ITenantEntity"/> uygulasalardı global filtre platform yöneticisinin kendi kiracısına daraltırdı ve çapraz kiracı
    /// okuma/yazma yanlış kapsamda yapılırdı.
    /// </summary>
    [Fact]
    public void PlatformEntities_AreGlobal_AndDoNotImplementITenantEntity()
    {
        var platformTypes = ProductAssemblies()
            .Where(a => a.GetName().Name == "Sense.Crm.Modules.Platform.Domain")
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name is "Plan" or "TenantAccount" or "DeletionRequest" or "UsageSnapshot" or "PlatformAuditEntry")
            .ToList();

        platformTypes.Count.ShouldBe(5);
        platformTypes.ShouldAllBe(t => !typeof(ITenantEntity).IsAssignableFrom(t));
        platformTypes.ShouldAllBe(t => GlobalEntities.Contains(t.Name));
    }

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
        Directory.GetFiles(AppContext.BaseDirectory, "Sense.Crm.*.dll")
            .Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal))
            .Select(Assembly.LoadFrom)
            .ToArray();
}
