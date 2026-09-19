using Microsoft.Extensions.Primitives;

namespace Sense.Crm.Api.Middleware;

/// <summary>
/// İstemciden gelen izleme/bağlam başlıklarını güvenli okur. Değerler loglara, span etiketlerine ve yanıt başlıklarına
/// yansıdığından yalnızca kısa, ASCII harf/rakam ve <c>- _ . : /</c> karakterlerinden oluşan değerler kabul edilir;
/// aksi halde değer yok sayılır (log injection, başlık bölme ve sınırsız uzunluk riskine karşı).
/// </summary>
public static class HeaderValues
{
    public const int MaxIdLength = 128;
    public const int MaxClientInfoLength = 64;

    public static string? Read(IHeaderDictionary headers, string name, int maxLength)
    {
        var raw = headers.TryGetValue(name, out StringValues values) ? values.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        return value.Length <= maxLength && value.All(IsAllowed) ? value : null;
    }

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/';
}
