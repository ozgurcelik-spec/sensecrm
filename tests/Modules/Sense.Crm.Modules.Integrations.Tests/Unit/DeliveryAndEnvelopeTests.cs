using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.OpenApi;
using Sense.Crm.Modules.Integrations.Application.Webhooks;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Domain.Webhooks;
using Sense.Crm.Modules.Integrations.Infrastructure.Delivery;
using Sense.Crm.Modules.Integrations.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Integrations.Tests.Unit;

internal sealed class FixedJitter(double factor = 1.0) : IJitter
{
    public double Factor(int percent) => factor;
}

/// <summary>Sonuç sınıflaması, geri çekilme çizelgesi, Retry-After, yanıt özeti (plan "Teslimat hattı").</summary>
public sealed class DeliveryOutcomeTests
{
    private static readonly WebhookOptions Options = new();

    private static TransportResult Http(int status, TimeSpan? retryAfter = null) => new(TransportStatus.Ok, status, [], retryAfter, TimeSpan.FromMilliseconds(5), null);

    private static DeliveryOutcome Classify(TransportResult result, int attempt) => DeliveryOutcomeClassifier.Classify(result, attempt, Options, new FixedJitter());

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(299)]
    public void TwoXx_IsSuccess(int status) => Classify(Http(status), 1).Kind.ShouldBe(OutcomeKind.Success);

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    public void RetryableStatuses_AreRetried(int status) => Classify(Http(status), 1).Kind.ShouldBe(OutcomeKind.Retry);

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(422)]
    public void OtherClientErrors_AreTerminal_OnASingleAttempt(int status)
    {
        var outcome = Classify(Http(status), 1);
        outcome.Kind.ShouldBe(OutcomeKind.Terminal);
        outcome.Reason.ShouldBe(DeliveryFailureReasons.HttpError);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public void Redirects_AreTerminal_AndNeverFollowed(int status)
    {
        var outcome = Classify(Http(status), 1);
        outcome.Kind.ShouldBe(OutcomeKind.Terminal);
        outcome.Reason.ShouldBe(DeliveryFailureReasons.Redirect);
    }

    [Fact]
    public void BlockedAndTlsErrors_AreTerminal_TimeoutsAndConnectionErrorsAreRetried()
    {
        Classify(new TransportResult(TransportStatus.Blocked, null, [], null, TimeSpan.Zero, "blocked"), 1).Reason.ShouldBe(DeliveryFailureReasons.BlockedDestination);
        Classify(new TransportResult(TransportStatus.TlsError, null, [], null, TimeSpan.Zero, "tls"), 1).Kind.ShouldBe(OutcomeKind.Terminal);
        Classify(new TransportResult(TransportStatus.Timeout, null, [], null, TimeSpan.FromSeconds(10), "timeout"), 1).Kind.ShouldBe(OutcomeKind.Retry);
        Classify(new TransportResult(TransportStatus.ConnectionError, null, [], null, TimeSpan.Zero, "x"), 1).Kind.ShouldBe(OutcomeKind.Retry);
    }

    [Fact]
    public void BackoffSchedule_IsExactly_10s_1m_5m_30m_2h_6h_12h_ThenExhausted()
    {
        var expected = new[] { 10, 60, 300, 1800, 7200, 21600, 43200 };
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            var outcome = Classify(Http(500), attempt);
            outcome.Kind.ShouldBe(OutcomeKind.Retry);
            outcome.RetryDelay.ShouldBe(TimeSpan.FromSeconds(expected[attempt - 1]), $"attempt {attempt}");
        }

        var last = Classify(Http(500), 8);
        last.Kind.ShouldBe(OutcomeKind.Exhausted);
        last.Reason.ShouldBe(DeliveryFailureReasons.RetriesExhausted);
    }

    [Fact]
    public void Jitter_ScalesTheDelay_WithinPlusMinusTwentyPercent()
    {
        DeliveryOutcomeClassifier.Classify(Http(500), 1, Options, new FixedJitter(0.8)).RetryDelay.ShouldBe(TimeSpan.FromSeconds(8));
        DeliveryOutcomeClassifier.Classify(Http(500), 1, Options, new FixedJitter(1.2)).RetryDelay.ShouldBe(TimeSpan.FromSeconds(12));
        for (var i = 0; i < 200; i++)
        {
            var factor = new RandomJitter().Factor(20);
            factor.ShouldBeInRange(0.8, 1.2);
        }
    }

    [Fact]
    public void RetryAfter_OnlyExtendsTheDelay_AndIsCappedAtOneHour()
    {
        Classify(Http(429, TimeSpan.FromSeconds(120)), 1).RetryDelay.ShouldBe(TimeSpan.FromSeconds(120), "longer than planned 10s");
        Classify(Http(429, TimeSpan.FromSeconds(5)), 1).RetryDelay.ShouldBe(TimeSpan.FromSeconds(10), "never shortens");
        Classify(Http(503, TimeSpan.FromDays(3)), 1).RetryDelay.ShouldBe(TimeSpan.FromSeconds(3600), "capped at 3600s");
        Classify(Http(503, TimeSpan.FromDays(3)), 3).RetryDelay.ShouldBe(TimeSpan.FromSeconds(3600), "planned 300s < cap");
    }

    [Fact]
    public void DnsErrors_AreRetriedThreeTimes_ThenTerminal()
    {
        DeliveryOutcomeClassifier.ClassifyDnsFailure(1, Options, new FixedJitter()).Kind.ShouldBe(OutcomeKind.Retry);
        DeliveryOutcomeClassifier.ClassifyDnsFailure(2, Options, new FixedJitter()).Kind.ShouldBe(OutcomeKind.Retry);
        var third = DeliveryOutcomeClassifier.ClassifyDnsFailure(3, Options, new FixedJitter());
        third.Kind.ShouldBe(OutcomeKind.Terminal);
        third.Reason.ShouldBe(DeliveryFailureReasons.DnsError);
    }
}

