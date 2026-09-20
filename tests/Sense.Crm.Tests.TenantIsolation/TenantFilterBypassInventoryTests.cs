using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.TenantIsolation;

/// <summary>
/// M7 (D6) — filtre atlama envanteri (docs/plan/m7-saas-hazirlik.md "IgnoreQueryFilters envanteri"): <c>src/**/*.cs</c> içindeki
/// <c>.IgnoreQueryFilters(</c> çağrıları (yorum satırları hariç) dosya başına sayılır ve aşağıdaki tabloyla <b>birebir</b> eşleşmelidir. Yeni bir kullanım
/// (ya da listeden kalkmış eski bir kayıt) bu testi kırar; ekleme = bu tablonun/belgenin bilinçli güncellenmesi ve güvenlik incelemesidir. İkinci envanter: ham SQL
/// çağrıları (<c>FromSql*</c>, <c>ExecuteSql*</c>, <c>SqlQuery*</c>) — kiracı kimliği her zaman <b>parametredir</b>, kullanıcı girdisi SQL'e birleştirilmez.
/// Bu test, eski (fiilen boş bir sezgisel olan) <c>ApplicationLayer_DoesNotBypassTenantFilters</c> testinin yerini alır.
/// </summary>
public sealed class TenantFilterBypassInventoryTests
{
    private const string Ignore = @"\.IgnoreQueryFilters\(";
    private const string RawSql = @"\b(?:FromSql\w*|ExecuteSql\w*|SqlQuery\w*)\s*[(<]";

    /// <summary>Kiracı filtresi bilinçli atlanan yerler (D6 tablosu). M7 bu listeye ekleme yapmaz.</summary>
    private static readonly Dictionary<string, int> ExpectedIgnoreQueryFilters = new(StringComparer.Ordinal)
    {
        // Yalnız çağıranın KENDİ üyelikleri/davetleri (organizasyon listesi, davet listesi).
        ["src/Modules/Identity/Sense.Crm.Modules.Identity.Infrastructure/Persistence/IdentityReadStore.cs"] = 3,

        // Giriş/organizasyon listesi/davet kabulü; her zaman kullanıcı (+ davet kimliği + bekliyor) ile daraltılır.
        ["src/Modules/Identity/Sense.Crm.Modules.Identity.Infrastructure/Persistence/Repositories.cs"] = 3,

        // Durum senkronu için dar projeksiyon (TenantId, ExecutionId); her yürütme sonra kendi kiracı kapsamında işlenir.
        ["src/Modules/Workflows/Sense.Crm.Modules.Workflows.Infrastructure/Persistence/Repositories.cs"] = 1,
    };

