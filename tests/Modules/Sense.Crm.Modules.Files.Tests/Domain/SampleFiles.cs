using System.IO.Compression;
using System.Text;

namespace Sense.Crm.Modules.Files.Tests.Domain;

/// <summary>Testler için küçük ama <b>imzası geçerli</b> örnek dosya baytları (içerik imzası denetçilerini sınamak için).</summary>
internal static class SampleFiles
{
    public static byte[] Pdf(int size = 1024)
    {
        var bytes = new byte[Math.Max(size, 16)];
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(bytes, 0);
        for (var i = 9; i < bytes.Length; i++)
        {
            bytes[i] = (byte)('a' + (i % 26));
        }

        return bytes;
    }

    public static byte[] Jpeg(int size = 256) => Padded([0xFF, 0xD8, 0xFF, 0xE0], size);

    public static byte[] Png(int size = 256) => Padded([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], size);

    public static byte[] Gif(int size = 128) => Padded("GIF89a"u8.ToArray(), size);

    public static byte[] Webp(int size = 128)
    {
        var bytes = new byte[Math.Max(size, 16)];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    public static byte[] Exe(int size = 256) => Padded("MZ"u8.ToArray(), size);

    public static byte[] Html() => Encoding.UTF8.GetBytes("<html><body><script>alert(1)</script></body></html>");

    public static byte[] Svg() => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

    public static byte[] Text(string content = "Merhaba dünya\r\nİkinci satır\n") => Encoding.UTF8.GetBytes(content);

    public static byte[] CsvWithBom(string content = "ad;soyad\nAli;Yılmaz\n") => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(content)];

    public static byte[] OleDocument(int size = 512) => Padded([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], size);

    public static byte[] Docx(bool withMacro = false, bool withExecutable = false) =>
        Zip([("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w:document/>"), .. Extra(withMacro, withExecutable, "word/vbaProject.bin")]);

    public static byte[] Xlsx(bool withMacro = false) =>
        Zip([("[Content_Types].xml", "<Types/>"), ("xl/workbook.xml", "<workbook/>"), .. Extra(withMacro, false, "xl/vbaProject.bin")]);

    public static byte[] Pptx() => Zip([("[Content_Types].xml", "<Types/>"), ("ppt/presentation.xml", "<p:presentation/>")]);

    /// <summary>[Content_Types].xml olmayan ZIP (uzantısı .docx olsa da geçerli OOXML değildir).</summary>
    public static byte[] PlainZip() => Zip([("readme.txt", "merhaba")]);

    public static byte[] Odt() => OpenDocument("application/vnd.oasis.opendocument.text");

    public static byte[] Ods() => OpenDocument("application/vnd.oasis.opendocument.spreadsheet");

    /// <summary>İlk giriş <c>mimetype</c> (sıkıştırmasız) + bir içerik girişi.</summary>
    public static byte[] OpenDocument(string mimeType)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mime = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mime.Open(), new UTF8Encoding(false)))
            {
                writer.Write(mimeType);
            }

            var content = archive.CreateEntry("content.xml", CompressionLevel.Optimal);
            using var contentWriter = new StreamWriter(content.Open(), new UTF8Encoding(false));
            contentWriter.Write("<office:document-content/>");
        }

        return stream.ToArray();
    }

    /// <summary>Verilen sayıda boş giriş içeren ZIP (giriş sayısı sınırı testi).</summary>
    public static byte[] ZipWithEntries(int count)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("[Content_Types].xml");
            archive.CreateEntry("word/document.xml");
            for (var i = 0; i < count; i++)
            {
                archive.CreateEntry($"word/media/{i}.bin", CompressionLevel.NoCompression);
            }
        }

        return stream.ToArray();
    }

    public static byte[] Zip(IEnumerable<(string Name, string Content)> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Verilen imzayı taşıyan ve <paramref name="size"/> bayta tamamlanan içerik (deterministik dolgu).</summary>
    public static byte[] Padded(byte[] signature, int size)
    {
        var bytes = new byte[Math.Max(size, signature.Length + 8)];
        signature.CopyTo(bytes, 0);
        for (var i = signature.Length; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) % 251);
        }

        return bytes;
    }

    private static IEnumerable<(string, string)> Extra(bool withMacro, bool withExecutable, string macroPath)
    {
        if (withMacro)
        {
            yield return (macroPath, "macro");
        }

        if (withExecutable)
        {
            yield return ("word/embeddings/payload.exe", "MZ");
        }
    }
}