public sealed class ResponseSnippetTests
{
    [Fact]
    public void BinaryContent_IsReplacedByAMarker() => ResponseSnippet.Build([0x89, 0x50, 0x00, 0x47], 2048).ShouldBe("[binary]");

    [Fact]
    public void Text_IsTruncatedToTheLimit_AndControlCharactersAreCleaned()
    {
        var text = ResponseSnippet.Build(Encoding.UTF8.GetBytes("ok[31mred" + new string('x', 5000)), 100)!;
        text.Length.ShouldBeLessThanOrEqualTo(100);
        text.ShouldNotContain("");
        text.ShouldNotContain("");
    }

    [Theory]
    [InlineData("{\"error\":\"bad\",\"token\":\"abc123\"}", "abc123")]
    [InlineData("authorization: Bearer eyJhbGciOi", "eyJhbGciOi")]
    [InlineData("password=hunter2&x=1", "hunter2")]
    [InlineData("api_key: sk_live_123", "sk_live_123")]
    [InlineData("X-API-Key = topsecretvalue", "topsecretvalue")]
    [InlineData("got whsec_abcDEF123-_xyz back", "abcDEF123-_xyz")]
    [InlineData("used crmk_0192f0a100007000800000000000000aa_0a1b2c3d_secretsecretsecret", "secretsecretsecret")]
    public void SecretPatterns_AreMasked(string input, string secret)
    {
        var snippet = ResponseSnippet.Build(Encoding.UTF8.GetBytes(input), 2048)!;
        snippet.ShouldNotContain(secret);
        snippet.ShouldContain("***");
    }

    [Fact]
    public void EmptyBody_HasNoSnippet() => ResponseSnippet.Build([], 2048).ShouldBeNull();
}

/// <summary>Zarf: belirli serileştirme, altın dosya (golden), PII yasağı ve türetilmiş kimlik.</summary>
public sealed class WebhookEnvelopeTests
{
    private static string G(string suffix) => "0192f0a1-0000-7000-8000-0000000000" + suffix;

    private static string Envelope(string type, string data) =>
        "{\"id\":\"0192f0a1-0000-7000-8000-000000000001\",\"type\":\"" + type + "\",\"version\":1,\"occurredAt\":\"2026-09-20T09:00:00Z\",\"tenantId\":\"" + G("aa")
        + "\",\"actorUserId\":\"" + G("ff") + "\",\"data\":{" + data + "}}";

    private static string Ids(params (string Name, string Suffix)[] fields) => string.Join(",", fields.Select(f => Q(f.Name, G(f.Suffix))));

