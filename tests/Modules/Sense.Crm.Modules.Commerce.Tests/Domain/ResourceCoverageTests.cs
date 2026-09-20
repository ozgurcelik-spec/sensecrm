using System.Reflection;
using System.Xml.Linq;
using Sense.Crm.Modules.Commerce.Contracts;
using Sense.Crm.Modules.Commerce.Domain;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Commerce.Tests.Domain;

/// <summary>
/// Her Commerce hata kodu / doğrulama anahtarı / izin adı için tr ve en kaynak metni vardır (eksik anahtar = kullanıcıya ham kod gösterilir).
/// </summary>
public sealed class ResourceCoverageTests
{
    private static readonly string[] ResourceFiles = ["SharedResource.resx", "SharedResource.en.resx"];

    private static HashSet<string> Keys(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        var path = Path.Combine(dir.FullName, "src/Shared/Sense.Crm.Shared.Infrastructure/Resources", file);
        return XDocument.Load(path).Descendants("data").Select(d => d.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("SharedResource.resx")]
    [InlineData("SharedResource.en.resx")]
    public void EveryCommerceErrorCodeAndValidationKey_HasALocalizedMessage(string file)
    {
        var keys = Keys(file);
        var codes = typeof(CommerceErrors).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        codes.ShouldNotBeEmpty();
        codes.Where(c => !keys.Contains(c)).ShouldBeEmpty($"{file}: eksik Commerce hata/doğrulama anahtarları");
    }

    [Theory]
    [InlineData("SharedResource.resx")]
    [InlineData("SharedResource.en.resx")]
    public void EveryCommercePermission_HasALocalizedName(string file)
    {
        var keys = Keys(file);

        CommercePermissions.All.Select(p => $"permission.{p.Key}").Where(k => !keys.Contains(k)).ShouldBeEmpty($"{file}: eksik izin adları");
        CommercePermissions.All.Count.ShouldBeGreaterThanOrEqualTo(14);
        CommercePermissions.All.Select(p => p.Key).ShouldContain("crm.purchaseorders.write");
    }

    [Fact]
    public void ErrorCodes_AreUnique()
    {
        var codes = typeof(CommerceErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        codes.Distinct().Count().ShouldBe(codes.Count);
        ResourceFiles.Length.ShouldBe(2);
    }
}