    /// <summary>
    /// Ham SQL çağrıları (kiracı-kör olabilir; hepsi bilinçli). Advisory kilitler (Marketing/Sales/Service tohumlayıcı ve üyelik kilitleri, <c>ModuleDbContext.AcquireAdvisoryLockAsync</c>),
    /// <c>OutboxProcessor</c>, M7 ile gelen üç imha bileşeni (<c>TenantDataEraser&lt;TContext&gt;</c>, Identity hesap/kiracı imhası, <c>AuditTenantDataEraser</c>) ve Platform'un
    /// küresel tablolarına yazan iki upsert (tembel hesap, anlık görüntü). Kiracı kimliği her zaman parametredir.
    /// </summary>
    private static readonly Dictionary<string, int> ExpectedRawSql = new(StringComparer.Ordinal)
    {
        // M8C: Files temizlik işi (kiracı listesi: yalnız kimlik döner, tek parametreli SqlQuery; yeni IgnoreQueryFilters yok). Plan +3 öngörmüştü; uzlaştırma kiracı listesini
        // ITenantDirectory'den alır ve nesne imhası TenantDataEraser<FilesDbContext>'i (mevcut girdi) kullanır, bu yüzden yalnız +1 gerekti.
        ["src/Modules/Files/Sense.Crm.Modules.Files.Infrastructure/Jobs/FilesJobs.cs"] = 1,
        ["src/Modules/Identity/Sense.Crm.Modules.Identity.Infrastructure/PlatformSupport.cs"] = 6,
        ["src/Modules/Marketing/Sense.Crm.Modules.Marketing.Infrastructure/Persistence/Repositories.cs"] = 1,
        ["src/Modules/Platform/Sense.Crm.Modules.Platform.Infrastructure/Entitlements/EntitlementServices.cs"] = 2,
        ["src/Modules/Platform/Sense.Crm.Modules.Platform.Infrastructure/Jobs/PlatformJobs.cs"] = 1,
        ["src/Modules/Sales/Sense.Crm.Modules.Sales.Infrastructure/Provisioning/DefaultPipelineSeeder.cs"] = 1,
        ["src/Modules/Service/Sense.Crm.Modules.Service.Infrastructure/Provisioning/DefaultSlaPolicySeeder.cs"] = 1,
        ["src/Shared/Sense.Crm.Shared.Infrastructure/Persistence/ModuleDbContext.cs"] = 1,
        ["src/Shared/Sense.Crm.Shared.Infrastructure/Persistence/Outbox/OutboxProcessor.cs"] = 1,
        ["src/Shared/Sense.Crm.Shared.Infrastructure/Persistence/Retention/TenantDataEraser.cs"] = 1,
    };

    [Fact]
    public void IgnoreQueryFilters_UsageMatchesTheApprovedInventoryExactly()
    {
        var actual = Scan(Ignore);
        Report(actual, ExpectedIgnoreQueryFilters, "IgnoreQueryFilters").ShouldBeEmpty(
            "Kiracı filtresi atlama envanteri değişti. Ekleme bilinçli bir karardır: bu testi ve docs/plan/m7-saas-hazirlik.md envanterini güncelleyin ve güvenlik incelemesi isteyin.");
    }

    [Fact]
    public void RawSql_UsageMatchesTheApprovedInventoryExactly()
    {
        var actual = Scan(RawSql);
        Report(actual, ExpectedRawSql, "ham SQL").ShouldBeEmpty(
            "Ham SQL envanteri değişti. Kiracı kimliği her zaman parametre olmalı; ekleme bilinçli bir karardır: bu testi güncelleyin ve inceleme isteyin.");
    }

    [Fact]
    public void InventoryScan_FindsTheExpectedNumberOfFiles_SoTheTestCannotPassVacuously()
    {
        Scan(Ignore).Count.ShouldBe(ExpectedIgnoreQueryFilters.Count);
        Scan(RawSql).Count.ShouldBe(ExpectedRawSql.Count);
    }

    private static List<string> Report(Dictionary<string, int> actual, Dictionary<string, int> expected, string what)
    {
        var problems = new List<string>();
        foreach (var (file, count) in actual)
        {
            if (!expected.TryGetValue(file, out var allowed))
            {
                problems.Add($"{what}: onaylı listede olmayan dosya {file} ({count} çağrı)");
            }
            else if (allowed != count)
            {
                problems.Add($"{what}: {file} beklenen {allowed}, bulunan {count}");
            }
        }

        problems.AddRange(expected.Keys.Where(f => !actual.ContainsKey(f)).Select(f => $"{what}: listede olan ama artık bulunmayan dosya {f}"));
        return problems;
    }

    /// <summary>Çözüm kökündeki <c>src/**/*.cs</c> (obj/bin/Migrations hariç, yorum satırları hariç) içinde desen eşleşmelerini dosya başına sayar.</summary>
    private static Dictionary<string, int> Scan(string pattern)
    {
        var root = FindSolutionRoot();
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            var count = 0;
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                {
                    continue;
                }

                count += regex.Count(line);
            }

            if (count > 0)
            {
                result[relative] = count;
            }
        }

        return result;
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sense.Crm.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Sense.Crm.slnx bulunamadı (çözüm kökü).");
    }
}
