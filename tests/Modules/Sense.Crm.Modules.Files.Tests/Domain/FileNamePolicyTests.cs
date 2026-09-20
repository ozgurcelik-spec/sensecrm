using Sense.Crm.Modules.Files.Domain;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Files.Tests.Domain;

/// <summary>Dosya adı temizleme (saf): yol geçişi, NUL/kontrol/çift yönlü karakterler, aygıt adları, uzunluk, çift uzantı, başlık enjeksiyonu.</summary>
public sealed class FileNamePolicyTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\a.pdf", "a.pdf")]
    [InlineData("C:\\Users\\x\\rapor.pdf", "rapor.pdf")]
    [InlineData("/var/www/rapor.pdf", "rapor.pdf")]
    [InlineData("a..b.pdf", "a.b.pdf")]
    [InlineData("a....b.pdf", "a.b.pdf")]
    [InlineData("  rapor.pdf  ", "rapor.pdf")]
    [InlineData("rapor.pdf.", "rapor.pdf")]
    [InlineData("rapor.pdf ", "rapor.pdf")]
    [InlineData("...rapor.pdf", "rapor.pdf")]
    public void Sanitize_StripsPathsDotsAndWhitespace(string raw, string expected) =>
        FileNamePolicy.Sanitize(raw).ShouldBe(expected);

    [Theory]
    [InlineData("a.pdf\0.exe", "a.pdf.exe")]
    [InlineData("a\u0001b.pdf", "ab.pdf")]
    [InlineData("a\r\nb.pdf", "ab.pdf")]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    [InlineData("a\u200Bb.pdf", "ab.pdf")]
    [InlineData("a\uFEFFb.pdf", "ab.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.pdf", "abcdefgh.pdf")]
    [InlineData("rapor\"; filename=\"x.exe.pdf", "rapor; filename=x.exe.pdf")]
    public void Sanitize_RemovesControlFormatAndForbiddenCharacters(string raw, string expected) =>
        FileNamePolicy.Sanitize(raw).ShouldBe(expected);

    [Theory]
    [InlineData("CON.pdf", "_CON.pdf")]
    [InlineData("nul", "_nul")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("LPT9.doc", "_LPT9.doc")]
    [InlineData("console.pdf", "console.pdf")]
    public void Sanitize_PrefixesWindowsDeviceNames(string raw, string expected) =>
        FileNamePolicy.Sanitize(raw).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%2e%2e/")]
    [InlineData("../")]
    [InlineData("...")]
    [InlineData("<>:\"|?*")]
    [InlineData("\u202E\u200B")]
    public void Sanitize_ReturnsNullWhenNothingIsLeft(string? raw) => FileNamePolicy.Sanitize(raw).ShouldBeNull();

    [Fact]
    public void Sanitize_OnlyExtension_LeavesNoExtension()
    {
        var name = FileNamePolicy.Sanitize(".pdf");
        name.ShouldBe("pdf");
        FileNamePolicy.GetExtension(name!).ShouldBe(string.Empty);
    }

    [Fact]
    public void Sanitize_PercentEncodedSequencesAreLiteral_NotDecoded() =>
        FileNamePolicy.Sanitize("a%2e%2e%2fb.pdf").ShouldBe("a%2e%2e%2fb.pdf");

    [Fact]
    public void Sanitize_LongName_IsTruncatedTo200_KeepingTheExtension()
    {
        var name = FileNamePolicy.Sanitize(new string('a', 10_000) + ".pdf")!;
        name.Length.ShouldBe(200);
        name.ShouldEndWith(".pdf");
    }

    [Fact]
    public void Sanitize_LongNameWithSurrogatePairs_NeverSplitsAPair()
    {
        var name = FileNamePolicy.Sanitize(string.Concat(Enumerable.Repeat("\U0001F600", 300)) + ".pdf")!;
        name.Length.ShouldBeLessThanOrEqualTo(200);
        name.ShouldEndWith(".pdf");
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]))
            {
                char.IsLowSurrogate(name[i + 1]).ShouldBeTrue();
                i++;
            }
            else
            {
                char.IsLowSurrogate(name[i]).ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void Sanitize_LoneSurrogates_AreDropped() => FileNamePolicy.Sanitize("a\uD800b.pdf").ShouldBe("ab.pdf");

    [Fact]
    public void Sanitize_NormalizesUnicodeToNfc()
    {
        // "e" + birleştirici akut vurgu → tek "é".
        FileNamePolicy.Sanitize("re\u0301sume\u0301.pdf").ShouldBe("r\u00E9sum\u00E9.pdf");
    }

    [Fact]
    public void Sanitize_KeepsTurkishAndOtherLetters() => FileNamePolicy.Sanitize("Sözleşme_ığüşöç İĞÜŞÖÇ.pdf").ShouldBe("Sözleşme_ığüşöç İĞÜŞÖÇ.pdf");

    [Theory]
    [InlineData("rapor.PDF", "pdf")]
    [InlineData("a.b.docx", "docx")]
    [InlineData("noext", "")]
    [InlineData("a.", "")]
    public void GetExtension_IsTheLowercasedLastExtension(string name, string expected) => FileNamePolicy.GetExtension(name).ShouldBe(expected);

    [Theory]
    [InlineData("a.exe.pdf", true)]
    [InlineData("a.EXE.pdf", true)]
    [InlineData("a.html.png", true)]
    [InlineData("a.svg.pdf", true)]
    [InlineData("a.js.txt", true)]
    [InlineData("a.b.c.ps1.docx", true)]
    [InlineData("a.pdf", false)]
    [InlineData("a.pdf.pdf", false)]
    [InlineData("a.tar.txt", false)]
    [InlineData("a.exe", false)]
    public void HasDangerousDoubleExtension_ChecksEveryPartBeforeTheLastExtension(string name, bool expected) =>
        FileNamePolicy.HasDangerousDoubleExtension(name).ShouldBe(expected);

    [Fact]
    public void ToAsciiFallback_ReplacesQuotesBackslashesPercentAndNonAscii()
    {
        var fallback = FileNamePolicy.ToAsciiFallback("a\"b\\c%d\u00E7\r\n.pdf");
        fallback.ShouldBe("a_b_c_d___.pdf");
        fallback.ShouldNotContain("\"");
        fallback.ShouldNotContain("\r");
        fallback.ShouldNotContain("\n");
    }
}
