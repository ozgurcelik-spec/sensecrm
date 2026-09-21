using System.IO.Compression;
using System.Text;

namespace Sense.Crm.Modules.Files.Domain;

/// <summary>Kataloğun bir türü: uzantı → kanonik tür + aile + önizlenebilirlik.</summary>
/// <param name="Extension">Küçük harf, noktasız.</param>
/// <param name="ContentType">Kanonik sunum türü (depoda daima <c>application/octet-stream</c>; sunulan tür <b>bunudur</b>).</param>
/// <param name="Family">İçerik imzası ailesi (aynı aile uzantıları birbirinin yerine geçer: <c>jpg</c>/<c>jpeg</c>).</param>
/// <param name="Previewable">Satır içi önizleme (yalnız png, jpeg, gif, webp, pdf).</param>
/// <param name="AlsoAcceptedDeclared">İstemci beyanı olarak zararsız sayılan ek türler (ör. Windows'ta <c>.csv</c> için <c>application/vnd.ms-excel</c>).</param>
public sealed record FileType(string Extension, string ContentType, string Family, bool Previewable, IReadOnlyList<string> AlsoAcceptedDeclared);

/// <summary>İçerik imzası denetimi sonucu.</summary>
public enum InspectionOutcome
{
    Ok,

    /// <summary>İmza uzantının ailesiyle uyuşmuyor (<c>file.content_mismatch</c>).</summary>
    SignatureMismatch,

    /// <summary>Makro/yürütülebilir taşıyan paket (<c>file.content_mismatch</c>).</summary>
    ForbiddenContent,
}

/// <summary>
/// İzinli dosya türleri ve içerik imzası denetçileri (kodda; yapılandırma yalnız <b>daraltabilir</b>). Her tür bir imza denetçisi ister — bu yüzden
/// yapılandırma yeni tür ekleyemez. ZIP incelemesi yalnız <b>merkezi dizini</b> okur (giriş adı listesi; içerik açılmaz, giriş sayısı ≤ 20 000).
/// </summary>
public static class FileTypeCatalog
{
    public const int MaxZipEntries = 20_000;
    public const int TextProbeBytes = 8 * 1024;
    private const int PdfProbeBytes = 1024;
    private const int ZipTailProbeBytes = 65_557 + 1024;

