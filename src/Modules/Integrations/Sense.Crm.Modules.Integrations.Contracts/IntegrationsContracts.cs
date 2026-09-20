using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sense.Crm.Shared.Contracts.Security;

namespace Sense.Crm.Modules.Integrations.Contracts;

/// <summary>
/// Entegrasyon izni (<c>org.integrations.manage</c>): webhook abonelikleri, API anahtarları, teslimat günlüğü ve OpenAPI belgesi. Yönetim düzlemi izni olduğundan
/// (<c>org.*</c>) bir API anahtarına <b>asla</b> verilemez: anahtar anahtar üretemez/iptal edemez/sır göremez.
/// </summary>
public static class IntegrationsPermissions
{
    public const string Module = "integrations";
    public const string Group = "org";

    public const string Manage = "org.integrations.manage";

    public static IReadOnlyList<Permission> All { get; } =
    [
        new(Manage, Module, Group),
    ];
}

/// <summary>Webhook olay türleri (v1 kataloğu; <b>yalnız ekleme</b> ile büyür). Tür adı tel değeridir.</summary>
public static class WebhookEventTypes
{
    public const string LeadCreated = "lead.created";
    public const string LeadConverted = "lead.converted";
    public const string AccountCreated = "account.created";
    public const string ContactCreated = "contact.created";
    public const string DealStageChanged = "deal.stage_changed";
    public const string DealWon = "deal.won";
    public const string DealLost = "deal.lost";
    public const string QuoteAccepted = "quote.accepted";
    public const string OrderCreated = "order.created";
    public const string CaseCreated = "case.created";
    public const string CaseResolved = "case.resolved";

    /// <summary>Abonelik türü değildir: yalnız test (<c>POST …/test</c>) zarfı.</summary>
    public const string Ping = "ping";

    /// <summary>Abone olunabilir türler (<see cref="Ping"/> hariç).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        LeadCreated, LeadConverted, AccountCreated, ContactCreated, DealStageChanged, DealWon, DealLost, QuoteAccepted, OrderCreated, CaseCreated, CaseResolved,
    ];
}

