using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sense.Crm.Modules.Integrations.Contracts;
using Sense.Crm.Modules.Integrations.Infrastructure;
using Sense.Crm.Modules.Integrations.Infrastructure.Delivery;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Integrations.Tests.Api.Kit;
using static Sense.Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Sense.Crm.Modules.Integrations.Tests.Api;

internal sealed record Hook(Tenant Tenant, Guid Id, string Secret);

/// <summary>Teslimat hattı (fan-out → Worker dispatcher çekirdeği): imza, yeniden deneme, ölü mektup, SSRF, askı, sağlık, adalet.</summary>
[Collection(ApiCollection.Name)]
public sealed class DeliveryPipelineTests(CrmApiFactory factory)
{
    private static readonly IPAddress PublicIp = IPAddress.Parse("93.184.216.34");

    private async Task<Hook> NewHookAsync(TestHost host, string name, string[]? events = null, Tenant? tenant = null, string url = "https://hooks.example.com/hook?token=abc")
    {
        var t = tenant ?? await host.Factory.NewTenantAsync(name + " " + Guid.NewGuid().ToString("N")[..6]);
        await host.Factory.DrainOutboxesAsync();
        var body = (await t.Admin.SendAsync(HttpMethod.Post, Wh, new { name = "hook-" + Guid.NewGuid().ToString("N")[..5], url, eventTypes = events ?? ["lead.created"] })).Body;
        return new Hook(t, body.GuidProp("id"), body.Str("secret"));
    }