    private const string Pdf = "pdf";
    private const string Jpeg = "jpeg";
    private const string Png = "png";
    private const string Gif = "gif";
    private const string Webp = "webp";
    private const string Docx = "docx";
    private const string Xlsx = "xlsx";
    private const string Pptx = "pptx";
    private const string Odt = "odt";
    private const string Ods = "ods";
    private const string OleDoc = "ole-doc";
    private const string OleXls = "ole-xls";
    private const string OlePpt = "ole-ppt";
    private const string Text = "text";
    private const string Csv = "csv";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>Tarayıcı beyanı olarak <b>tehlikeli</b> sayılan türler: beyan edilirse (uzantı ne olursa olsun) <c>file.content_mismatch</c>.</summary>
    private static readonly HashSet<string> DangerousDeclared = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xhtml+xml", "image/svg+xml", "text/javascript", "application/javascript", "application/x-javascript",
        "application/x-msdownload", "application/x-msdos-program", "application/x-dosexec", "application/x-executable", "application/x-sh",
        "application/x-httpd-php", "application/hta", "text/xml", "application/xml",
    };

    private static readonly FileType[] Types =
    [
        new("pdf", "application/pdf", Pdf, true, []),
        new("jpg", "image/jpeg", Jpeg, true, []),
        new("jpeg", "image/jpeg", Jpeg, true, []),
        new("png", "image/png", Png, true, []),
        new("gif", "image/gif", Gif, true, []),
        new("webp", "image/webp", Webp, true, []),
        new("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Docx, false, []),
        new("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Xlsx, false, []),
        new("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", Pptx, false, []),
        new("odt", "application/vnd.oasis.opendocument.text", Odt, false, []),
        new("ods", "application/vnd.oasis.opendocument.spreadsheet", Ods, false, []),
        new("doc", "application/msword", OleDoc, false, []),
        new("xls", "application/vnd.ms-excel", OleXls, false, []),
        new("ppt", "application/vnd.ms-powerpoint", OlePpt, false, []),
        new("txt", "text/plain; charset=utf-8", Text, false, []),
        new("csv", "text/csv; charset=utf-8", Csv, false, ["text/plain", "application/csv", "application/vnd.ms-excel"]),
    ];

    private static readonly Dictionary<string, FileType> ByExtension = Types.ToDictionary(t => t.Extension, StringComparer.Ordinal);

    /// <summary>Kataloğun tamamı (varsayılan izinli küme).</summary>
    public static IReadOnlyList<FileType> All => Types;

    public static IReadOnlyList<string> AllExtensions { get; } = Types.Select(t => t.Extension).ToArray();

    public static IReadOnlyList<string> PreviewableExtensions { get; } = Types.Where(t => t.Previewable).Select(t => t.Extension).ToArray();

    public static bool TryGet(string? extension, out FileType type)
    {
        if (extension is not null && ByExtension.TryGetValue(extension, out var found))
        {
            type = found;
            return true;
        }

        type = null!;
        return false;
    }

    /// <summary>
    /// İstemcinin <c>Content-Type</c> beyanı <b>yalnız danışmandır</b>: boş/bilinmeyen/<c>application/octet-stream</c> yok sayılır; tehlikeli türler
    /// (<c>text/html</c>, <c>image/svg+xml</c> …) veya katalogda <b>başka</b> bir aileye eşlenen türler uyuşmazlıktır.
    /// </summary>
    public static bool IsDeclaredTypeCompatible(FileType type, string? declared)
    {
        ArgumentNullException.ThrowIfNull(type);
        var media = NormalizeMediaType(declared);
        if (media.Length == 0 || media == "application/octet-stream" || media == "binary/octet-stream")
        {
            return true;
        }

        if (DangerousDeclared.Contains(media))
        {
            return false;
        }

        if (string.Equals(NormalizeMediaType(type.ContentType), media, StringComparison.Ordinal) || type.AlsoAcceptedDeclared.Contains(media, StringComparer.Ordinal))
        {
            return true;
        }

        // Katalogda başka bir aileye eşleniyorsa uyuşmazlık; hiçbir aileye eşlenmeyen (bilinmeyen) beyan yok sayılır.
        return !Types.Any(t => t.Family != type.Family && string.Equals(NormalizeMediaType(t.ContentType), media, StringComparison.Ordinal));
    }

    /// <summary>
    /// İçerik imzası denetimi. <paramref name="content"/> <b>aranabilir</b> olmalı (geçici dosya); başlangıç konumu değiştirilir, çağıran sonra başa sarar.
    /// </summary>
    public static InspectionOutcome Inspect(FileType type, Stream content)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanSeek)
        {
            throw new ArgumentException("Content stream must be seekable.", nameof(content));
        }

        try
        {
            return type.Family switch
            {
                Pdf => Contains(content, "%PDF-"u8, PdfProbeBytes) ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                Jpeg => StartsWith(content, [0xFF, 0xD8, 0xFF]) ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                Png => StartsWith(content, PngSignature) ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                Gif => StartsWith(content, "GIF87a"u8) || StartsWith(content, "GIF89a"u8) ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                Webp => IsWebp(content) ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                Docx => InspectOoxml(content, "word/"),
                Xlsx => InspectOoxml(content, "xl/"),
                Pptx => InspectOoxml(content, "ppt/"),
                Odt => IsOpenDocument(content, "application/vnd.oasis.opendocument.text") ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                Ods => IsOpenDocument(content, "application/vnd.oasis.opendocument.spreadsheet") ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                OleDoc or OleXls or OlePpt => StartsWith(content, OleSignature) ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                Text or Csv => IsPlainText(content) ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch,
                _ => InspectionOutcome.SignatureMismatch,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or NotSupportedException or ArgumentException)
        {
            // Bozuk/kesik paket: imza uyuşmuyor sayılır (içerik çözülmez, istisna kullanıcıya sızmaz).
            return InspectionOutcome.SignatureMismatch;
        }
    }

    private static string NormalizeMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        var media = semicolon >= 0 ? value[..semicolon] : value;
        return media.Trim().ToLowerInvariant();
    }

    private static bool StartsWith(Stream stream, ReadOnlySpan<byte> prefix)
    {
        Span<byte> buffer = stackalloc byte[prefix.Length];
        stream.Position = 0;
        return ReadFully(stream, buffer) == prefix.Length && buffer.SequenceEqual(prefix);
    }

    private static bool Contains(Stream stream, ReadOnlySpan<byte> needle, int probeBytes)
    {
        var buffer = new byte[(int)Math.Min(stream.Length, probeBytes)];
        stream.Position = 0;
        var read = ReadFully(stream, buffer);
        return buffer.AsSpan(0, read).IndexOf(needle) >= 0;
    }

    private static bool IsWebp(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.Position = 0;
        return ReadFully(stream, header) == 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>OOXML: ZIP + merkezi dizinde <c>[Content_Types].xml</c> ve <c>{word|xl|ppt}/</c> girişi; makro/yürütülebilir giriş varsa reddedilir.</summary>
    private static InspectionOutcome InspectOoxml(Stream stream, string requiredPrefix)
    {
        if (!StartsWith(stream, ZipLocalHeader) || !TryReadEntryCount(stream, out var count) || count > MaxZipEntries)
        {
            return InspectionOutcome.SignatureMismatch;
        }

        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var hasContentTypes = false;
        var hasPart = false;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return InspectionOutcome.ForbiddenContent;
            }

            hasContentTypes |= string.Equals(name, "[Content_Types].xml", StringComparison.Ordinal);
            hasPart |= name.StartsWith(requiredPrefix, StringComparison.Ordinal);
        }

        return hasContentTypes && hasPart ? InspectionOutcome.Ok : InspectionOutcome.SignatureMismatch;
    }

    /// <summary>Uçtaki "end of central directory" kaydından toplam giriş sayısı (zip bombası/CPU koruması: girişler açılmadan sayılır).</summary>
    private static bool TryReadEntryCount(Stream stream, out int count)
    {
        count = 0;
        var tail = (int)Math.Min(stream.Length, ZipTailProbeBytes);
        if (tail < 22)
        {
            return false;
        }

        var buffer = new byte[tail];
        stream.Position = stream.Length - tail;
        var read = ReadFully(stream, buffer);
        for (var i = read - 22; i >= 0; i--)
        {
            if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
            {
                count = buffer[i + 10] | (buffer[i + 11] << 8);
                return true;
            }
        }

        return false;
    }

    /// <summary>OpenDocument: ilk yerel giriş <c>mimetype</c>, sıkıştırmasız ve beklenen dize.</summary>
    private static bool IsOpenDocument(Stream stream, string expectedMimeType)
    {
        Span<byte> header = stackalloc byte[30];
        stream.Position = 0;
        if (ReadFully(stream, header) != 30 || !header[..4].SequenceEqual(ZipLocalHeader))
        {
            return false;
        }

        var method = header[8] | (header[9] << 8);
        var compressedSize = header[18] | (header[19] << 8) | (header[20] << 16) | (header[21] << 24);
        var nameLength = header[26] | (header[27] << 8);
        var extraLength = header[28] | (header[29] << 8);
        if (method != 0 || nameLength != "mimetype".Length || compressedSize != expectedMimeType.Length)
        {
            return false;
        }

        var body = new byte[nameLength + extraLength + compressedSize];
        if (ReadFully(stream, body) != body.Length)
        {
            return false;
        }

        return Encoding.ASCII.GetString(body, 0, nameLength) == "mimetype"
            && Encoding.ASCII.GetString(body, nameLength + extraLength, compressedSize) == expectedMimeType;
    }

    /// <summary>
    /// <c>txt</c>/<c>csv</c>: ilk 8 KB geçerli UTF-8 (BOM serbest), NUL yok, <c>\t \r \n</c> dışı kontrol karakteri yok ve HTML/SVG/script ile başlamıyor.
    /// </summary>
    private static bool IsPlainText(Stream stream)
    {
        var buffer = new byte[(int)Math.Min(stream.Length, TextProbeBytes)];
        stream.Position = 0;
        var read = ReadFully(stream, buffer);
        if (read == 0)
        {
            return false;
        }

        var chars = new char[Encoding.UTF8.GetMaxCharCount(read)];
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetDecoder();
        int charCount;
        try
        {
            // flush=false: 8 KB sınırında bölünen çok baytlı karakter geçerli sayılır.
            charCount = decoder.GetChars(buffer, 0, read, chars, 0, flush: stream.Length <= TextProbeBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var start = 0;
        if (charCount > 0 && chars[0] == '﻿')
        {
            start = 1;
        }

        for (var i = start; i < charCount; i++)
        {
            var ch = chars[i];
            if (ch == '\0' || (char.IsControl(ch) && ch is not ('\t' or '\r' or '\n')))
            {
                return false;
            }
        }

        var head = new string(chars, start, Math.Min(charCount - start, 256)).TrimStart();
        return !LooksLikeMarkup(head);
    }

    private static bool LooksLikeMarkup(string head)
    {
        if (head.Length == 0 || head[0] != '<')
        {
            return false;
        }

        foreach (var marker in MarkupStarts)
        {
            if (head.AsSpan(1).TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] MarkupStarts =
    [
        "!doctype", "html", "head", "body", "svg", "script", "iframe", "?xml", "meta", "link", "style", "img", "object", "embed", "form", "a ", "!--",
    ];
}
