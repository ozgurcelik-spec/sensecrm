using System.Globalization;
using System.Text;

namespace Sense.Crm.Modules.Files.Domain;

/// <summary>
/// Dosya adı temizleme (saf, test edilebilir). İstemcinin gönderdiği ad <b>ham girdidir</b>; sonuç yalnız veritabanında ve <c>Content-Disposition</c>'da yaşar,
/// nesne anahtarına/dosya sistemi yoluna hiçbir zaman girmez. Sıra (plan "Dosya adı"): NFC → son <c>/</c> veya <c>\</c> sonrası → kontrol/biçim/çift yönlü
/// karakterler ve <c>&lt; &gt; : " | ? *</c> çıkar → <c>..</c> tek noktaya → baş/son boşluk ve nokta kırp → Windows aygıt adları <c>_</c> önekli →
/// 200 UTF-16 birim (uzantı korunur). Boş kalırsa <c>null</c> (<c>file.name_invalid</c>).
/// </summary>
public static class FileNamePolicy
{
    /// <summary>Çift uzantıda tehlikeli sayılan parçalar (<c>a.exe.pdf</c> reddedilir).</summary>
    public static IReadOnlySet<string> DangerousExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "bat", "cmd", "scr", "msi", "jar", "js", "vbs", "ps1", "html", "htm", "svg", "hta", "lnk",
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string ForbiddenPunctuation = "<>:\"|?*";

    /// <summary>Temizlenmiş ad; boş kalırsa <c>null</c>.</summary>
    public static string? Sanitize(string? raw, int maxLength = FilesLimits.NameMaxLength)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var text = Normalize(StripLoneSurrogates(raw));

        // Dizin yolu atılır: son '/' veya '\' sonrası.
        var cut = text.LastIndexOfAny(['/', '\\']);
        if (cut >= 0)
        {
            text = text[(cut + 1)..];
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsDisallowed(rune))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        var cleaned = builder.ToString();
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        cleaned = cleaned.Trim().Trim('.').Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        cleaned = PrefixReservedDeviceName(cleaned);
        cleaned = Truncate(cleaned, maxLength);
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Temizlenmiş adın <b>son</b> uzantısı (küçük harf, noktasız); yoksa boş dize.</summary>
    public static string GetExtension(string sanitizedName)
    {
        ArgumentNullException.ThrowIfNull(sanitizedName);
        var dot = sanitizedName.LastIndexOf('.');
        return dot < 0 || dot == sanitizedName.Length - 1 ? string.Empty : sanitizedName[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Son uzantıdan önceki herhangi bir parça tehlikeli listedeyse <c>true</c> (<c>a.exe.pdf</c>).</summary>
    public static bool HasDangerousDoubleExtension(string sanitizedName)
    {
        ArgumentNullException.ThrowIfNull(sanitizedName);
        var parts = sanitizedName.Split('.');
        for (var i = 1; i < parts.Length - 1; i++)
        {
            if (DangerousExtensions.Contains(parts[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>ASCII yedek ad (<c>Content-Disposition filename=</c>): <c>"</c>, <c>\</c>, CR/LF ve ASCII dışı karakterler <c>_</c> olur.</summary>
    public static string ToAsciiFallback(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(ch is >= ' ' and <= '~' and not '"' and not '\\' and not '%' ? ch : '_');
        }

        return builder.ToString();
    }

    private static string Normalize(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private static string StripLoneSurrogates(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(ch).Append(value[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsDisallowed(Rune rune)
    {
        if (rune.Value < 0x80 && ForbiddenPunctuation.Contains((char)rune.Value, StringComparison.Ordinal))
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned;
    }

    private static string PrefixReservedDeviceName(string name)
    {
        var firstDot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = firstDot < 0 ? name : name[..firstDot];
        return ReservedDeviceNames.Contains(stem.Trim()) ? "_" + name : name;
    }

    private static string Truncate(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= 17 ? name[dot..] : string.Empty;
        var bodyLength = Math.Max(maxLength - extension.Length, 1);
        var body = name[..(dot > 0 && extension.Length > 0 ? dot : name.Length)];
        if (body.Length > bodyLength)
        {
            body = body[..bodyLength];
            if (char.IsHighSurrogate(body[^1]))
            {
                body = body[..^1];
            }
        }

        var result = (body + extension).Trim().TrimEnd('.');
        return result.Length == 0 ? string.Empty : result;
    }
}
