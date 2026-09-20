using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Sense.Crm.Modules.Integrations.Domain;

namespace Sense.Crm.Modules.Integrations.Application.Security;

/// <summary>IPv4/IPv6 ağ öneki (CIDR): ayrıştırma ve üyelik. Bayt karşılaştırmalıdır (sınır adresleri dahil).</summary>
public readonly record struct Cidr(IPAddress Network, int PrefixLength)
{
    public static bool TryParse(string? text, out Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !IPAddress.TryParse(text.AsSpan(0, slash), out var address)
            || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix > max || (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6))
        {
            return false;
        }

        cidr = new Cidr(address, prefix);
        return true;
    }

    /// <summary>Yalnız <c>/0</c> (tüm internet) reddi için: <c>0.0.0.0/0</c>, <c>::/0</c>.</summary>
    public bool IsAny => PrefixLength == 0;

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Network.AddressFamily)
        {
            return false;
        }

        Span<byte> a = stackalloc byte[16];
        Span<byte> n = stackalloc byte[16];
        if (!address.TryWriteBytes(a, out var length) || !Network.TryWriteBytes(n, out _))
        {
            return false;
        }

        var fullBytes = PrefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i])
            {
                return false;
            }
        }

        var rest = PrefixLength % 8;
        if (rest == 0 || fullBytes >= length)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }
}

/// <summary>Engel gerekçesi (günlük/teşhis; kullanıcıya <c>blocked_destination</c> olarak yansır).</summary>
public enum IpBlockKind
{
    None,

    /// <summary>Kalıcı engel (yapılandırmayla açılamaz): loopback, link-local/metadata, çok noktaya yayın, ayrılmış/dokümantasyon/CGNAT.</summary>
    Permanent,

    /// <summary>Varsayılan engel (özel ağ): yalnız dağıtım yapılandırması <c>AllowedPrivateCidrs</c> ile açılabilir.</summary>
    Private,
}

/// <summary>
/// Hedef IP sınıflandırıcısı (D5, SSRF): kalıcı engel listesi ve varsayılan-engel (özel ağ) listesi. IPv4-eşlenmiş IPv6 (<c>::ffff:a.b.c.d</c>), NAT64 (<c>64:ff9b::/96</c>), 6to4
/// (<c>2002::/16</c>) ve Teredo (<c>2001::/32</c>) içindeki <b>gömülü IPv4</b> ayrıca sınıflandırılır. <see cref="AllowedPrivateCidrs"/> yalnız varsayılan-engel kümesini
/// açar, kalıcı engeli <b>açmaz</b>; <c>allowLoopback</c> yalnız Development/Testing'de loopback'i açar.
/// </summary>
public static class IpClassifier
{
    private static readonly Cidr[] Permanent = Parse(
        "0.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16", "224.0.0.0/4", "240.0.0.0/4", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "198.51.100.0/24",
        "203.0.113.0/24", "198.18.0.0/15", "100.64.0.0/10",
        "::/128", "::1/128", "::/96", "fe80::/10", "fec0::/10", "ff00::/8", "fd00:ec2::254/128", "100::/64", "2001:db8::/32");

    private static readonly Cidr[] PrivateNetworks = Parse("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7");

    private static readonly Cidr Nat64 = Parse("64:ff9b::/96")[0];

    private static readonly Cidr SixToFour = Parse("2002::/16")[0];

    private static readonly Cidr Teredo = Parse("2001::/32")[0];

    private static readonly Cidr LoopbackV4 = Parse("127.0.0.0/8")[0];

    /// <summary>Adres engelli mi; engelliyse türü. <paramref name="allowedPrivate"/> yalnız <see cref="IpBlockKind.Private"/>'ı açar.</summary>
    public static IpBlockKind Classify(IPAddress address, IReadOnlyList<Cidr> allowedPrivate, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(allowedPrivate);

        var worst = IpBlockKind.None;
        foreach (var candidate in Expand(address))
        {
            var kind = ClassifyOne(candidate, allowedPrivate, allowLoopback);
            if (kind == IpBlockKind.Permanent)
            {
                return IpBlockKind.Permanent;
            }

            if (kind == IpBlockKind.Private)
            {
                worst = IpBlockKind.Private;
            }
        }

        return worst;
    }

