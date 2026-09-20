using System.Net;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.Security;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Integrations.Tests.Unit;

/// <summary>SSRF matrisi (D5): IP sınıflandırıcı (kalıcı/varsayılan engel, gömülü IPv4) ve URL sözdizimi denetimi.</summary>
public sealed class IpClassifierTests
{
    private static readonly IReadOnlyList<Cidr> NoPrivate = [];

    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    [Theory]
    // Kalıcı engel: ilk / orta / son adres
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.0.5")]
    [InlineData("192.0.2.77")]
    [InlineData("198.51.100.9")]
    [InlineData("203.0.113.200")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.200")]
    [InlineData("100.127.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("febf:ffff::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("ff02::1")]
    // IPv4 gömülü biçimler
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2002:a9fe:a9fe::1")]
    // Varsayılan engel (özel ağ; yapılandırma açmadıkça)
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.20.5.5")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fdff:ffff::1")]
    public void BlockedAddresses_AreBlocked(string address) => IpClassifier.IsBlocked(Ip(address), NoPrivate, allowLoopback: false).ShouldBeTrue(address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("11.0.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    [InlineData("192.169.0.1")]
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    public void PublicAddresses_AndBoundaryNeighbours_AreAllowed(string address) => IpClassifier.IsBlocked(Ip(address), NoPrivate, allowLoopback: false).ShouldBeFalse(address);

    [Fact]
    public void AllowedPrivateCidrs_OnlyOpenTheDefaultBlockedSet_NeverThePermanentOne()
    {
        var allow = new[] { Cidr.TryParse("10.0.0.0/8", out var a) ? a : default, Cidr.TryParse("169.254.0.0/16", out var b) ? b : default, Cidr.TryParse("127.0.0.0/8", out var c) ? c : default, Cidr.TryParse("192.168.1.0/24", out var d) ? d : default };

        IpClassifier.IsBlocked(Ip("10.5.5.5"), allow, false).ShouldBeFalse("configured private range opens");
        IpClassifier.IsBlocked(Ip("192.168.1.7"), allow, false).ShouldBeFalse();
        IpClassifier.IsBlocked(Ip("192.168.2.7"), allow, false).ShouldBeTrue("outside the configured /24");
        IpClassifier.IsBlocked(Ip("172.16.0.1"), allow, false).ShouldBeTrue("not configured");
        IpClassifier.IsBlocked(Ip("169.254.169.254"), allow, false).ShouldBeTrue("metadata can never be opened by configuration");
        IpClassifier.IsBlocked(Ip("127.0.0.1"), allow, false).ShouldBeTrue("loopback can never be opened by AllowedPrivateCidrs");
        IpClassifier.IsBlocked(Ip("::ffff:10.5.5.5"), allow, false).ShouldBeFalse("mapped form follows the embedded IPv4");
        IpClassifier.IsBlocked(Ip("::ffff:169.254.169.254"), allow, false).ShouldBeTrue();
    }

    [Fact]
    public void DevAllowLoopback_OnlyOpensLoopback()
    {
        IpClassifier.IsBlocked(Ip("127.0.0.1"), NoPrivate, allowLoopback: true).ShouldBeFalse();
        IpClassifier.IsBlocked(Ip("::1"), NoPrivate, allowLoopback: true).ShouldBeFalse();
        IpClassifier.IsBlocked(Ip("169.254.169.254"), NoPrivate, allowLoopback: true).ShouldBeTrue();
        IpClassifier.IsBlocked(Ip("10.0.0.1"), NoPrivate, allowLoopback: true).ShouldBeTrue();
    }

    [Fact]
    public void CidrContains_HandlesPrefixBoundaries()
    {
        Cidr.TryParse("172.16.0.0/12", out var cidr).ShouldBeTrue();
        cidr.Contains(Ip("172.16.0.0")).ShouldBeTrue();
        cidr.Contains(Ip("172.31.255.255")).ShouldBeTrue();
        cidr.Contains(Ip("172.15.255.255")).ShouldBeFalse();
        cidr.Contains(Ip("172.32.0.0")).ShouldBeFalse();
        Cidr.TryParse("0.0.0.0/0", out var any).ShouldBeTrue();
        any.IsAny.ShouldBeTrue();
        Cidr.TryParse("10.0.0.0/33", out _).ShouldBeFalse();
        Cidr.TryParse("nonsense", out _).ShouldBeFalse();
        Cidr.TryParse("10.0.0.0", out _).ShouldBeFalse();
    }
}

public sealed class SsrfGuardTests
{
    private static readonly UrlPolicy Strict = new([443, 8443], [], [], AllowLoopback: false);

    [Theory]
    [InlineData("https://hooks.example.com/path", true)]
    [InlineData("https://hooks.example.com:8443/path?x=1", true)]
    [InlineData("https://sub.a.example.com/", true)]
    public void PublicHttpsUrls_OnAllowedPorts_AreAccepted(string url, bool ok) => SsrfGuard.Validate(url, Strict).Ok.ShouldBe(ok);

    [Theory]
    [InlineData("http://hooks.example.com/", UrlInvalidReasons.Scheme)]
    [InlineData("ftp://hooks.example.com/", UrlInvalidReasons.Scheme)]
    [InlineData("file:///etc/passwd", UrlInvalidReasons.Scheme)]
    [InlineData("gopher://hooks.example.com/", UrlInvalidReasons.Scheme)]
    [InlineData("hooks.example.com/no-scheme", UrlInvalidReasons.Scheme)]
    [InlineData("https://user:pw@hooks.example.com/", UrlInvalidReasons.Userinfo)]
    [InlineData("https://user@hooks.example.com/", UrlInvalidReasons.Userinfo)]
    [InlineData("https://hooks.example.com@evil.com/", UrlInvalidReasons.Userinfo)]
    [InlineData("https://127.0.0.1/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://2130706433/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://0x7f.1/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://017700000001/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://127.1/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://[::1]/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://[::ffff:127.0.0.1]/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://169.254.169.254/latest/meta-data/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://8.8.8.8/", UrlInvalidReasons.IpLiteral)]
    [InlineData("https://localhost/", UrlInvalidReasons.Host)]
    [InlineData("https://LOCALHOST./", UrlInvalidReasons.Host)]
    [InlineData("https://a.localhost/", UrlInvalidReasons.Host)]
    [InlineData("https://x.internal/", UrlInvalidReasons.Host)]
    [InlineData("https://printer.local/", UrlInvalidReasons.Host)]
    [InlineData("https://nas.lan/", UrlInvalidReasons.Host)]
    [InlineData("https://intranet/", UrlInvalidReasons.Host)]
    [InlineData("https://hooks.example.com:80/", UrlInvalidReasons.Port)]
    [InlineData("https://hooks.example.com:22/", UrlInvalidReasons.Port)]
    [InlineData("https://hooks.example.com:6379/", UrlInvalidReasons.Port)]
    public void Rejections_CarryTheDocumentedReason(string url, string reason)
    {
        var result = SsrfGuard.Validate(url, Strict);
        result.Ok.ShouldBeFalse(url);
        result.Reason.ShouldBe(reason, url);
    }

    [Fact]
    public void EmptyOrMissingHost_TooLongUrl_AreRejected()
    {
        SsrfGuard.Validate(string.Empty, Strict).Reason.ShouldBe(UrlInvalidReasons.Host);
        SsrfGuard.Validate("   ", Strict).Ok.ShouldBeFalse();
        SsrfGuard.Validate("https:///path", Strict).Ok.ShouldBeFalse();
        SsrfGuard.Validate("https://hooks.example.com/" + new string('a', 2048), Strict).Reason.ShouldBe(UrlInvalidReasons.TooLong);
        SsrfGuard.Validate("https://" + new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 63) + ".com/", Strict).Ok.ShouldBeFalse();
    }

    [Fact]
    public void FragmentIsDropped_AndIdnBecomesPunycode_AndHostIsNormalised()
    {
        var result = SsrfGuard.Validate("https://Hooks.Example.COM./a/b?token=1#frag", Strict);
        result.Ok.ShouldBeTrue();
        result.Host.ShouldBe("hooks.example.com");
        result.Uri!.AbsoluteUri.ShouldNotContain("#");
        result.Uri.AbsoluteUri.ShouldContain("?token=1");

        var idn = SsrfGuard.Validate("https://bücher.example/", Strict);
        idn.Ok.ShouldBeTrue();
        idn.Host.ShouldBe("xn--bcher-kva.example");
    }

    [Fact]
    public void AllowList_MatchesExactAndWildcardOnly_NotSuffixTricks()
    {
        var policy = new UrlPolicy([443], ["example.com", "*.corp.example.net"], [], false);
        SsrfGuard.Validate("https://example.com/x", policy).Ok.ShouldBeTrue();
        SsrfGuard.Validate("https://a.corp.example.net/x", policy).Ok.ShouldBeTrue();
        SsrfGuard.Validate("https://a.b.corp.example.net/x", policy).Ok.ShouldBeTrue();
        SsrfGuard.Validate("https://corp.example.net/x", policy).Reason.ShouldBe(UrlInvalidReasons.NotAllowListed);
        SsrfGuard.Validate("https://example.com.evil.net/x", policy).Reason.ShouldBe(UrlInvalidReasons.NotAllowListed);
        SsrfGuard.Validate("https://evilexample.com/x", policy).Reason.ShouldBe(UrlInvalidReasons.NotAllowListed);
        SsrfGuard.Validate("https://notcorp.example.net/x", policy).Reason.ShouldBe(UrlInvalidReasons.NotAllowListed);
        SsrfGuard.MatchesHost("evilexample.com", "*.example.com").ShouldBeFalse();
    }

    [Fact]
    public void DevAllowLoopback_IsHonouredOnlyWhenTheEnvironmentIsDevelopmentLike()
    {
        var options = new IntegrationsOptions { Webhooks = { DevAllowLoopback = true }, DevelopmentLike = false };
        var production = UrlPolicy.From(options);
        production.AllowLoopback.ShouldBeFalse();
        SsrfGuard.Validate("http://127.0.0.1:5000/hook", production).Ok.ShouldBeFalse();
        SsrfGuard.Validate("https://localhost/hook", production).Ok.ShouldBeFalse();

        options.DevelopmentLike = true;
        var dev = UrlPolicy.From(options);
        dev.AllowLoopback.ShouldBeTrue();
        SsrfGuard.Validate("http://127.0.0.1:5000/hook", dev).Ok.ShouldBeTrue();
        SsrfGuard.Validate("http://169.254.169.254/", dev).Ok.ShouldBeFalse("dev flag opens loopback only");
        SsrfGuard.Validate("http://10.0.0.1/", dev).Ok.ShouldBeFalse();
    }
}
