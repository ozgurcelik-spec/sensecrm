using System.Text;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Shared.Contracts.Security;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Integrations.Tests.Unit;

/// <summary>Webhook imza referans doğrulayıcısı (D8): plan doğrulama vektörü, tolerans sınırları, kurcalama, bozuk başlık.</summary>
public sealed class WebhookSignatureTests
{
    private const string Secret = "whsec_test_secret_do_not_use";
    private const string PreviousSecret = "whsec_test_previous_do_not_use";
    private const long T = 1700000000;

    private const string Body =
        """{"id":"0192f0a1-0000-7000-8000-000000000001","type":"lead.created","version":1,"occurredAt":"2026-09-20T09:00:00Z","tenantId":"0192f0a1-0000-7000-8000-0000000000aa","data":{"leadId":"0192f0a1-0000-7000-8000-0000000000bb","source":"web","ownerUserId":"0192f0a1-0000-7000-8000-0000000000cc"}}""";

    private static byte[] Bytes => Encoding.UTF8.GetBytes(Body);

    [Fact]
    public void PlanVector_MatchesExactly_ForCurrentAndPreviousSecret()
    {
        Body.Length.ShouldBe(290);
        WebhookSignature.ComputeHex(Secret, T, Bytes).ShouldBe("2b983e0f8a409dd77786abc9af28fc4799e30692be01c35141e1627a9839f72e");
        WebhookSignature.ComputeHex(PreviousSecret, T, Bytes).ShouldBe("ade2c1096a0e4befec037cad54277de09d333de4b1b1258fccc0820a1fcc9662");
        WebhookSignature.BuildHeader(T, Bytes, [Secret, PreviousSecret])
            .ShouldBe("t=1700000000,v1=2b983e0f8a409dd77786abc9af28fc4799e30692be01c35141e1627a9839f72e,v1=ade2c1096a0e4befec037cad54277de09d333de4b1b1258fccc0820a1fcc9662");
    }