    public static bool IsBlocked(IPAddress address, IReadOnlyList<Cidr> allowedPrivate, bool allowLoopback) =>
        Classify(address, allowedPrivate, allowLoopback) != IpBlockKind.None;

    private static IpBlockKind ClassifyOne(IPAddress address, IReadOnlyList<Cidr> allowedPrivate, bool allowLoopback)
    {
        if (allowLoopback && (IPAddress.IsLoopback(address) || (address.AddressFamily == AddressFamily.InterNetwork && LoopbackV4.Contains(address))))
        {
            return IpBlockKind.None;
        }

        foreach (var cidr in Permanent)
        {
            if (cidr.Contains(address))
            {
                return IpBlockKind.Permanent;
            }
        }

        foreach (var cidr in PrivateNetworks)
        {
            if (cidr.Contains(address))
            {
                return allowedPrivate.Any(a => a.Contains(address)) ? IpBlockKind.None : IpBlockKind.Private;
            }
        }

        return IpBlockKind.None;
    }

    /// <summary>Adresin kendisi + içine gömülü IPv4 adresleri (eşleme, NAT64, 6to4, Teredo sunucu/istemci).</summary>
    private static List<IPAddress> Expand(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            return [address.MapToIPv4()];
        }

        var result = new List<IPAddress> { address };
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return result;
        }

        var b = address.GetAddressBytes();
        if (Nat64.Contains(address))
        {
            result.Add(new IPAddress(b[12..16]));
        }
        else if (SixToFour.Contains(address))
        {
            result.Add(new IPAddress(b[2..6]));
        }
        else if (Teredo.Contains(address))
        {
            result.Add(new IPAddress(b[4..8]));
            var client = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                client[i] = (byte)(b[12 + i] ^ 0xFF);
            }

            result.Add(new IPAddress(client));
        }

        return result;
    }

    private static Cidr[] Parse(params string[] values) =>
        [.. values.Select(v => Cidr.TryParse(v, out var c) ? c : throw new InvalidOperationException("Invalid built-in CIDR: " + v))];
}

/// <summary>Kayıt/teslimat anı URL doğrulama ilkesi (yapılandırmadan; Production'da dev bayrakları yok sayılır).</summary>
public sealed record UrlPolicy(IReadOnlyList<int> AllowedPorts, IReadOnlyList<string> AllowedHosts, IReadOnlyList<Cidr> AllowedPrivateCidrs, bool AllowLoopback)
{
    public static UrlPolicy From(IntegrationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var w = options.Webhooks;
        var cidrs = w.EffectivePrivateCidrs.Select(c => Cidr.TryParse(c, out var parsed) ? parsed : (Cidr?)null).Where(c => c is not null).Select(c => c!.Value).ToList();
        return new UrlPolicy(w.EffectivePorts, w.EffectiveAllowedHosts, cidrs, w.DevAllowLoopback && options.DevelopmentLike);
    }
}

/// <summary>URL doğrulama sonucu: başarıda normalize URL (fragment atılmış, IDN punycode) ve ana bilgisayar.</summary>
public sealed record UrlCheck(bool Ok, string? Reason, Uri? Uri, string? Host)
{
    public static UrlCheck Invalid(string reason) => new(false, reason, null, null);
}

/// <summary><c>webhook.url_invalid</c> <c>args.reason</c> değerleri.</summary>
public static class UrlInvalidReasons
{
    public const string Scheme = "scheme";
    public const string TooLong = "too_long";
    public const string Userinfo = "userinfo";
    public const string IpLiteral = "ip_literal";
    public const string Host = "host";
    public const string Port = "port";
    public const string NotAllowListed = "not_allow_listed";
}

