using System.Text;
using Sense.Crm.Modules.Files.Domain;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Files.Tests.Domain;

/// <summary>Tür allow-list'i ve içerik imzası denetçileri: her tür için geçerli örnek + uyuşmazlık, beyan edilen tür danışmanlığı.</summary>
public sealed class FileTypeCatalogTests
{
    private static InspectionOutcome Inspect(string extension, byte[] content)
    {
        FileTypeCatalog.TryGet(extension, out var type).ShouldBeTrue($".{extension} katalogda olmalı");
        using var stream = new MemoryStream(content);
        return FileTypeCatalog.Inspect(type, stream);
    }

    public static TheoryData<string, byte[]> ValidSamples() => new()
    {
        { "pdf", SampleFiles.Pdf() },
        { "jpg", SampleFiles.Jpeg() },
        { "jpeg", SampleFiles.Jpeg() },
        { "png", SampleFiles.Png() },
        { "gif", SampleFiles.Gif() },
        { "webp", SampleFiles.Webp() },
        { "docx", SampleFiles.Docx() },
        { "xlsx", SampleFiles.Xlsx() },
        { "pptx", SampleFiles.Pptx() },
        { "odt", SampleFiles.Odt() },
        { "ods", SampleFiles.Ods() },
        { "doc", SampleFiles.OleDocument() },
        { "xls", SampleFiles.OleDocument() },
        { "ppt", SampleFiles.OleDocument() },
        { "txt", SampleFiles.Text() },
        { "csv", SampleFiles.CsvWithBom() },
        { "csv", SampleFiles.Text("a,b\n1,2\n") },
    };

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public void EveryType_AcceptsAValidSample(string extension, byte[] content) => Inspect(extension, content).ShouldBe(InspectionOutcome.Ok);

