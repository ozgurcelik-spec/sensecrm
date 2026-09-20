using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Infrastructure.Delivery;
using Shouldly;
using Xunit;

namespace Sense.Crm.Modules.Integrations.Tests.Api;

/// <summary>Yerel HTTP alıcı: gerçek <c>SafeWebhookHttpTransport</c> (ConnectCallback, IP sabitleme, yönlendirme yok, gövde sınırı, zaman aşımı).</summary>
public sealed class ReceiverFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public int Port { get; private set; }

    public List<(string Path, string Body, Dictionary<string, string> Headers)> Requests { get; } = [];

    private int _metadataHits;

    public int MetadataHits => Volatile.Read(ref _metadataHits);

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Map("/{**path}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value ?? "/";
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (path == "/metadata")
            {
                Interlocked.Increment(ref _metadataHits);
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync("metadata!");
                return;
            }

            lock (Requests)
            {
                Requests.Add((path, body, context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            }

            if (path.StartsWith("/redirect", StringComparison.Ordinal))
            {
                context.Response.StatusCode = int.Parse(context.Request.Query["code"].FirstOrDefault() ?? "302", System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers.Location = $"http://127.0.0.1:{Port}/metadata";
                return;
            }

            if (path == "/big")
            {
                await context.Response.WriteAsync(new string('a', 1_000_000));
                return;
            }

            if (path == "/slow")
            {
                await Task.Delay(TimeSpan.FromSeconds(4), context.RequestAborted).ContinueWith(_ => { }, TaskScheduler.Default);
                return;
            }

            if (path == "/retry")
            {
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "120";
                return;
            }

            await context.Response.WriteAsync("ok");
        });
        await _app.StartAsync();
        Port = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

public sealed class RealTransportTests(ReceiverFixture receiver) : IClassFixture<ReceiverFixture>
{
    private static IOptions<IntegrationsOptions> Options(bool allowLoopback = true, string? proxy = null)
    {
        var options = new IntegrationsOptions { DevelopmentLike = true, Webhooks = { AllowDirectEgress = proxy is null, DevAllowLoopback = allowLoopback, EgressProxy = proxy, DnsServer = proxy is null ? null : "127.0.0.1" } };
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private TransportRequest Request(string path, IPAddress? pinned = null, string host = "127.0.0.1", TimeSpan? timeout = null, int maxBytes = 65536, byte[]? body = null) =>
        new(new Uri($"http://{host}:{receiver.Port}{path}"), pinned ?? IPAddress.Loopback, body ?? Encoding.UTF8.GetBytes("{\"hello\":\"world\"}"),
            new Dictionary<string, string> { ["User-Agent"] = "SenseCRM-Webhooks/1.0", ["X-Crm-Signature"] = "t=1,v1=abc", ["X-Crm-Event-Id"] = "evt-1" }, timeout ?? TimeSpan.FromSeconds(5), maxBytes);

    [Fact]
    public async Task Delivers_TheExactBodyAndHeaders_OverARealSocket()
    {
        using var transport = new SafeWebhookHttpTransport(Options());
        var result = await transport.SendAsync(Request("/ok"), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportStatus.Ok);
        result.HttpStatus.ShouldBe(200);
        Encoding.UTF8.GetString(result.Body).ShouldBe("ok");
        var seen = receiver.Requests.Last(r => r.Path == "/ok");
        seen.Body.ShouldBe("{\"hello\":\"world\"}");
        seen.Headers["X-Crm-Signature"].ShouldBe("t=1,v1=abc");
        seen.Headers["X-Crm-Event-Id"].ShouldBe("evt-1");
        seen.Headers["User-Agent"].ShouldBe("SenseCRM-Webhooks/1.0");
        seen.Headers["Content-Type"].ShouldStartWith("application/json");
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirects_AreNeverFollowed_TheLocationTargetIsNeverContacted(int code)
    {
        using var transport = new SafeWebhookHttpTransport(Options());
        var before = receiver.MetadataHits;
        var result = await transport.SendAsync(Request($"/redirect?code={code}"), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportStatus.Ok);
        result.HttpStatus.ShouldBe(code);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        receiver.MetadataHits.ShouldBe(before, "the redirect target (metadata) must receive no request");
    }

    [Fact]
    public async Task TheConnectionGoesToThePinnedIp_NotToWhateverTheUrlHostResolvesTo()
    {
        using var transport = new SafeWebhookHttpTransport(Options());
        var result = await transport.SendAsync(Request("/ok", pinned: IPAddress.Loopback, host: "pinned-host.example.invalid"), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportStatus.Ok, result.Detail);
        receiver.Requests.Last(r => r.Path == "/ok").Headers["Host"].ShouldStartWith("pinned-host.example.invalid");
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("100.100.100.200")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task ConnectTimeCheck_BlocksInternalPinnedAddresses_WithoutDialling(string address)
    {
        using var transport = new SafeWebhookHttpTransport(Options(allowLoopback: false));
        var started = DateTime.UtcNow;
        var result = await transport.SendAsync(Request("/ok", pinned: IPAddress.Parse(address), host: "hooks.example.com"), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportStatus.Blocked);
        (DateTime.UtcNow - started).TotalSeconds.ShouldBeLessThan(2);
    }

    [Fact]
    public async Task Loopback_IsBlockedAtConnectTime_WhenTheDevFlagIsOff_EvenIfSomethingListens()
    {
        using var transport = new SafeWebhookHttpTransport(Options(allowLoopback: false));
        var before = receiver.Requests.Count;
        var result = await transport.SendAsync(Request("/ok"), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportStatus.Blocked);
        receiver.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task ResponseBody_IsCapped_ThenTheConnectionIsDropped()
    {
        using var transport = new SafeWebhookHttpTransport(Options());
        var result = await transport.SendAsync(Request("/big", maxBytes: 1024), TestContext.Current.CancellationToken);
        result.HttpStatus.ShouldBe(200);
        result.Body.Length.ShouldBe(1024);
    }

    [Fact]
    public async Task ASlowReceiver_TimesOut_AtTheConfiguredLimit()
    {
        using var transport = new SafeWebhookHttpTransport(Options());
        var started = DateTime.UtcNow;
        var result = await transport.SendAsync(Request("/slow", timeout: TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportStatus.Timeout);
        (DateTime.UtcNow - started).TotalSeconds.ShouldBeInRange(0.8, 3.0);
    }

    [Fact]
    public async Task RetryAfterHeader_IsParsed_AndAClosedPortIsAConnectionError()
    {
        using var transport = new SafeWebhookHttpTransport(Options());
        var retry = await transport.SendAsync(Request("/retry"), TestContext.Current.CancellationToken);
        retry.HttpStatus.ShouldBe(429);
        retry.RetryAfter.ShouldBe(TimeSpan.FromSeconds(120));

        var closed = new TransportRequest(new Uri("http://127.0.0.1:1/"), IPAddress.Loopback, [], new Dictionary<string, string>(), TimeSpan.FromSeconds(3), 1024);
        (await transport.SendAsync(closed, TestContext.Current.CancellationToken)).Status.ShouldBe(TransportStatus.ConnectionError);
    }

    [Fact]
    public async Task ViaTheEgressProxy_TheTunnelTargetIsTheIp_NotAName_AndARefusalIsBlocked()
    {
        var connects = new List<string>();
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var allow = true;
        var serve = Task.Run(
            async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await proxy.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        var read = await stream.ReadAsync(buffer);
                        var head = Encoding.ASCII.GetString(buffer, 0, read);
                        var line = head.Split("\r\n")[0];
                        lock (connects)
                        {
                            connects.Add(line);
                        }

                        if (!allow || !line.StartsWith("CONNECT 127.0.0.1:", StringComparison.Ordinal))
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\n\r\n"));
                            return;
                        }

                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
                        using var upstream = new TcpClient();
                        await upstream.ConnectAsync(IPAddress.Loopback, receiver.Port);
                        var up = upstream.GetStream();
                        await Task.WhenAny(stream.CopyToAsync(up), up.CopyToAsync(stream));
                    }
                });
            }
        }, TestContext.Current.CancellationToken);

        using var transport = new SafeWebhookHttpTransport(Options(proxy: $"http://127.0.0.1:{proxyPort}"));
        var ok = await transport.SendAsync(Request("/ok", host: "hooks.example.com"), TestContext.Current.CancellationToken);
        ok.Status.ShouldBe(TransportStatus.Ok, ok.Detail);
        lock (connects)
        {
            connects.ShouldContain($"CONNECT 127.0.0.1:{receiver.Port} HTTP/1.1");
            connects.ShouldAllBe(c => !c.Contains("hooks.example.com", StringComparison.Ordinal));
        }

        // Yeni tasiyici: ilk baglanti havuzda canli kalirdi.
        allow = false;
        using var second = new SafeWebhookHttpTransport(Options(proxy: $"http://127.0.0.1:{proxyPort}"));
        var refused = await second.SendAsync(Request("/ok", host: "hooks.example.com"), TestContext.Current.CancellationToken);
        refused.Status.ShouldBe(TransportStatus.Blocked);
        proxy.Stop();
        await serve;
    }
}
