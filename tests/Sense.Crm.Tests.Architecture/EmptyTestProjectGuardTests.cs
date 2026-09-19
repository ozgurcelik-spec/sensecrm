using System.Reflection;
using Shouldly;
using Xunit;

namespace Sense.Crm.Tests.Architecture;

/// <summary>ADR-0027 nöbetçisi: çözümdeki her test assembly'si en az bir [Fact]/[Theory] içerir.
/// Boş test projeleri sessizce kapıyı kırmasın.</summary>
public sealed class EmptyTestProjectGuardTests
{
    private const string Prefix = "Sense.Crm.";
    private const string TestSuffix = ".Tests";

    [Fact]
    public void AllTestAssembliesHaveAtLeastOneTestMethod()
    {
        var testAssemblies = LoadTestAssemblies();
        var emptyTestAssemblies = new List<string>();

        foreach (var asm in testAssemblies)
        {
            var hasAnyTestMethod = asm.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Any(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: false).Length > 0 ||
                          m.GetCustomAttributes(typeof(TheoryAttribute), inherit: false).Length > 0);

            if (!hasAnyTestMethod)
            {
                emptyTestAssemblies.Add(asm.GetName().Name!);
            }
        }

        emptyTestAssemblies.ShouldBeEmpty(
            "Aşağıdaki test assembly'leri en az bir [Fact] veya [Theory] içermedir: " +
            string.Join(", ", emptyTestAssemblies));
    }

    private static Assembly[] LoadTestAssemblies()
    {
        // Çalışan assembly'nin bin klasöründe Sense.Crm.*.Tests.dll dosyalarını bul
        var dir = AppContext.BaseDirectory;
        var testDlls = Directory.GetFiles(dir, Prefix + "*" + TestSuffix + ".dll", SearchOption.TopDirectoryOnly);
        return testDlls.Select(f => Assembly.LoadFrom(f)).ToArray();
    }
}