    public static TheoryData<string, byte[], string> Mismatches() => new()
    {
        { "pdf", SampleFiles.Exe(), "EXE adlı .pdf" },
        { "png", SampleFiles.Jpeg(), "JPEG içinde .png" },
        { "jpg", SampleFiles.Png(), "PNG içinde .jpg" },
        { "txt", SampleFiles.Html(), "HTML .txt" },
        { "csv", SampleFiles.Html(), "HTML .csv" },
        { "png", SampleFiles.Svg(), "SVG .png" },
        { "docx", SampleFiles.PlainZip(), "[Content_Types].xml yok" },
        { "xlsx", SampleFiles.Docx(), "docx içeriği .xlsx (xl/ yok)" },
        { "docx", SampleFiles.Pdf(), "PDF .docx" },
        { "odt", SampleFiles.Ods(), "ods içeriği .odt" },
        { "odt", SampleFiles.Docx(), "OOXML .odt (mimetype yok)" },
        { "doc", SampleFiles.Docx(), "ZIP .doc" },
        { "gif", SampleFiles.Webp(), "WEBP .gif" },
        { "webp", SampleFiles.Gif(), "GIF .webp" },
        { "txt", SampleFiles.Exe(), "EXE .txt" },
        { "pdf", [], "boş içerik" },
        { "docx", [0x50, 0x4B, 0x03, 0x04], "kesik ZIP" },
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void SignatureMismatches_AreRejected(string extension, byte[] content, string why)
    {
        var outcome = Inspect(extension, content);
        outcome.ShouldNotBe(InspectionOutcome.Ok, why);
    }

    [Fact]
    public void Docx_WithMacroProject_IsRejectedAsForbiddenContent() => Inspect("docx", SampleFiles.Docx(withMacro: true)).ShouldBe(InspectionOutcome.ForbiddenContent);

    [Fact]
    public void Xlsx_WithMacroProject_IsRejectedAsForbiddenContent() => Inspect("xlsx", SampleFiles.Xlsx(withMacro: true)).ShouldBe(InspectionOutcome.ForbiddenContent);

    [Fact]
    public void Docx_WithEmbeddedExecutable_IsRejectedAsForbiddenContent() => Inspect("docx", SampleFiles.Docx(withExecutable: true)).ShouldBe(InspectionOutcome.ForbiddenContent);

    [Fact]
    public void Zip_WithTooManyEntries_IsRejectedWithoutReadingThem()
    {
        // ZipWithEntries iki sabit giriş ([Content_Types].xml, word/document.xml) + verilen sayıda giriş üretir.
        Inspect("docx", SampleFiles.ZipWithEntries(FileTypeCatalog.MaxZipEntries - 2)).ShouldBe(InspectionOutcome.Ok);
        Inspect("docx", SampleFiles.ZipWithEntries(FileTypeCatalog.MaxZipEntries - 1)).ShouldBe(InspectionOutcome.SignatureMismatch);
    }

    [Fact]
    public void Text_WithNulUtf16OrControlCharacters_IsRejected()
    {
        Inspect("txt", Encoding.Unicode.GetBytes("merhaba")).ShouldBe(InspectionOutcome.SignatureMismatch);
        Inspect("txt", [.. "abc"u8, 0x00, .. "def"u8]).ShouldBe(InspectionOutcome.SignatureMismatch);
        Inspect("txt", "abcd"u8.ToArray()).ShouldBe(InspectionOutcome.SignatureMismatch);
        Inspect("txt", [0xFF, 0xFE, 0x41, 0x00]).ShouldBe(InspectionOutcome.SignatureMismatch);
        Inspect("txt", SampleFiles.Text("tab\tve\r\nsatır sonu")).ShouldBe(InspectionOutcome.Ok);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html></html>")]
    [InlineData("  <html><body>x</body></html>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<?xml version='1.0'?><a/>")]
    [InlineData("<iframe src='x'></iframe>")]
    public void Text_StartingWithMarkup_IsRejected(string content) => Inspect("txt", SampleFiles.Text(content)).ShouldBe(InspectionOutcome.SignatureMismatch);

    [Fact]
    public void Text_LessThanSignThatIsNotMarkup_IsAccepted() => Inspect("txt", SampleFiles.Text("<5 birim altı")).ShouldBe(InspectionOutcome.Ok);

    [Fact]
    public void Text_MultibyteCharacterSplitAtTheProbeBoundary_IsStillValidUtf8()
    {
        // 8 KB sınırında bölünen iki baytlı karakter geçersiz sayılmamalı; dosya sınırdan uzun.
        var bytes = new List<byte>();
        while (bytes.Count < FileTypeCatalog.TextProbeBytes - 1)
        {
            bytes.Add((byte)'a');
        }

        bytes.AddRange(Encoding.UTF8.GetBytes("ş"));
        bytes.AddRange(Encoding.UTF8.GetBytes(new string('b', 200)));
        Inspect("txt", [.. bytes]).ShouldBe(InspectionOutcome.Ok);
    }

    [Fact]
    public void PolyglotGif_WithHtmlBody_IsAcceptedAsGif_AndServedWithItsCanonicalType()
    {
        var polyglot = Encoding.ASCII.GetBytes("GIF89a<html><script>alert(1)</script></html>");
        Inspect("gif", polyglot).ShouldBe(InspectionOutcome.Ok);
        FileTypeCatalog.TryGet("gif", out var type).ShouldBeTrue();
        type.ContentType.ShouldBe("image/gif");
    }

    [Fact]
    public void Pdf_MagicWithinTheFirstKilobyte_IsAccepted_ButNotLater()
    {
        var early = new byte[2048];
        Encoding.ASCII.GetBytes("%PDF-1.4").CopyTo(early, 900);
        Inspect("pdf", early).ShouldBe(InspectionOutcome.Ok);

        var late = new byte[4096];
        Encoding.ASCII.GetBytes("%PDF-1.4").CopyTo(late, 2000);
        Inspect("pdf", late).ShouldBe(InspectionOutcome.SignatureMismatch);
    }

    [Fact]
    public void Catalog_HasCanonicalTypesAndOnlyTheDocumentedPreviewableExtensions()
    {
        FileTypeCatalog.PreviewableExtensions.OrderBy(e => e).ShouldBe(["gif", "jpeg", "jpg", "pdf", "png", "webp"]);
        FileTypeCatalog.AllExtensions.ShouldNotContain("svg");
        FileTypeCatalog.AllExtensions.ShouldNotContain("html");
        FileTypeCatalog.AllExtensions.ShouldNotContain("exe");
        FileTypeCatalog.AllExtensions.ShouldNotContain("zip");
        FileTypeCatalog.TryGet("txt", out var txt).ShouldBeTrue();
        txt.ContentType.ShouldBe("text/plain; charset=utf-8");
        txt.Previewable.ShouldBeFalse();
        FileTypeCatalog.TryGet("EXE", out _).ShouldBeFalse();
        FileTypeCatalog.TryGet(null, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("pdf", null, true)]
    [InlineData("pdf", "", true)]
    [InlineData("pdf", "application/octet-stream", true)]
    [InlineData("pdf", "application/pdf", true)]
    [InlineData("pdf", "application/x-unknown-thing", true)]
    [InlineData("pdf", "text/html", false)]
    [InlineData("pdf", "image/svg+xml", false)]
    [InlineData("pdf", "image/png", false)]
    [InlineData("png", "image/jpeg", false)]
    [InlineData("jpg", "image/jpeg", true)]
    [InlineData("csv", "text/csv", true)]
    [InlineData("csv", "application/vnd.ms-excel", true)]
    [InlineData("csv", "text/plain", true)]
    [InlineData("csv", "application/pdf", false)]
    [InlineData("txt", "TEXT/HTML; charset=utf-8", false)]
    [InlineData("txt", "text/plain; charset=utf-8", true)]
    [InlineData("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("docx", "application/msword", false)]
    [InlineData("xls", "application/vnd.ms-excel", true)]
    public void DeclaredContentType_IsAdvisory_ButHtmlSvgAndOtherFamiliesAreMismatches(string extension, string? declared, bool compatible)
    {
        FileTypeCatalog.TryGet(extension, out var type).ShouldBeTrue();
        FileTypeCatalog.IsDeclaredTypeCompatible(type, declared).ShouldBe(compatible);
    }
}