    public static TheoryData<string, string> Golden => new()
    {
        { "lead.created", Envelope("lead.created", Ids(("leadId", "bb")) + ",\"source\":\"web\"," + Ids(("ownerUserId", "cc"))) },
        { "lead.converted", Envelope("lead.converted", Ids(("leadId", "bb"), ("accountId", "cc"), ("contactId", "dd"), ("dealId", "ee"))) },
        { "account.created", Envelope("account.created", Ids(("accountId", "bb"), ("ownerUserId", "cc"))) },
        { "contact.created", Envelope("contact.created", Ids(("contactId", "bb"), ("accountId", "cc"), ("ownerUserId", "dd"))) },
        { "deal.stage_changed", Envelope("deal.stage_changed", Ids(("dealId", "bb"), ("pipelineId", "cc"), ("fromStageId", "dd"), ("toStageId", "ee")) + ",\"toStageKind\":\"open\",\"amount\":1500.5,\"currency\":\"TRY\"") },
        { "deal.won", Envelope("deal.won", Ids(("dealId", "bb"), ("pipelineId", "cc"), ("toStageId", "ee")) + ",\"amount\":1500.5,\"currency\":\"TRY\"") },
        { "deal.lost", Envelope("deal.lost", Ids(("dealId", "bb"), ("pipelineId", "cc"), ("toStageId", "ee")) + ",\"amount\":1500.5,\"currency\":\"TRY\"") },
        { "quote.accepted", Envelope("quote.accepted", Ids(("quoteId", "bb")) + ",\"number\":\"Q-2026-0001\"," + Ids(("accountId", "cc"), ("dealId", "dd")) + ",\"grandTotal\":1500.5,\"currency\":\"TRY\"") },
        { "order.created", Envelope("order.created", Ids(("orderId", "bb")) + ",\"number\":\"O-2026-0001\"," + Ids(("accountId", "cc"), ("dealId", "dd"), ("quoteId", "ee")) + ",\"grandTotal\":1500.5,\"currency\":\"TRY\",\"source\":\"quote\"") },
        { "case.created", Envelope("case.created", Ids(("caseId", "bb")) + ",\"number\":\"C-2026-0001\"," + Ids(("accountId", "cc"), ("contactId", "dd")) + ",\"priority\":\"normal\",\"channel\":\"web\"," + Ids(("assignedUserId", "ee"))) },
        { "case.resolved", Envelope("case.resolved", Ids(("caseId", "bb")) + ",\"number\":\"C-2026-0001\"," + Ids(("accountId", "cc"), ("contactId", "dd")) + ",\"priority\":\"normal\"," + Ids(("assignedUserId", "ee")) + ",\"resolvedAt\":\"2026-09-20T09:00:00Z\",\"resolutionMinutes\":95,\"slaBreached\":false") },
    };

    private static string Q(string name, string value) => "\"" + name + "\":\"" + value + "\"";

    [Theory]
    [MemberData(nameof(Golden))]
    public void EveryCatalogType_ProducesTheGoldenEnvelope_ByteForByte(string type, string expected) =>
        WebhookEventCatalog.SampleEnvelope(WebhookEventCatalog.Find(type)!).ShouldBe(expected);

    [Fact]
    public void Catalog_CoversEveryEventType_AndOnlyThose()
    {
        WebhookEventCatalog.All.Select(d => d.Type).Order(StringComparer.Ordinal).ShouldBe(WebhookEventTypes.All.Order(StringComparer.Ordinal));
        Golden.Count.ShouldBe(WebhookEventTypes.All.Count);
        WebhookEventCatalog.All.ShouldAllBe(d => d.Version >= 1 && !d.Deprecated);
    }

    [Fact]
    public void SampleData_NeverCarriesForbiddenPiiFieldNames()
    {
        var forbidden = new HashSet<string>(["email", "phone", "name", "firstName", "lastName", "address", "note", "description", "subject", "comment", "body"], StringComparer.OrdinalIgnoreCase);
        foreach (var definition in WebhookEventCatalog.All)
        {
            var keys = Keys(JsonNode.Parse(WebhookEventCatalog.SampleEnvelope(definition))!["data"]!);
            keys.Where(forbidden.Contains).ShouldBeEmpty($"{definition.Type} must not expose PII field names");
        }
    }