/// <summary>
/// Webhook imzası (D8, referans doğrulayıcı): <c>X-Crm-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;[,v1=&lt;hex&gt;]</c>; <c>v1 = HMAC-SHA256(secret, "&lt;t&gt;." + gövde)</c>.
/// Testler ve geliştirici rehberi aynı kodu kullanır. Alıcı kuralı: <c>|now − t| &gt; tolerans</c> reddedilir (sınır dahil değil: 300 kabul, 301 ret),
/// <c>v1</c> değerleri <b>sabit zamanlı</b> karşılaştırılır, olay kimliği tekilleştirilir.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Crm-Signature";
    public const string SecretPrefix = "whsec_";
    public const int DefaultToleranceSeconds = 300;
    private const int MaxHeaderLength = 1024;
    private const int Sha256HexLength = 64;

    /// <summary><c>lowerhex(HMAC-SHA256(secret_utf8, "&lt;t&gt;." + body))</c>.</summary>
    public static string ComputeHex(string secret, long timestamp, ReadOnlySpan<byte> body) => Convert.ToHexStringLower(ComputeMac(secret, timestamp, body));

    /// <summary>Başlık değeri; birden çok sır (döndürme grace süresi) → birden çok <c>v1</c> (yeni sır önce).</summary>
    public static string BuildHeader(long timestamp, ReadOnlySpan<byte> body, IReadOnlyList<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var builder = new StringBuilder("t=").Append(timestamp.ToString(CultureInfo.InvariantCulture));
        foreach (var secret in secrets)
        {
            builder.Append(",v1=").Append(ComputeHex(secret, timestamp, body));
        }

        return builder.ToString();
    }

    /// <summary>Başlıktaki herhangi bir <c>v1</c> sırla eşleşir ve zaman damgası toleranstaysa true. Bozuk/eksik başlık → false.</summary>
    public static bool Verify(string secret, string? header, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (!TryParse(header, out var timestamp, out var signatures))
        {
            return false;
        }

        var age = now.ToUnixTimeSeconds() - timestamp;
        if (Math.Abs((double)age) > tolerance.TotalSeconds)
        {
            return false;
        }

        var expected = ComputeMac(secret, timestamp, body);
        var matched = false;
        foreach (var signature in signatures)
        {
            // Erken çıkış yok: tüm v1 değerleri sabit zamanlı karşılaştırılır.
            matched |= CryptographicOperations.FixedTimeEquals(expected, signature);
        }

        return matched;
    }

    /// <summary>Başlığı ayrıştırır (tam bir <c>t</c>, en az bir onaltılık 64 haneli <c>v1</c>; bilinmeyen anahtarlar yok sayılır).</summary>
    public static bool TryParse(string? header, out long timestamp, out IReadOnlyList<byte[]> signatures)
    {
        timestamp = 0;
        signatures = [];
        if (string.IsNullOrWhiteSpace(header) || header.Length > MaxHeaderLength)
        {
            return false;
        }

        var found = false;
        var list = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return false;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (string.Equals(key, "t", StringComparison.Ordinal))
            {
                if (found || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
                {
                    return false;
                }

                found = true;
            }
            else if (string.Equals(key, "v1", StringComparison.Ordinal))
            {
                if (value.Length != Sha256HexLength || !IsLowerHex(value))
                {
                    return false;
                }

                list.Add(Convert.FromHexString(value));
            }
        }

        signatures = list;
        return found && list.Count > 0;
    }

    private static byte[] ComputeMac(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Ayrıştırılmış API anahtarı: <c>crmk_&lt;tenantN&gt;_&lt;prefix&gt;_&lt;secret&gt;</c>.</summary>
public sealed record ApiKeyParts(Guid TenantId, string Prefix, string Secret)
{
    /// <summary>Arayüzde/denetimde/günlükte görünen tek kısım: <c>crmk_&lt;prefix&gt;</c>.</summary>
    public string Display => ApiKeyToken.Display(Prefix);
}

/// <summary>
/// API anahtarı biçimi (D9): <c>crmk_&lt;tenantN&gt;_&lt;prefix&gt;_&lt;secret&gt;</c>; <c>tenantN</c> = kiracı kimliği 32 hane küçük harf onaltılık, <c>prefix</c> = 8 hane
/// onaltılık (kiracıda benzersiz), <c>secret</c> = 32 rastgele bayt base64url (43 karakter; <c>_</c> içerebilir → <c>Split('_', 4)</c>). <c>crmk_</c> öneki gizli-tarama desenidir.
/// Saklanan: <c>SHA-256(secret)</c> (256-bit rastgele sır için hızlı özet yeterlidir).
/// </summary>
public static class ApiKeyToken
{
    public const string Prefix = ApiKeyClaimNames.TokenPrefix;
    public const int MaxLength = 128;
    public const int TenantHexLength = 32;
    public const int PrefixLength = 8;
    public const int SecretLength = 43;

    public static string Display(string prefix) => Prefix + prefix;

    public static string Format(Guid tenantId, string prefix, string secret) =>
        string.Concat(Prefix, tenantId.ToString("N"), "_", prefix, "_", secret);

    /// <summary>Yeni rastgele önek + sır (kiracı kimliği anahtara gömülür).</summary>
    public static ApiKeyParts Generate(Guid tenantId) =>
        new(tenantId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PrefixLength / 2)), Base64Url(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Katı ayrıştırma: ön ek, uzunluk, karakter sınıfları. Herhangi bir sapma false (DB'ye gidilmez).</summary>
    public static bool TryParse(string? token, out ApiKeyParts parts)
    {
        parts = default!;
        if (string.IsNullOrEmpty(token) || token.Length > MaxLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = token.Split('_', 4);
        if (segments.Length != 4 || segments[0] + "_" != Prefix
            || segments[1].Length != TenantHexLength || !IsLowerHex(segments[1])
            || segments[2].Length != PrefixLength || !IsLowerHex(segments[2])
            || segments[3].Length != SecretLength || !IsBase64Url(segments[3])
            || !Guid.TryParseExact(segments[1], "N", out var tenantId))
        {
            return false;
        }

        parts = new ApiKeyParts(tenantId, segments[2], segments[3]);
        return true;
    }

    /// <summary><c>SHA-256(UTF-8(secret))</c> (32 bayt).</summary>
    public static byte[] HashSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBase64Url(string value)
    {
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Anahtar kapsam politikası (D10): verilebilir kümeler <c>crm.*</c> eksi <c>crm.approvals.decide</c>; <c>org.*</c> hiçbir anahtarda olmaz.</summary>
public static class ApiKeyScopePolicy
{
    public const string CrmPrefix = "crm.";
    public const string ApprovalsDecide = "crm.approvals.decide";

    /// <summary>İzin katalogundan anahtara verilebilir kapsamlar (sıralı, tekil).</summary>
    public static IReadOnlyList<string> Allowed(IEnumerable<Permission> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Select(p => p.Key).Where(IsAllowedKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    public static bool IsAllowedKey(string key) =>
        key.StartsWith(CrmPrefix, StringComparison.Ordinal) && !string.Equals(key, ApprovalsDecide, StringComparison.Ordinal);
}