    [Fact]
    public void GraceHeader_VerifiesWithEitherSecret_ButNotWithAThirdOne()
    {
        var header = WebhookSignature.BuildHeader(T, Bytes, [Secret, PreviousSecret]);
        var now = DateTimeOffset.FromUnixTimeSeconds(T);
        WebhookSignature.Verify(Secret, header, Bytes, now, TimeSpan.FromSeconds(300)).ShouldBeTrue();
        WebhookSignature.Verify(PreviousSecret, header, Bytes, now, TimeSpan.FromSeconds(300)).ShouldBeTrue();
        WebhookSignature.Verify("whsec_other", header, Bytes, now, TimeSpan.FromSeconds(300)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(-299, true)]
    [InlineData(-300, true)]
    [InlineData(-301, false)]
    public void ToleranceBoundary_IsInclusiveAt300_AndRejectsFutureTimestampsBeyondIt(int offsetSeconds, bool accepted)
    {
        var header = WebhookSignature.BuildHeader(T, Bytes, [Secret]);
        var now = DateTimeOffset.FromUnixTimeSeconds(T + offsetSeconds);
        WebhookSignature.Verify(Secret, header, Bytes, now, TimeSpan.FromSeconds(300)).ShouldBe(accepted);
    }

    [Fact]
    public void TamperedBody_ByOneByte_WrongSecret_OrChangedTimestamp_AreRejected()
    {
        var header = WebhookSignature.BuildHeader(T, Bytes, [Secret]);
        var now = DateTimeOffset.FromUnixTimeSeconds(T);
        var tampered = (byte[])Bytes.Clone();
        tampered[10] ^= 0x01;
        WebhookSignature.Verify(Secret, header, tampered, now, TimeSpan.FromSeconds(300)).ShouldBeFalse();
        WebhookSignature.Verify("whsec_wrong", header, Bytes, now, TimeSpan.FromSeconds(300)).ShouldBeFalse();
        WebhookSignature.Verify(Secret, header.Replace("t=1700000000", "t=1700000001", StringComparison.Ordinal), Bytes, now, TimeSpan.FromSeconds(300)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=2b983e0f8a409dd77786abc9af28fc4799e30692be01c35141e1627a9839f72e")]
    [InlineData("t=1700000000")]
    [InlineData("t=abc,v1=2b983e0f8a409dd77786abc9af28fc4799e30692be01c35141e1627a9839f72e")]
    [InlineData("t=1700000000,v1=nothex")]
    [InlineData("t=1700000000,v1=2b98")]
    [InlineData("t=1700000000,t=1700000000,v1=2b983e0f8a409dd77786abc9af28fc4799e30692be01c35141e1627a9839f72e")]
    [InlineData("t=-5,v1=2b983e0f8a409dd77786abc9af28fc4799e30692be01c35141e1627a9839f72e")]
    public void MalformedHeaders_AreRejected(string? header) =>
        WebhookSignature.Verify(Secret, header, Bytes, DateTimeOffset.FromUnixTimeSeconds(T), TimeSpan.FromSeconds(300)).ShouldBeFalse();

    [Fact]
    public void OverlongHeader_IsRejected() =>
        WebhookSignature.Verify(Secret, "t=1700000000,v1=" + new string('a', 5000), Bytes, DateTimeOffset.FromUnixTimeSeconds(T), TimeSpan.FromSeconds(300)).ShouldBeFalse();

    [Fact]
    public void EveryAttemptUsesFreshTimestamp_ButTheBodyStaysByteForByteTheSame()
    {
        var first = WebhookSignature.BuildHeader(T, Bytes, [Secret]);
        var second = WebhookSignature.BuildHeader(T + 60, Bytes, [Secret]);
        first.ShouldNotBe(second);
        WebhookSignature.Verify(Secret, second, Bytes, DateTimeOffset.FromUnixTimeSeconds(T + 60), TimeSpan.FromSeconds(300)).ShouldBeTrue();
    }
}

/// <summary>API anahtarı biçimi (D9): katı ayrıştırma, Split('_', 4), özet vektörü.</summary>
public sealed class ApiKeyTokenTests
{
    private static readonly Guid Tenant = new("0192f0a1-0000-7000-8000-0000000000aa");

    [Fact]
    public void GeneratedKey_RoundTrips_AndContainsOnlyThePrefixInDisplay()
    {
        var parts = ApiKeyToken.Generate(Tenant);
        var token = ApiKeyToken.Format(Tenant, parts.Prefix, parts.Secret);

        token.ShouldStartWith("crmk_");
        token.Length.ShouldBeLessThanOrEqualTo(ApiKeyToken.MaxLength);
        ApiKeyToken.TryParse(token, out var parsed).ShouldBeTrue();
        parsed.TenantId.ShouldBe(Tenant);
        parsed.Prefix.ShouldBe(parts.Prefix);
        parsed.Secret.ShouldBe(parts.Secret);
        parsed.Display.ShouldBe("crmk_" + parts.Prefix);
        parsed.Display.ShouldNotContain(parts.Secret);
    }

    [Fact]
    public void SecretMayContainUnderscores_SplitLimitedToFourParts()
    {
        var secret = "abc_def_" + new string('x', ApiKeyToken.SecretLength - 8);
        ApiKeyToken.TryParse(ApiKeyToken.Format(Tenant, "0a1b2c3d", secret), out var parts).ShouldBeTrue();
        parts.Secret.ShouldBe(secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("crmk_")]
    [InlineData("crmk_x_y_z")]
    [InlineData("crmx_0192f0a100007000800000000000000aa_0a1b2c3d_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ")]
    public void MalformedTokens_AreRejected(string? token) => ApiKeyToken.TryParse(token, out _).ShouldBeFalse();

    [Fact]
    public void WrongSegmentShapes_AreRejected()
    {
        var good = ApiKeyToken.Generate(Tenant);
        ApiKeyToken.TryParse(ApiKeyToken.Format(Tenant, good.Prefix.ToUpperInvariant(), good.Secret), out _).ShouldBeFalse();
        ApiKeyToken.TryParse(ApiKeyToken.Format(Tenant, good.Prefix, good.Secret + "x"), out _).ShouldBeFalse();
        ApiKeyToken.TryParse(ApiKeyToken.Format(Tenant, good.Prefix, good.Secret[..^1]), out _).ShouldBeFalse();
        ApiKeyToken.TryParse(ApiKeyToken.Format(Tenant, good.Prefix, good.Secret[..^1] + "!"), out _).ShouldBeFalse();
        ApiKeyToken.TryParse(ApiKeyToken.Format(Tenant, good.Prefix, good.Secret) + new string('a', 100), out _).ShouldBeFalse();
    }

    [Fact]
    public void SecretHash_IsSha256_OfTheSecret_WithThePlanVector()
    {
        var hex = Convert.ToHexStringLower(ApiKeyToken.HashSecret("s3cr3t-test-secret"));
        hex.ShouldStartWith("4723d744");
        hex.ShouldEndWith("1b46");
        hex.Length.ShouldBe(64);
    }

    [Fact]
    public void ScopePolicy_AllowedSet_ExcludesEveryOrgKey_AndApprovalsDecide()
    {
        var catalog = new[]
        {
            new Permission("org.settings.manage", "identity", "org"),
            new Permission("org.integrations.manage", "integrations", "org"),
            new Permission("crm.leads.read", "sales", "crm"),
            new Permission("crm.approvals.decide", "workflows", "crm"),
            new Permission("crm.reports.read", "crm", "crm"),
        };

        var allowed = ApiKeyScopePolicy.Allowed(catalog);
        allowed.ShouldBe(["crm.leads.read", "crm.reports.read"]);
        allowed.Where(a => a.StartsWith("org.", StringComparison.Ordinal)).ShouldBeEmpty();
    }
}