    [Fact]
    public void EveryMapper_IsWiredToTheAllowListedFieldsOnly_NoReflectionOverEventProperties()
    {
        // LeadName/Company (kisi adi) event'te var ama zarfa GIRMEZ.
        var data = WebhookDataMappers.Map(new Sense.Crm.Modules.Sales.Contracts.LeadCreated(Guid.NewGuid(), Guid.NewGuid(), "Ayse Yilmaz", "Acme", "web", Guid.NewGuid()));
        data.ToJsonString().ShouldNotContain("Ayse");
        data.ToJsonString().ShouldNotContain("Acme");
        data.Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(["leadId", "ownerUserId", "source"]);
    }

    [Fact]
    public void Serialization_IsDeterministic_AndKeepsTheInsertionOrder()
    {
        var definition = WebhookEventCatalog.Find("lead.created")!;
        var first = WebhookEventCatalog.SampleEnvelope(definition);
        var second = WebhookEventCatalog.SampleEnvelope(definition);
        second.ShouldBe(first);
        first.IndexOf("\"id\"", StringComparison.Ordinal).ShouldBeLessThan(first.IndexOf("\"type\"", StringComparison.Ordinal));
        first.IndexOf("\"tenantId\"", StringComparison.Ordinal).ShouldBeLessThan(first.IndexOf("\"data\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DerivedDealIds_AreDeterministic_DistinctPerType_AndNeverEqualTheSourceEventId()
    {
        var source = Guid.CreateVersion7();
        var won = WebhookEnvelope.DeriveId(source, WebhookEventTypes.DealWon);
        var lost = WebhookEnvelope.DeriveId(source, WebhookEventTypes.DealLost);
        WebhookEnvelope.DeriveId(source, WebhookEventTypes.DealWon).ShouldBe(won);
        won.ShouldNotBe(lost);
        won.ShouldNotBe(source);
        lost.ShouldNotBe(source);
    }

    [Fact]
    public void Amounts_AreJsonNumbers_WithTrailingZerosTrimmed() =>
        WebhookDataMappers.Normalize(1500.5000m).ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("1500.5");

    private static IEnumerable<string> Keys(JsonNode node) =>
        node is JsonObject obj ? obj.SelectMany(p => new[] { p.Key }.Concat(p.Value is null ? [] : Keys(p.Value))) : [];
}

/// <summary>Webhook sırrı şifreleme (AES-256-GCM, AAD bağlı).</summary>
public sealed class WebhookSecretProtectorTests
{
    private static WebhookSecretProtector Create(string keyId = "k1", byte fill = 7, bool devLike = true, params (string Id, byte Fill)[] extra)
    {
        var options = new IntegrationsOptions { DevelopmentLike = devLike, Encryption = { CurrentKeyId = keyId } };
        options.Encryption.Keys[keyId] = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());
        foreach (var (id, f) in extra)
        {
            options.Encryption.Keys[id] = Convert.ToBase64String(Enumerable.Repeat(f, 32).ToArray());
        }

        return new WebhookSecretProtector(Options.Create(options));
    }

    [Fact]
    public void SealOpen_RoundTrips_AndTheCipherContainsNoPlaintext()
    {
        var protector = Create();
        var tenant = Guid.NewGuid();
        var sub = Guid.NewGuid();
        var secret = protector.GenerateSecret();
        var sealedSecret = protector.Seal(secret, tenant, sub, 1);

        secret.ShouldStartWith("whsec_");
        secret.Length.ShouldBe(6 + 43);
        Encoding.UTF8.GetString(sealedSecret.Cipher).ShouldNotContain(secret[6..]);
        sealedSecret.Last4.ShouldBe(secret[^4..]);
        sealedSecret.KeyId.ShouldBe("k1");
        protector.Open(sealedSecret.Cipher, "k1", tenant, sub, 1).ShouldBe(secret);
    }

    [Fact]
    public void Opening_UnderAnotherTenantSubscriptionOrVersion_FailsBecauseTheAadIsBound()
    {
        var protector = Create();
        var tenant = Guid.NewGuid();
        var sub = Guid.NewGuid();
        var sealedSecret = protector.Seal(protector.GenerateSecret(), tenant, sub, 1);

        Should.Throw<CryptographicException>(() => protector.Open(sealedSecret.Cipher, "k1", Guid.NewGuid(), sub, 1));
        Should.Throw<CryptographicException>(() => protector.Open(sealedSecret.Cipher, "k1", tenant, Guid.NewGuid(), 1));
        Should.Throw<CryptographicException>(() => protector.Open(sealedSecret.Cipher, "k1", tenant, sub, 2));
    }

    [Fact]
    public void TamperedBlob_UnknownKeyId_AndWrongKey_AreRejected()
    {
        var protector = Create();
        var tenant = Guid.NewGuid();
        var sub = Guid.NewGuid();
        var sealedSecret = protector.Seal("whsec_abcdefghijklmnop", tenant, sub, 1);
        var tampered = (byte[])sealedSecret.Cipher.Clone();
        tampered[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(() => protector.Open(tampered, "k1", tenant, sub, 1));
        Should.Throw<CryptographicException>(() => protector.Open(sealedSecret.Cipher, "k9", tenant, sub, 1));
        Should.Throw<CryptographicException>(() => Create(fill: 9).Open(sealedSecret.Cipher, "k1", tenant, sub, 1));
        Should.Throw<CryptographicException>(() => protector.Open(new byte[5], "k1", tenant, sub, 1));
    }

    [Fact]
    public void KeyRotation_OldKeyStillOpens_NewSealsUseTheCurrentKeyId()
    {
        var oldProtector = Create("k1", 1);
        var tenant = Guid.NewGuid();
        var sub = Guid.NewGuid();
        var old = oldProtector.Seal("whsec_rotationsecret0001", tenant, sub, 1);

        var rotated = Create("k2", 2, true, ("k1", 1));
        rotated.Open(old.Cipher, "k1", tenant, sub, 1).ShouldBe("whsec_rotationsecret0001");
        rotated.Seal("whsec_rotationsecret0001", tenant, sub, 1).KeyId.ShouldBe("k2");
    }

    [Fact]
    public void OutsideDevelopment_AMissingKey_FailsInsteadOfGeneratingAnEphemeralOne()
    {
        var options = new IntegrationsOptions { DevelopmentLike = false };
        var protector = new WebhookSecretProtector(Options.Create(options));
        Should.Throw<InvalidOperationException>(() => protector.Seal("whsec_x1234567890", Guid.NewGuid(), Guid.NewGuid(), 1));
        IntegrationsOptionsValidator.Validate(options).ShouldBeEmpty("key presence is enforced by the dedicated startup validator");
    }

    [Fact]
    public void RandomSecrets_AreUnique() =>
        Enumerable.Range(0, 50).Select(_ => Create().GenerateSecret()).Distinct().Count().ShouldBe(50);
}

/// <summary>Yapılandırma doğrulaması: tutarsız aralık ve eksik egress açılışta reddedilir.</summary>
public sealed class IntegrationsOptionsValidationTests
{
    [Fact]
    public void Defaults_AreValid_AndWebhooksAreOffByDefault()
    {
        var options = new IntegrationsOptions();
        IntegrationsOptionsValidator.Validate(options).ShouldBeEmpty();
        options.Webhooks.Enabled.ShouldBeFalse();
        options.Webhooks.EffectiveBackoff.ShouldBe([10, 60, 300, 1800, 7200, 21600, 43200]);
        options.Webhooks.EffectivePorts.ShouldBe([443, 8443]);
    }

    [Fact]
    public void EnabledInProduction_WithoutEgressProxyAndDns_IsRejected()
    {
        var options = new IntegrationsOptions { DevelopmentLike = false, Webhooks = { Enabled = true } };
        IntegrationsOptionsValidator.Validate(options).ShouldNotBeEmpty();
        options.Webhooks.EgressProxy = "http://10.213.77.54:3128";
        options.Webhooks.DnsServer = "10.213.77.53";
        IntegrationsOptionsValidator.Validate(options).ShouldBeEmpty();
        new IntegrationsOptions { DevelopmentLike = true, Webhooks = { Enabled = true } }.Let(o => IntegrationsOptionsValidator.Validate(o).ShouldBeEmpty());
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(31, 5)]
    [InlineData(10, 20)]
    public void InconsistentTimeouts_AreRejected(int timeout, int connect)
    {
        var options = new IntegrationsOptions { Webhooks = { TimeoutSeconds = timeout, ConnectTimeoutSeconds = connect } };
        IntegrationsOptionsValidator.Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void InvalidPrivateCidr_MaxLifetimeBelowDefault_AndTooFewBackoffSteps_AreRejected()
    {
        IntegrationsOptionsValidator.Validate(new IntegrationsOptions { Webhooks = { AllowedPrivateCidrs = ["10.0.0.0/99"] } }).ShouldNotBeEmpty();
        IntegrationsOptionsValidator.Validate(new IntegrationsOptions { ApiKeys = { DefaultLifetimeDays = 365, MaxLifetimeDays = 30 } }).ShouldNotBeEmpty();
        IntegrationsOptionsValidator.Validate(new IntegrationsOptions { Webhooks = { MaxAttempts = 8, BackoffSeconds = [10, 60] } }).ShouldNotBeEmpty();
    }
}

internal static class TestExtensions
{
    public static void Let<T>(this T value, Action<T> action) => action(value);
}

/// <summary>DNS ileti kodlaması/çözümlemesi (asgari UDP istemcisi): A/AAAA, sıkıştırma göstergesi, kimlik uyuşmazlığı, hatalı ileti.</summary>
public sealed class DnsMessageTests
{
    [Fact]
    public void Query_HasTheExpectedWireFormat()
    {
        var query = WebhookDnsResolver.BuildQuery(0x1234, "hooks.example.com", 1);
        query[0].ShouldBe((byte)0x12);
        query[1].ShouldBe((byte)0x34);
        query[2].ShouldBe((byte)0x01);
        query[5].ShouldBe((byte)0x01);
        query[12].ShouldBe((byte)5);
        Encoding.ASCII.GetString(query, 13, 5).ShouldBe("hooks");
        query[^4].ShouldBe((byte)0);
        query[^3].ShouldBe((byte)1);
    }

    [Fact]
    public void Response_WithCompressedNames_YieldsARecords()
    {
        var query = WebhookDnsResolver.BuildQuery(0x0102, "a.example.com", 1);
        var response = new List<byte>(query);
        response[2] = 0x81;
        response[3] = 0x80;
        response[7] = 2; // ancount
        foreach (var address in new[] { new byte[] { 93, 184, 216, 34 }, new byte[] { 1, 2, 3, 4 } })
        {
            response.AddRange([0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04]);
            response.AddRange(address);
        }

        var (addresses, truncated) = WebhookDnsResolver.Parse([.. response], 0x0102, 1);
        truncated.ShouldBeFalse();
        addresses.Select(a => a.ToString()).ShouldBe(["93.184.216.34", "1.2.3.4"]);
    }

    [Fact]
    public void IdMismatch_AndTruncatedMessages_AreRejected()
    {
        var query = WebhookDnsResolver.BuildQuery(0x0102, "a.example.com", 1);
        Should.Throw<InvalidDataException>(() => WebhookDnsResolver.Parse(query, 0x9999, 1));
        Should.Throw<InvalidDataException>(() => WebhookDnsResolver.Parse(query[..5], 0x0102, 1));
        var bad = query.ToArray();
        bad[7] = 1;
        Should.Throw<InvalidDataException>(() => WebhookDnsResolver.Parse(bad, 0x0102, 1));
    }
}

/// <summary>OpenAPI süzgeci: yalnız katalog yolları, plan modül süzgeci, kapsam/güvenlik eklemeleri, şema budama.</summary>
public sealed class OpenApiFilterTests
{
    private const string Doc = """
        {"openapi":"3.1.1","info":{"title":"x","version":"1"},
         "paths":{
          "/api/v1/accounts":{"get":{"responses":{"200":{"content":{"application/json":{"schema":{"$ref":"#/components/schemas/AccountList"}}}}}},"post":{"responses":{"201":{}}}},
          "/api/v1/accounts/{id}/contacts":{"get":{"responses":{"200":{}}}},
          "/api/v1/quotes":{"get":{"responses":{"200":{}}}},
          "/api/v1/quotes/{id}/convert":{"post":{"responses":{"200":{}}}},
          "/api/v1/cases":{"get":{"responses":{"200":{}}}},
          "/api/v1/pipelines":{"get":{"responses":{"200":{}}},"post":{"responses":{"201":{}}}},
          "/api/v1/reports/sales/summary":{"get":{"responses":{"200":{}}}},
          "/api/v1/reports/commerce/x":{"get":{"responses":{"200":{}}}},
          "/api/v1/organization/members":{"get":{"responses":{"200":{}}}},
          "/api/v1/platform/organizations":{"get":{"responses":{"200":{}}}},
          "/api/v1/integrations/api-keys":{"get":{"responses":{"200":{}}}},
          "/api/v1/auth/login":{"post":{"responses":{"200":{}}}},
          "/api/v1/me":{"get":{"responses":{"200":{}}}},
          "/api/v1/service/sla-policies":{"get":{"responses":{"200":{}}}},
          "/api/v1/workflows/rules":{"get":{"responses":{"200":{}}}},
          "/api/v1/approvals":{"get":{"responses":{"200":{}}}}
         },
         "components":{"schemas":{"AccountList":{"type":"object","properties":{"items":{"type":"array","items":{"$ref":"#/components/schemas/Account"}}}},"Account":{"type":"object"},"Unused":{"type":"object"}}}}
        """;

    [Fact]
    public void OnlyCatalogPaths_SurviveWithTheirScopes_AndManagementPlanePathsAreGone()
    {
        var doc = JsonNode.Parse(OpenApiDocumentFilter.Filter(Doc, _ => true))!;
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        paths.ShouldBe(
        [
            "/api/v1/accounts", "/api/v1/accounts/{id}/contacts", "/api/v1/cases", "/api/v1/pipelines", "/api/v1/quotes", "/api/v1/quotes/{id}/convert",
            "/api/v1/reports/commerce/x", "/api/v1/reports/sales/summary",
        ]);
        doc["paths"]!["/api/v1/accounts"]!["get"]!["x-required-scope"]!.GetValue<string>().ShouldBe("crm.accounts.read");
        doc["paths"]!["/api/v1/accounts"]!["post"]!["x-required-scope"]!.GetValue<string>().ShouldBe("crm.accounts.write");
        doc["paths"]!["/api/v1/accounts/{id}/contacts"]!["get"]!["x-required-scopes"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["crm.accounts.read", "crm.contacts.read"]);
        doc["paths"]!["/api/v1/quotes/{id}/convert"]!["post"]!["x-required-scopes"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["crm.quotes.read", "crm.orders.write"]);
        doc["paths"]!["/api/v1/reports/sales/summary"]!["get"]!["x-required-scope"]!.GetValue<string>().ShouldBe("crm.reports.read");
        doc["paths"]!["/api/v1/pipelines"]!.AsObject().ContainsKey("post").ShouldBeFalse("pipeline writes need org.settings.manage and are not documented");
        JsonSerializer.Serialize(doc).ShouldNotContain("organization");
    }

    [Fact]
    public void DisabledModules_RemoveTheirPaths()
    {
        var doc = JsonNode.Parse(OpenApiDocumentFilter.Filter(Doc, m => m is not ("commerce" or "service")))!;
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();
        paths.ShouldNotContain("/api/v1/quotes");
        paths.ShouldNotContain("/api/v1/cases");
        paths.ShouldNotContain("/api/v1/reports/commerce/x");
        paths.ShouldContain("/api/v1/accounts");
    }

    [Fact]
    public void EveryOperation_DeclaresTheApiKeySecurityScheme_AndUnreferencedSchemasArePruned()
    {
        var doc = JsonNode.Parse(OpenApiDocumentFilter.Filter(Doc, _ => true))!;
        foreach (var (_, item) in doc["paths"]!.AsObject())
        {
            foreach (var (_, operation) in item!.AsObject())
            {
                operation!["security"]![0]!["bearerApiKey"].ShouldNotBeNull();
                operation["x-required-scope"].ShouldNotBeNull();
            }
        }

        var scheme = doc["components"]!["securitySchemes"]!["bearerApiKey"]!;
        scheme["type"]!.GetValue<string>().ShouldBe("http");
        scheme["scheme"]!.GetValue<string>().ShouldBe("bearer");
        scheme["bearerFormat"]!.GetValue<string>().ShouldBe("crmk");
        var schemas = doc["components"]!["schemas"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        schemas.ShouldBe(["Account", "AccountList"]);
        doc["info"]!["description"]!.GetValue<string>().ShouldContain("pageSize");
    }
}