    private async Task<List<(Guid Id, string Status, int Attempts, string? Reason, string Kind, DateTime? Next)>> DeliveriesAsync(Guid subscriptionId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT id, status, attempts, failure_reason, kind, next_attempt_at FROM integrations.webhook_deliveries WHERE subscription_id = @s ORDER BY created_at, id", connection);
        command.Parameters.AddWithValue("s", subscriptionId);
        var rows = new List<(Guid, string, int, string?, string, DateTime?)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5)));
        }

        return rows;
    }

    private Task<int> QueueCountAsync(Guid subscriptionId) =>
        factory.ScalarAsync<int>("SELECT count(*) FROM integrations.delivery_queue q JOIN integrations.webhook_deliveries d ON d.id = q.delivery_id WHERE d.subscription_id = @s", ("s", subscriptionId));

    private async Task<Hook> PendingLeadDeliveryAsync(TestHost host, string name, string[]? events = null)
    {
        var hook = await NewHookAsync(host, name, events);
        await hook.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        return hook;
    }

    [Fact]
    public async Task LeadCreated_IsFannedOut_AndDeliveredWithAVerifiableSignature_ToThePinnedIp()
    {
        await using var host = TestHost.Create(factory);
        var hook = await PendingLeadDeliveryAsync(host, "Imza");
        var rows = await DeliveriesAsync(hook.Id);
        rows.Count.ShouldBe(1);
        rows[0].Status.ShouldBe("pending");
        (await QueueCountAsync(hook.Id)).ShouldBe(1);

        (await host.RunDispatcherAsync()).ShouldBe(1);

        var call = host.Transport.Calls.Single();
        call.PinnedAddress.ShouldBe(PublicIp);
        call.Url.Host.ShouldBe("hooks.example.com");
        call.Url.PathAndQuery.ShouldBe("/hook?token=abc");
        var body = Encoding.UTF8.GetString(call.Body);
        using var envelope = JsonDocument.Parse(body);
        envelope.RootElement.Str("type").ShouldBe("lead.created");
        envelope.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
        envelope.RootElement.GuidProp("tenantId").ShouldBe(hook.Tenant.TenantId);
        envelope.RootElement.GuidProp("actorUserId").ShouldBe(hook.Tenant.AdminUserId);
        envelope.RootElement.GetProperty("data").GetProperty("source").GetString().ShouldNotBeNull();
        envelope.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["leadId", "ownerUserId", "source"]);

        // Basliklar ve imza.
        call.Headers["X-Crm-Event-Id"].ShouldBe(envelope.RootElement.Str("id"));
        call.Headers["X-Crm-Event-Type"].ShouldBe("lead.created");
        call.Headers["X-Crm-Delivery-Attempt"].ShouldBe("1");
        call.Headers["User-Agent"].ShouldBe("SenseCRM-Webhooks/1.0");
        WebhookSignature.Verify(hook.Secret, call.Headers["X-Crm-Signature"], call.Body, host.Clock.GetUtcNow(), TimeSpan.FromSeconds(300)).ShouldBeTrue();
        WebhookSignature.Verify("whsec_wrong", call.Headers["X-Crm-Signature"], call.Body, host.Clock.GetUtcNow(), TimeSpan.FromSeconds(300)).ShouldBeFalse();

        // Saklanan payload imzalanan bayt dizisiyle ayni; teslimat tamamlandi, kuyruk bos, sayac sifir.
        (await factory.ScalarAsync<string>("SELECT payload FROM integrations.webhook_deliveries WHERE id = @id", ("id", rows[0].Id))).ShouldBe(body);
        var after = (await DeliveriesAsync(hook.Id)).Single();
        after.Status.ShouldBe("succeeded");
        after.Attempts.ShouldBe(1);
        (await QueueCountAsync(hook.Id)).ShouldBe(0);
        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.webhook_delivery_attempts WHERE delivery_id = @id", ("id", rows[0].Id))).ShouldBe(1);
        (await factory.ScalarAsync<int>("SELECT consecutive_failures FROM integrations.webhook_subscriptions WHERE id = @id", ("id", hook.Id))).ShouldBe(0);

        // Yonetim gorunumu: sorgu dizgisi hicbir yerde yok, imza [redacted].
        var detail = await hook.Tenant.Admin.GetJsonAsync($"{Deliveries}/{rows[0].Id}");
        detail.GetProperty("requestHeaders").GetProperty("X-Crm-Signature").GetString().ShouldBe("[redacted]");
        detail.Str("host").ShouldBe("hooks.example.com");
        JsonSerializer.Serialize(detail).ShouldNotContain("token=abc");
        JsonSerializer.Serialize(detail).ShouldNotContain(hook.Secret);
        detail.GetProperty("attemptLog").GetArrayLength().ShouldBe(1);
        detail.GetProperty("payload").GetProperty("type").GetString().ShouldBe("lead.created");
    }

    [Fact]
    public async Task FanOut_IsIdempotentPerEvent_FiltersByType_SkipsDisabled_AndNeverCrossesTenants()
    {
        await using var host = TestHost.Create(factory);
        var a = await NewHookAsync(host, "Ayni A", ["lead.created"]);
        var otherType = await NewHookAsync(host, "Ayni Tur", ["deal.won"], a.Tenant);
        var disabled = await NewHookAsync(host, "Ayni Pasif", ["lead.created"], a.Tenant);
        await a.Tenant.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{disabled.Id}/disable", null, HttpStatusCode.NoContent);
        var b = await NewHookAsync(host, "Ayni B", ["lead.created"]);

        await a.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();

        (await DeliveriesAsync(a.Id)).Count.ShouldBe(1);
        (await DeliveriesAsync(otherType.Id)).ShouldBeEmpty();
        (await DeliveriesAsync(disabled.Id)).ShouldBeEmpty();
        (await DeliveriesAsync(b.Id)).ShouldBeEmpty("tenant B's subscription must never receive tenant A's event");

        // Outbox yeniden denemesi (ayni olay tekrar isleniyor): tek teslimat.
        await factory.SqlAsync("UPDATE sales.outbox_messages SET processed_at = NULL, attempts = 0 WHERE tenant_id = @t AND type ILIKE '%LeadCreated%'", ("t", a.Tenant.TenantId));
        await host.Factory.DrainOutboxesAsync();
        (await DeliveriesAsync(a.Id)).Count.ShouldBe(1);
        (await factory.ScalarAsync<int>("SELECT count(*) FROM integrations.delivery_queue WHERE tenant_id = @t", ("t", a.Tenant.TenantId))).ShouldBe(1);

        // Pasif abonelik: bekleyen teslimat surer (yalniz yeni fan-out durur).
        var pending = await NewHookAsync(host, "Ayni Bekleyen", ["lead.created"], a.Tenant);
        await a.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        await a.Tenant.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{pending.Id}/disable", null, HttpStatusCode.NoContent);
        await host.RunDispatcherAsync();
        (await DeliveriesAsync(pending.Id)).Single().Status.ShouldBe("succeeded");
        (await DeliveriesAsync(b.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RetrySchedule_IsExact_ThenTheDeliveryDeadLetters_AndCanBeRedelivered()
    {
        await using var host = TestHost.Create(factory);
        host.Transport.Responder = _ => FakeTransport.Ok(500);
        var hook = await PendingLeadDeliveryAsync(host, "Yeniden Deneme");
        var deliveryId = (await DeliveriesAsync(hook.Id)).Single().Id;
        var expected = new[] { 10, 60, 300, 1800, 7200, 21600, 43200 };

        for (var attempt = 1; attempt <= 7; attempt++)
        {
            (await host.RunDispatcherAsync()).ShouldBe(1, $"attempt {attempt}");
            var row = (await DeliveriesAsync(hook.Id)).Single();
            row.Status.ShouldBe("pending");
            row.Attempts.ShouldBe(attempt);
            row.Next!.Value.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddSeconds(expected[attempt - 1]), TimeSpan.FromMilliseconds(2), $"attempt {attempt}");

            // Vadesi gelmeden tekrar talep edilmez.
            (await host.RunDispatcherAsync()).ShouldBe(0);
            host.Clock.Advance(TimeSpan.FromSeconds(expected[attempt - 1]));
        }

        (await host.RunDispatcherAsync()).ShouldBe(1);
        var dead = (await DeliveriesAsync(hook.Id)).Single();
        dead.Status.ShouldBe("failed");
        dead.Attempts.ShouldBe(8);
        dead.Reason.ShouldBe("retries_exhausted");
        (await QueueCountAsync(hook.Id)).ShouldBe(0);
        host.Transport.Calls.Count.ShouldBe(8);
        host.Transport.Calls.Select(c => c.Headers["X-Crm-Delivery-Attempt"]).ShouldBe(["1", "2", "3", "4", "5", "6", "7", "8"]);

        // Her denemede yeni t (imza degisir), govde bayt bayt ayni.
        host.Transport.Calls.Select(c => Encoding.UTF8.GetString(c.Body)).Distinct().Count().ShouldBe(1);
        host.Transport.Calls.Select(c => c.Headers["X-Crm-Signature"]).Distinct().Count().ShouldBe(8);

        // Elle yeniden gonder: yeni satir (redelivery), ayni event id; basari.
        host.Transport.Responder = _ => FakeTransport.Ok();
        var redeliver = await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Deliveries}/{deliveryId}/redeliver");
        redeliver.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await host.RunDispatcherAsync()).ShouldBe(1);
        var rows = await DeliveriesAsync(hook.Id);
        rows.Count.ShouldBe(2);
        rows[1].Kind.ShouldBe("redelivery");
        rows[1].Status.ShouldBe("succeeded");
        host.Transport.Calls[^1].Headers["X-Crm-Event-Id"].ShouldBe(host.Transport.Calls[0].Headers["X-Crm-Event-Id"]);
        Encoding.UTF8.GetString(host.Transport.Calls[^1].Body).ShouldBe(Encoding.UTF8.GetString(host.Transport.Calls[0].Body));

        // Tamamlanmamis teslimat yeniden gonderilemez.
        host.Transport.Responder = _ => FakeTransport.Ok(500);
        await hook.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();
        var pending = (await DeliveriesAsync(hook.Id)).Last(r => r.Kind == "event");
        pending.Status.ShouldBe("pending");
        await (await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Deliveries}/{pending.Id}/redeliver")).Response.ShouldBeCodeAsync(HttpStatusCode.Conflict, "delivery.not_redeliverable");
    }

    [Fact]
    public async Task RetryAfter_ExtendsButNeverShortensTheDelay_AndIsCappedAtOneHour()
    {
        await using var host = TestHost.Create(factory);
        var hook = await PendingLeadDeliveryAsync(host, "Retry After");
        host.Transport.Responder = _ => new TransportResult(TransportStatus.Ok, 429, [], TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(3), null);
        await host.RunDispatcherAsync();
        (await DeliveriesAsync(hook.Id)).Single().Next!.Value.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddSeconds(120), TimeSpan.FromMilliseconds(2));

        host.Clock.Advance(TimeSpan.FromSeconds(120));
        host.Transport.Responder = _ => new TransportResult(TransportStatus.Ok, 503, [], TimeSpan.FromDays(2), TimeSpan.FromMilliseconds(3), null);
        await host.RunDispatcherAsync();
        (await DeliveriesAsync(hook.Id)).Single().Next!.Value.ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddSeconds(3600), TimeSpan.FromMilliseconds(2));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task TerminalResponses_FailOnASingleAttempt_AndRedirectsAreNeverFollowed(int status)
    {
        await using var host = TestHost.Create(factory);
        host.Transport.Responder = _ => FakeTransport.Ok(status);
        var hook = await PendingLeadDeliveryAsync(host, "Terminal " + status);
        await host.RunDispatcherAsync();

        var row = (await DeliveriesAsync(hook.Id)).Single();
        row.Status.ShouldBe("failed");
        row.Attempts.ShouldBe(1);
        row.Reason.ShouldBe(status is >= 300 and < 400 ? "redirect" : "http_error");
        host.Transport.Calls.Count.ShouldBe(1);
        (await QueueCountAsync(hook.Id)).ShouldBe(0);
    }

    [Theory]
    [InlineData(TransportStatus.Timeout, "timeout", "pending")]
    [InlineData(TransportStatus.ConnectionError, "connection_error", "pending")]
    [InlineData(TransportStatus.TlsError, "tls_error", "failed")]
    [InlineData(TransportStatus.Blocked, "blocked_destination", "failed")]
    public async Task TransportFailures_AreClassified(TransportStatus transportStatus, string reason, string expectedStatus)
    {
        await using var host = TestHost.Create(factory);
        host.Transport.Responder = _ => new TransportResult(transportStatus, null, [], null, TimeSpan.FromSeconds(1), reason);
        var hook = await PendingLeadDeliveryAsync(host, "Tasiyici " + reason);
        await host.RunDispatcherAsync();
        var row = (await DeliveriesAsync(hook.Id)).Single();
        row.Status.ShouldBe(expectedStatus);
        row.Reason.ShouldBe(reason);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2002:7f00:1::")]
    public async Task ResolvedInternalAddresses_AreBlocked_AndNoRequestIsEverSent(string address)
    {
        await using var host = TestHost.Create(factory, ("Integrations:Webhooks:DevAllowLoopback", "false"));
        host.Dns.Default = [IPAddress.Parse(address)];
        var hook = await PendingLeadDeliveryAsync(host, "Ssrf Dns");
        await host.RunDispatcherAsync();

        host.Transport.Calls.ShouldBeEmpty("a blocked destination must never be contacted");
        var row = (await DeliveriesAsync(hook.Id)).Single();
        row.Status.ShouldBe("failed");
        row.Reason.ShouldBe("blocked_destination");
        row.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task MixedRecords_AreRejectedEntirely_AndRebindingCannotSwapTheAddressAfterValidation()
    {
        await using var host = TestHost.Create(factory, ("Integrations:Webhooks:DevAllowLoopback", "false"));
        host.Dns.Default = [PublicIp, IPAddress.Parse("10.0.0.7")];
        var hook = await PendingLeadDeliveryAsync(host, "Karisik Kayit");
        await host.RunDispatcherAsync();
        host.Transport.Calls.ShouldBeEmpty("one blocked address blocks the whole record set");
        (await DeliveriesAsync(hook.Id)).Single().Reason.ShouldBe("blocked_destination");

        // Rebinding: 1. cevap genel IP, 2. cevap loopback; teslimat TEK cozumleme yapar ve dogrulanan IP'ye sabitlenir.
        var second = await PendingLeadDeliveryAsync(host, "Rebinding");
        host.Dns.Default = [PublicIp];
        host.Dns.Enqueue("hooks.example.com", PublicIp);
        host.Dns.Enqueue("hooks.example.com", IPAddress.Parse("127.0.0.1"));
        var callsBefore = host.Dns.Calls;
        await host.RunDispatcherAsync();
        (host.Dns.Calls - callsBefore).ShouldBe(1, "exactly one resolution per attempt");
        host.Transport.Calls.Single().PinnedAddress.ShouldBe(PublicIp);
        (await DeliveriesAsync(second.Id)).Single().Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task DnsFailures_AreRetriedThreeTimes_ThenTerminal()
    {
        await using var host = TestHost.Create(factory);
        host.Dns.Default = [];
        var hook = await PendingLeadDeliveryAsync(host, "Dns Hata");
        for (var i = 0; i < 3; i++)
        {
            await host.RunDispatcherAsync();
            host.Clock.Advance(TimeSpan.FromMinutes(6));
        }

        var row = (await DeliveriesAsync(hook.Id)).Single();
        row.Status.ShouldBe("failed");
        row.Reason.ShouldBe("dns_error");
        row.Attempts.ShouldBe(3);
        host.Transport.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConsecutiveFailures_AutoDisableTheSubscription_SuccessResets_PingsDoNotCount_ManualEnableResets()
    {
        await using var host = TestHost.Create(factory);
        host.Transport.Responder = _ => FakeTransport.Ok(404);
        var hook = await NewHookAsync(host, "Otomatik Pasif");
        var admin = hook.Tenant.Admin;

        for (var i = 0; i < 4; i++)
        {
            await admin.CreateLeadAsync();
        }

        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();
        (await factory.ScalarAsync<int>("SELECT consecutive_failures FROM integrations.webhook_subscriptions WHERE id = @id", ("id", hook.Id))).ShouldBe(4);

        // Ping sayaci etkilemez.
        var ping = await admin.SendAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/test");
        ping.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await host.RunDispatcherAsync();
        (await factory.ScalarAsync<int>("SELECT consecutive_failures FROM integrations.webhook_subscriptions WHERE id = @id", ("id", hook.Id))).ShouldBe(4);
        (await DeliveriesAsync(hook.Id)).Last().Kind.ShouldBe("ping");

        // Basari sayaci sifirlar.
        host.Transport.Responder = _ => FakeTransport.Ok();
        await admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();
        (await factory.ScalarAsync<int>("SELECT consecutive_failures FROM integrations.webhook_subscriptions WHERE id = @id", ("id", hook.Id))).ShouldBe(0);

        // 10 ardisik terminal hata -> pasif (failing).
        host.Transport.Responder = _ => FakeTransport.Ok(404);
        for (var i = 0; i < 10; i++)
        {
            await admin.CreateLeadAsync();
        }

        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();
        var got = await admin.GetJsonAsync($"{Wh}/{hook.Id}");
        got.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        got.Str("disabledReason").ShouldBe("failing");
        got.Str("health").ShouldBe("disabled");
        got.GetProperty("consecutiveFailures").GetInt32().ShouldBe(10);
        (await factory.ScalarAsync<int>("SELECT count(*) FROM audit.audit_log_entries WHERE tenant_id = @t AND entity_id = @id AND action = 'updated'", ("t", hook.Tenant.TenantId), ("id", hook.Id.ToString()))).ShouldBeGreaterThanOrEqualTo(1);

        // Yeni fan-out durur; elle etkinlestirme sayaci sifirlar.
        var before = (await DeliveriesAsync(hook.Id)).Count;
        await admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        (await DeliveriesAsync(hook.Id)).Count.ShouldBe(before);
        await admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/enable", null, HttpStatusCode.NoContent);
        var enabled = await admin.GetJsonAsync($"{Wh}/{hook.Id}");
        enabled.GetProperty("consecutiveFailures").GetInt32().ShouldBe(0);
        enabled.TryGetProperty("disabledReason", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SuspendedOrPlanBlockedTenants_GetNoAttempts_RowsWait_AndResumeAfterwards_NewEventsAreSkipped()
    {
        await using var host = TestHost.Create(factory);
        var hook = await PendingLeadDeliveryAsync(host, "Aski Teslim");
        var platform = await host.Factory.PlatformAdminAsync();
        await platform.SendOkAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{hook.Tenant.TenantId}/suspend", new { reason = "test", mode = "readOnly" }, HttpStatusCode.NoContent);

        await host.RunDispatcherAsync();
        host.Transport.Calls.ShouldBeEmpty("a suspended tenant must not deliver");
        var waiting = (await DeliveriesAsync(hook.Id)).Single();
        waiting.Status.ShouldBe("pending");
        (await factory.ScalarAsync<DateTime>("SELECT due_at FROM integrations.delivery_queue WHERE delivery_id = @id", ("id", waiting.Id))).ShouldBe(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(5), TimeSpan.FromSeconds(1));

        // Askidan cikinca (satir bekliyordu) teslim edilir.
        await platform.SendOkAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{hook.Tenant.TenantId}/reactivate", null, HttpStatusCode.NoContent);
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        await host.RunDispatcherAsync();
        host.Transport.Calls.Count.ShouldBe(1);
        (await DeliveriesAsync(hook.Id)).Single().Status.ShouldBe("succeeded");
    }

    [Fact]
    public async Task EventsProducedWhileSuspended_AreNeverFannedOut()
    {
        await using var host = TestHost.Create(factory);
        var hook = await NewHookAsync(host, "Aski Fanout");
        var platform = await host.Factory.PlatformAdminAsync();
        await hook.Tenant.Admin.CreateLeadAsync();
        await platform.SendOkAsync(HttpMethod.Post, $"{PlatformBase}/organizations/{hook.Tenant.TenantId}/suspend", new { reason = "test", mode = "readOnly" }, HttpStatusCode.NoContent);
        await host.Factory.DrainOutboxesAsync();
        (await DeliveriesAsync(hook.Id)).ShouldBeEmpty("the event handler is skipped for suspended tenants (M7 behaviour)");
    }

    [Fact]
    public async Task ModuleDisabledPlan_StopsDelivery_UntilTheModuleReturns()
    {
        await using var host = TestHost.Create(factory);
        await factory.EnsurePlanAsync("m8b_off2", """{"maxUsers":null,"maxRecords":{}}""", """{"workflows":true,"commerce":true,"service":true,"marketing":true,"integrations":false}""");
        var hook = await PendingLeadDeliveryAsync(host, "Plan Teslim");
        var platform = await host.Factory.PlatformAdminAsync();
        await platform.PutSubscriptionAsync(hook.Tenant.TenantId, "m8b_off2");
        await host.RunDispatcherAsync();
        host.Transport.Calls.ShouldBeEmpty();
        await platform.PutSubscriptionAsync(hook.Tenant.TenantId, "internal");
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        await host.RunDispatcherAsync();
        host.Transport.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Ping_UsesTheSamePipeline_WorksOnDisabledSubscriptions_AndIsRateLimited()
    {
        await using var host = TestHost.Create(factory);
        var hook = await NewHookAsync(host, "Ping");
        await hook.Tenant.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/disable", null, HttpStatusCode.NoContent);

        var accepted = await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/test");
        accepted.Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var deliveryId = accepted.Body.GuidProp("deliveryId");
        await host.RunDispatcherAsync();

        var call = host.Transport.Calls.Single();
        using var envelope = JsonDocument.Parse(call.Body);
        envelope.RootElement.Str("type").ShouldBe("ping");
        envelope.RootElement.GetProperty("data").GuidProp("subscriptionId").ShouldBe(hook.Id);
        WebhookSignature.Verify(hook.Secret, call.Headers["X-Crm-Signature"], call.Body, host.Clock.GetUtcNow(), TimeSpan.FromSeconds(300)).ShouldBeTrue();
        (await hook.Tenant.Admin.GetJsonAsync($"{Deliveries}/{deliveryId}")).Str("kind").ShouldBe("ping");

        for (var i = 0; i < 4; i++)
        {
            (await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/test")).Response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var limited = await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/test");
        await limited.Response.ShouldBeCodeAsync(HttpStatusCode.TooManyRequests, "general.rate_limit_exceeded");
    }

    [Fact]
    public async Task Redeliver_RequiresAnEnabledSubscription_AndTheSameEventId_DisabledIs409()
    {
        await using var host = TestHost.Create(factory);
        var hook = await PendingLeadDeliveryAsync(host, "Yeniden Gonder");
        await host.RunDispatcherAsync();
        var id = (await DeliveriesAsync(hook.Id)).Single().Id;
        await hook.Tenant.Admin.SendOkAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/disable", null, HttpStatusCode.NoContent);
        await (await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Deliveries}/{id}/redeliver")).Response.ShouldBeCodeAsync(HttpStatusCode.Conflict, "webhook.disabled");
    }

    [Fact]
    public async Task SecretRotation_SignsWithBothSecretsDuringGrace_ThenOnlyTheNewOne()
    {
        await using var host = TestHost.Create(factory);
        var hook = await NewHookAsync(host, "Rotasyon");
        var rotated = (await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/rotate-secret", new { graceHours = 24 })).Body.Str("secret");

        await hook.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();
        var call = host.Transport.Calls.Single();
        var header = call.Headers["X-Crm-Signature"];
        header.Split(',').Count(p => p.StartsWith("v1=", StringComparison.Ordinal)).ShouldBe(2);
        header.IndexOf(WebhookSignature.ComputeHex(rotated, long.Parse(header.Split(',')[0][2..]), call.Body), StringComparison.Ordinal).ShouldBeLessThan(header.LastIndexOf("v1=", StringComparison.Ordinal), "new secret first");
        var now = host.Clock.GetUtcNow();
        WebhookSignature.Verify(rotated, header, call.Body, now, TimeSpan.FromSeconds(300)).ShouldBeTrue();
        WebhookSignature.Verify(hook.Secret, header, call.Body, now, TimeSpan.FromSeconds(300)).ShouldBeTrue("old secret still valid during the grace period");

        // Grace bitti: tek v1 ve previous_* temizlenir (saklama isi).
        host.Clock.Advance(TimeSpan.FromHours(25));
        await hook.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();
        var later = host.Transport.Calls.Last();
        later.Headers["X-Crm-Signature"].Split(',').Count(p => p.StartsWith("v1=", StringComparison.Ordinal)).ShouldBe(1);
        WebhookSignature.Verify(hook.Secret, later.Headers["X-Crm-Signature"], later.Body, host.Clock.GetUtcNow(), TimeSpan.FromSeconds(300)).ShouldBeFalse();

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IntegrationsRetention>().RunAsync(Ct);
        }

        (await factory.ScalarAsync<bool>("SELECT previous_secret_enc IS NULL AND previous_secret_expires_at IS NULL FROM integrations.webhook_subscriptions WHERE id = @id", ("id", hook.Id))).ShouldBeTrue();

        // graceHours = 0: hemen tek v1.
        var zero = (await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/rotate-secret", new { graceHours = 0 })).Body.Str("secret");
        await hook.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();
        var last = host.Transport.Calls.Last();
        last.Headers["X-Crm-Signature"].Split(',').Count(p => p.StartsWith("v1=", StringComparison.Ordinal)).ShouldBe(1);
        WebhookSignature.Verify(zero, last.Headers["X-Crm-Signature"], last.Body, host.Clock.GetUtcNow(), TimeSpan.FromSeconds(300)).ShouldBeTrue();
    }

    [Fact]
    public async Task OldEvents_AreFailedWithoutAnAttempt_AfterSeventyTwoHours()
    {
        await using var host = TestHost.Create(factory);
        var hook = await PendingLeadDeliveryAsync(host, "Eski Olay");
        host.Clock.Advance(TimeSpan.FromHours(73));
        await host.RunDispatcherAsync();
        var row = (await DeliveriesAsync(hook.Id)).Single();
        row.Status.ShouldBe("failed");
        row.Reason.ShouldBe("expired");
        row.Attempts.ShouldBe(0);
        host.Transport.Calls.ShouldBeEmpty();
        (await QueueCountAsync(hook.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task ResponseSnippet_IsSanitised_AndStoredInTheAttemptLog()
    {
        await using var host = TestHost.Create(factory);
        host.Transport.Responder = _ => FakeTransport.Ok(500, Encoding.UTF8.GetBytes("{\"error\":\"boom\",\"token\":\"LEAKME123\",\"echo\":\"whsec_ABCDEFGH1234\"}"));
        var hook = await PendingLeadDeliveryAsync(host, "Yanit Ozeti");
        await host.RunDispatcherAsync();
        var detail = await hook.Tenant.Admin.GetJsonAsync($"{Deliveries}/{(await DeliveriesAsync(hook.Id)).Single().Id}");
        var snippet = detail.GetProperty("attemptLog")[0].Str("responseSnippet");
        snippet.ShouldContain("boom");
        snippet.ShouldNotContain("LEAKME123");
        snippet.ShouldNotContain("ABCDEFGH1234");
        detail.GetProperty("attemptLog")[0].GetProperty("responseStatus").GetInt32().ShouldBe(500);
    }

    [Fact]
    public async Task Fairness_ANoisyTenantCannotStarveAnother_PerTenantBatchAndConcurrencyLimits()
    {
        await using var host = TestHost.Create(factory, ("Integrations:Webhooks:PerTenantBatch", "3"), ("Integrations:Webhooks:BatchSize", "50"), ("Integrations:Webhooks:MaxConcurrentPerTenant", "2"), ("Integrations:Webhooks:MaxConcurrentPerHost", "100"));
        host.Transport.Delay = TimeSpan.FromMilliseconds(150);
        var noisy = await NewHookAsync(host, "Gurultucu");
        var quiet = await NewHookAsync(host, "Sessiz");
        for (var i = 0; i < 9; i++)
        {
            await noisy.Tenant.Admin.CreateLeadAsync();
        }

        await quiet.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();

        // Tek tur: gurultucu en cok 3 satir alir, sessiz kiraci ayni turda ilerler.
        var processed = await host.RunDispatcherOnceAsync();
        processed.ShouldBe(4, "3 rows from the noisy tenant + 1 from the quiet one");
        (await DeliveriesAsync(quiet.Id)).Single().Status.ShouldBe("succeeded");
        (await DeliveriesAsync(noisy.Id)).Count(r => r.Status == "succeeded").ShouldBeLessThanOrEqualTo(2, "per-tenant concurrency cap defers the third row of the batch");
        host.Transport.MaxObservedConcurrency.ShouldBeLessThanOrEqualTo(3);

        // Ertelenen satirlar sonraki turlarda tamamlanir (basarisiz sayilmaz).
        host.Clock.Advance(TimeSpan.FromSeconds(20));
        host.Transport.Delay = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            await host.RunDispatcherAsync();
            host.Clock.Advance(TimeSpan.FromSeconds(20));
        }

        (await DeliveriesAsync(noisy.Id)).ShouldAllBe(r => r.Status == "succeeded" && r.Attempts == 1);
    }

    [Fact]
    public async Task WebhooksDisabledDeployment_WritesNothing_TestAndRedeliverAre409_ButCrudWorks()
    {
        await using var host = TestHost.Create(factory, ("Integrations:Webhooks:Enabled", "false"));
        var hook = await NewHookAsync(host, "Kapali Kurulum");
        await hook.Tenant.Admin.CreateLeadAsync();
        await host.Factory.DrainOutboxesAsync();
        (await DeliveriesAsync(hook.Id)).ShouldBeEmpty("fan-out writes nothing while delivery is disabled");

        await (await hook.Tenant.Admin.SendAsync(HttpMethod.Post, $"{Wh}/{hook.Id}/test")).Response.ShouldBeCodeAsync(HttpStatusCode.Conflict, "webhook.delivery_unavailable");
        (await hook.Tenant.Admin.GetJsonAsync($"{Base}/integrations/status")).GetProperty("webhooksEnabled").GetBoolean().ShouldBeFalse();
        await hook.Tenant.Admin.SendOkAsync(HttpMethod.Put, $"{Wh}/{hook.Id}", new { name = "yeni-ad", url = "https://hooks.example.com/x", eventTypes = new[] { "lead.created" }, enabled = true }, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DealWonAndLost_ProduceDerivedDistinctIds_AndReachTheRightSubscribers()
    {
        await using var host = TestHost.Create(factory);
        var stage = await NewHookAsync(host, "Firsat Asama", ["deal.stage_changed"]);
        var won = await NewHookAsync(host, "Firsat Kazanildi", ["deal.won"], stage.Tenant);
        var lost = await NewHookAsync(host, "Firsat Kaybedildi", ["deal.lost"], stage.Tenant);
        var admin = stage.Tenant.Admin;

        var account = (await admin.SendAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "Firsat Firmasi" })).Body.GuidProp("id");
        var pipelines = await admin.GetJsonAsync($"{Base}/pipelines");
        var pipeline = (pipelines.ValueKind == JsonValueKind.Array ? pipelines : pipelines.GetProperty("items")).EnumerateArray().First();
        var stages = pipeline.GetProperty("stages").EnumerateArray().ToList();
        var wonStage = stages.First(s => s.Str("kind") == "won").GuidProp("id");
        var lostStage = stages.First(s => s.Str("kind") == "lost").GuidProp("id");

        var created = await admin.SendAsync(HttpMethod.Post, $"{Base}/deals", new { name = "Firsat 1", accountId = account, amount = 1500.5m, pipelineId = pipeline.GuidProp("id") });
        created.Response.StatusCode.ShouldBe(HttpStatusCode.Created, created.Body.ToString());
        var dealId = created.Body.GuidProp("id");
        (await admin.SendAsync(HttpMethod.Post, $"{Base}/deals/{dealId}/stage", new { stageId = wonStage })).Response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await host.Factory.DrainOutboxesAsync();

        (await DeliveriesAsync(won.Id)).Count.ShouldBe(1);
        (await DeliveriesAsync(lost.Id)).ShouldBeEmpty();
        var stageRows = await DeliveriesAsync(stage.Id);
        stageRows.Count.ShouldBe(1);

        var wonEventId = await factory.ScalarAsync<Guid>("SELECT event_id FROM integrations.webhook_deliveries WHERE subscription_id = @s", ("s", won.Id));
        var stageEventId = await factory.ScalarAsync<Guid>("SELECT event_id FROM integrations.webhook_deliveries WHERE subscription_id = @s", ("s", stage.Id));
        wonEventId.ShouldNotBe(stageEventId, "deal.won gets a derived id, distinct from deal.stage_changed");
        await host.RunDispatcherAsync();
        var payloads = host.Transport.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.Clone()).ToList();
        payloads.Select(p => p.Str("type")).Order(StringComparer.Ordinal).ShouldBe(["deal.stage_changed", "deal.won"]);
        payloads.Single(p => p.Str("type") == "deal.won").GetProperty("data").GetProperty("amount").GetDecimal().ShouldBe(1500.5m);
        payloads.Single(p => p.Str("type") == "deal.won").GetProperty("data").GetProperty("currency").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task AccountContactAndCaseEvents_AreFannedOut_WithoutPersonalData()
    {
        await using var host = TestHost.Create(factory);
        var hook = await NewHookAsync(host, "Olaylar", ["account.created", "contact.created", "case.created", "lead.converted"]);
        var admin = hook.Tenant.Admin;
        var account = (await admin.SendAsync(HttpMethod.Post, $"{Base}/accounts", new { name = "Gizli Ad Firmasi", email = "gizli@example.com", phone = "+905551112233" })).Body.GuidProp("id");
        (await admin.SendAsync(HttpMethod.Post, $"{Base}/contacts", new { lastName = "Gizli", firstName = "Kisi", email = "kisi@example.com", accountId = account })).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.SendAsync(HttpMethod.Post, $"{Base}/cases", new { subject = "Gizli Konu", description = "Serbest metin", accountId = account, priority = "high", channel = "web" })).Response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var lead = await admin.CreateLeadAsync();
        (await admin.SendAsync(HttpMethod.Post, $"{Base}/leads/{lead}/convert", new { createDeal = false })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await host.Factory.DrainOutboxesAsync();
        await host.RunDispatcherAsync();

        var bodies = host.Transport.Calls.Select(c => Encoding.UTF8.GetString(c.Body)).ToList();
        bodies.Select(b => JsonDocument.Parse(b).RootElement.Str("type")).Order(StringComparer.Ordinal).ShouldBe(["account.created", "case.created", "contact.created", "lead.converted"]);
        foreach (var body in bodies)
        {
            body.ShouldNotContain("gizli@example.com");
            body.ShouldNotContain("kisi@example.com");
            body.ShouldNotContain("+905551112233");
            body.ShouldNotContain("Gizli");
            body.ShouldNotContain("Serbest metin");
        }

        // lead donusumu account.created/contact.created URETMEZ: tek account, tek contact.
        bodies.Count(b => b.Contains("\"type\":\"account.created\"", StringComparison.Ordinal)).ShouldBe(1);
        bodies.Count(b => b.Contains("\"type\":\"contact.created\"", StringComparison.Ordinal)).ShouldBe(1);
        var caseBody = JsonDocument.Parse(bodies.Single(b => b.Contains("case.created", StringComparison.Ordinal))).RootElement.GetProperty("data");
        caseBody.Str("priority").ShouldBe("high");
        caseBody.Str("channel").ShouldBe("web");
    }
}
