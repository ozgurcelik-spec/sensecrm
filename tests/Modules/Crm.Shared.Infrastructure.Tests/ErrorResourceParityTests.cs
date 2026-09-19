using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Crm.Shared.Infrastructure.Tests;

/// <summary>
/// TALIMAT madde 3: Türkçe içerik zorunluluğunun mekanik kanıtı.
///
/// İddia: SharedResource.resx (TR) ve SharedResource.en.resx (EN) anahtar kümeleri
/// birebir aynıdır — eksik çeviri = kırmızı, fazlalık = kırmızı.
/// Ek iddia: hiçbir değer boş string değildir.
/// </summary>
public sealed class ErrorResourceParityTests
{
    [Fact]
    public void SharedResource_TR_And_EN_HaveSameKeys()
    {
        var repoRoot = FindRepositoryRoot();
        repoRoot.ShouldNotBeNull("Repository root bulunamadı");

        var trPath = Path.Combine(repoRoot, "src/Shared/Crm.Shared.Infrastructure/Resources/SharedResource.resx");
        var enPath = Path.Combine(repoRoot, "src/Shared/Crm.Shared.Infrastructure/Resources/SharedResource.en.resx");

        File.Exists(trPath).ShouldBeTrue($"SharedResource.resx bulunamadı: {trPath}");
        File.Exists(enPath).ShouldBeTrue($"SharedResource.en.resx bulunamadı: {enPath}");

        var trKeys = ExtractResourceKeys(trPath);
        var enKeys = ExtractResourceKeys(enPath);

        var missingInEN = trKeys.Except(enKeys).ToList();
        var extraInEN = enKeys.Except(trKeys).ToList();

        missingInEN.ShouldBeEmpty($"Şu anahtarlar SharedResource.en.resx'te eksik: {string.Join(", ", missingInEN)}");
        extraInEN.ShouldBeEmpty($"Şu anahtarlar SharedResource.en.resx'te fazla: {string.Join(", ", extraInEN)}");
    }

    [Fact]
    public void SharedResource_NoEmptyValues()
    {
        var repoRoot = FindRepositoryRoot();
        repoRoot.ShouldNotBeNull("Repository root bulunamadı");

        var trPath = Path.Combine(repoRoot, "src/Shared/Crm.Shared.Infrastructure/Resources/SharedResource.resx");
        var enPath = Path.Combine(repoRoot, "src/Shared/Crm.Shared.Infrastructure/Resources/SharedResource.en.resx");

        var trEmpty = ExtractEmptyValues(trPath);
        var enEmpty = ExtractEmptyValues(enPath);

        trEmpty.ShouldBeEmpty($"SharedResource.resx'te boş değerler: {string.Join(", ", trEmpty)}");
        enEmpty.ShouldBeEmpty($"SharedResource.en.resx'te boş değerler: {string.Join(", ", enEmpty)}");
    }

    private static List<string> ExtractResourceKeys(string resxPath)
    {
        try
        {
            var doc = XDocument.Load(resxPath);
            return doc.Descendants("data")
                .Select(d => d.Attribute("name")?.Value)
                .Where(k => k != null)
                .Cast<string>()
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($".resx dosyası okunurken hata: {resxPath}", ex);
        }
    }

    private static List<(string Key, string Value)> ExtractEmptyValues(string resxPath)
    {
        try
        {
            var doc = XDocument.Load(resxPath);
            return doc.Descendants("data")
                .Select(d => new
                {
                    Key = d.Attribute("name")?.Value,
                    Value = d.Element("value")?.Value
                })
                .Where(x => x.Key != null && string.IsNullOrEmpty(x.Value))
                .Select(x => (x.Key!, x.Value ?? ""))
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($".resx dosyası okunurken hata: {resxPath}", ex);
        }
    }

    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