/// <summary>
/// SSRF koruması, sözdizimsel katman (D5): API'de kayıt/güncellemede ve Worker'da her teslimatta çalışır (DNS yok). Şema yalnız <c>https</c>; uzunluk ≤ 2048; userinfo yasak;
/// IP adresi ana bilgisayarı yasak (ondalık/sekizlik/onaltılık biçimler <c>Uri</c> normalizasyonu sonrası yakalanır); <c>localhost</c>, <c>*.localhost|.local|.internal|.lan</c>,
/// tek etiketli ad ve sayısal son etiket yasak; IDN punycode'a çevrilir; port izin listesinde olmalı; fragment atılır; operatör <c>AllowedHosts</c> (boş değilse) eşleşmeli.
/// </summary>
public static class SsrfGuard
{
    private static readonly string[] BlockedSuffixes = [".localhost", ".local", ".internal", ".lan"];

    public static UrlCheck Validate(string? raw, UrlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UrlCheck.Invalid(UrlInvalidReasons.Host);
        }

        if (raw.Length > IntegrationsLimits.UrlMaxLength)
        {
            return UrlCheck.Invalid(UrlInvalidReasons.TooLong);
        }

        var text = raw.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
        {
            return UrlCheck.Invalid(UrlInvalidReasons.Scheme);
        }

        var loopback = policy.AllowLoopback && IsLoopbackHost(uri);
        var https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        if (!https && !(loopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            return UrlCheck.Invalid(UrlInvalidReasons.Scheme);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || (text.Contains('@', StringComparison.Ordinal) && HasAuthorityUserInfo(text)))
        {
            return UrlCheck.Invalid(UrlInvalidReasons.Userinfo);
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host.Length > IntegrationsLimits.HostMaxLength || (Uri.CheckHostName(host) == UriHostNameType.Unknown && uri.HostNameType != UriHostNameType.IPv6))
        {
            return UrlCheck.Invalid(UrlInvalidReasons.Host);
        }

        if (!loopback)
        {
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(host.Trim('[', ']'), out _) || LastLabelNumeric(host))
            {
                return UrlCheck.Invalid(UrlInvalidReasons.IpLiteral);
            }

            if (host == "localhost" || !host.Contains('.', StringComparison.Ordinal) || BlockedSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
            {
                return UrlCheck.Invalid(UrlInvalidReasons.Host);
            }

            if (!policy.AllowedPorts.Contains(uri.Port))
            {
                return UrlCheck.Invalid(UrlInvalidReasons.Port);
            }

            if (policy.AllowedHosts.Count > 0 && !policy.AllowedHosts.Any(pattern => MatchesHost(host, pattern)))
            {
                return UrlCheck.Invalid(UrlInvalidReasons.NotAllowListed);
            }
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty, Host = uri.IdnHost };
        return new UrlCheck(true, null, builder.Uri, host);
    }

    /// <summary><c>example.com</c> tam, <c>*.example.com</c> alt alan (en az bir etiket; <c>example.com</c> kendisi ve <c>evilexample.com</c> eşleşmez).</summary>
    public static bool MatchesHost(string host, string pattern)
    {
        var p = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        var h = host.TrimEnd('.').ToLowerInvariant();
        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = p[1..];
            return h.Length > suffix.Length && h.EndsWith(suffix, StringComparison.Ordinal);
        }

        return string.Equals(h, p, StringComparison.Ordinal);
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        var host = uri.IdnHost.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Yetki bölümünde (<c>://</c> ile ilk <c>/ ? #</c> arası) <c>@</c> var mı (ör. <c>https://evil.com@good.com</c>; <c>Uri</c> zaten userinfo verir, ek güvence).</summary>
    private static bool HasAuthorityUserInfo(string text)
    {
        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var rest = text[(start + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        return authority.Contains('@', StringComparison.Ordinal);
    }

    private static bool LastLabelNumeric(string host)
    {
        var last = host[(host.LastIndexOf('.') + 1)..];
        return last.Length > 0 && last.All(char.IsAsciiDigit);
    }
}
