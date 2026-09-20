using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;
using Sense.Crm.Modules.Integrations.Application.Security;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Delivery;

/// <summary>Giden istek: doğrulanmış (SSRF denetiminden geçmiş) <see cref="PinnedAddress"/>'e bağlanılır; TLS/SNI özgün ad üzerinden (<see cref="Url"/>).</summary>
public sealed record TransportRequest(
    Uri Url,
    IPAddress PinnedAddress,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers,
    TimeSpan Timeout,
    int MaxResponseBytes);

/// <summary>Taşıyıcı sonuç türü (HTTP yanıtı alındıysa <see cref="Ok"/>).</summary>
public enum TransportStatus
{
    Ok,
    Timeout,
    ConnectionError,
    TlsError,
    Blocked,
}

/// <summary>Taşıyıcı sonucu. <see cref="Body"/> en çok <c>MaxResponseBytes</c> baytlık başlangıçtır (sonra bağlantı kesilir). <c>RetryAfter</c> saniye/HTTP-tarihinden hesaplanır.</summary>
public sealed record TransportResult(
    TransportStatus Status,
    int? HttpStatus,
    byte[] Body,
    TimeSpan? RetryAfter,
    TimeSpan Duration,
    string? Detail);

/// <summary>Webhook HTTP taşıyıcısı (yalnız Worker kullanır; API asla hedefe bağlanmaz). Test sahteleri bu arayüzü uygular.</summary>
public interface IWebhookTransport
{
    Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct);
}

/// <summary>Bağlantı anında hedef IP engel sınıflayıcısından geçemedi.</summary>
public sealed class BlockedDestinationException(string message) : Exception(message);

/// <summary>
/// Güvenli taşıyıcı (D5): <c>SocketsHttpHandler.ConnectCallback</c> yalnız <b>doğrulanan IP'ye</b> bağlanır (proxy varsa <c>CONNECT &lt;ip&gt;:&lt;port&gt;</c> tüneli — ad değil IP), TLS el sıkışması URL ana bilgisayarına
/// göre (sertifika doğrulaması <b>kapatılmaz</b>, TLS ≥ 1.2); yönlendirme (3xx) <b>izlenmez</b> (<c>Location</c> hiç çözülmez); bağlantı havuzu ömrü 30 sn (eski çözümleme uzun tutulmaz); her yeni bağlantıda
/// IP <b>yeniden sınıflandırılır</b> (bağlantı anı denetimi). Yanıt gövdesi <c>MaxResponseBytes</c>'ta kesilir. Egress proxy ikinci, bağımsız katmandır (DevOps).
/// </summary>
public sealed class SafeWebhookHttpTransport : IWebhookTransport, IDisposable
{
    private static readonly HttpRequestOptionsKey<IPAddress> PinnedKey = new("crm.pinned_ip");
    private readonly HttpClient _client;
    private readonly IOptions<IntegrationsOptions> _options;

    public SafeWebhookHttpTransport(IOptions<IntegrationsOptions> options)
    {
        _options = options;
        var settings = options.Value;
        var policy = UrlPolicy.From(settings);
        var proxy = ParseProxy(settings.Webhooks.EgressProxy);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            ConnectTimeout = TimeSpan.FromSeconds(settings.Webhooks.ConnectTimeoutSeconds),
            MaxConnectionsPerServer = Math.Max(settings.Webhooks.MaxConcurrentPerHost, 1),
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
            ConnectCallback = async (context, ct) =>
            {
                if (!context.InitialRequestMessage.Options.TryGetValue(PinnedKey, out var pinned))
                {
                    throw new BlockedDestinationException("no pinned address");
                }

                // Bağlantı anı denetimi: doğrulanmış IP havuzlu/yeniden kurulan her bağlantıda yeniden sınıflandırılır.
                if (IpClassifier.IsBlocked(pinned, policy.AllowedPrivateCidrs, policy.AllowLoopback))
                {
                    throw new BlockedDestinationException("blocked at connect time");
                }

                var port = context.DnsEndPoint.Port;
                if (proxy is not null)
                {
                    return await TunnelAsync(proxy, pinned, port, ct).ConfigureAwait(false);
                }

                if (!(settings.DevelopmentLike && settings.Webhooks.AllowDirectEgress))
                {
                    throw new InvalidOperationException("Direct egress is disabled (configure Integrations:Webhooks:EgressProxy).");
                }

                var socket = new Socket(pinned.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(pinned, port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        message.Options.Set(PinnedKey, request.PinnedAddress);
        message.Content = new ByteArrayContent(request.Body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var body = await ReadLimitedAsync(response, request.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
            return new TransportResult(TransportStatus.Ok, (int)response.StatusCode, body, RetryAfterOf(response), Elapsed(started), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(TransportStatus.Timeout, started, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return Map(ex, started);
        }
        catch (BlockedDestinationException)
        {
            return Fail(TransportStatus.Blocked, started, "blocked_destination");
        }
    }

    public void Dispose() => _client.Dispose();

    private static TransportResult Map(HttpRequestException ex, long started)
    {
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is BlockedDestinationException)
            {
                return Fail(TransportStatus.Blocked, started, "blocked_destination");
            }

            if (inner is AuthenticationException)
            {
                return Fail(TransportStatus.TlsError, started, "tls_error");
            }
        }

        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return Fail(TransportStatus.TlsError, started, "tls_error");
        }

        // Ham iletiler URL/IP içerebilir; yalnız sabit sınıf adı saklanır.
        var socket = ex.InnerException as SocketException;
        return Fail(TransportStatus.ConnectionError, started, socket is null ? "connection_error" : "connection_error:" + socket.SocketErrorCode);
    }

    private static TransportResult Fail(TransportStatus status, long started, string detail) => new(status, null, [], null, Elapsed(started), detail);

    private static TimeSpan Elapsed(long started) => System.Diagnostics.Stopwatch.GetElapsedTime(started);

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[max];
        var total = 0;
        while (total < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static IPEndPoint? ParseProxy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
        {
            return new IPEndPoint(address, uri.Port);
        }

        return IPEndPoint.TryParse(text, out var endpoint) ? endpoint : null;
    }

    /// <summary>Egress proxy: <c>CONNECT &lt;IP&gt;:&lt;port&gt;</c> (ad değil IP) tüneli; yalnız <c>200</c> kabul.</summary>
    private static async Task<Stream> TunnelAsync(IPEndPoint proxy, IPAddress target, int port, CancellationToken ct)
    {
        var socket = new Socket(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxy, ct).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var authority = target.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{target}]:{port}" : $"{target}:{port}";
            var connect = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: keep-alive\r\n\r\n");
            await stream.WriteAsync(connect, ct).ConfigureAwait(false);

            var header = new StringBuilder();
            var single = new byte[1];
            while (header.Length < 8192)
            {
                if (await stream.ReadAsync(single, ct).ConfigureAwait(false) == 0)
                {
                    throw new IOException("Proxy closed the connection.");
                }

                header.Append((char)single[0]);
                if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n")
                {
                    break;
                }
            }

            var status = header.ToString().Split(' ', 3);
            if (status.Length < 2 || status[1] != "200")
            {
                throw new BlockedDestinationException("egress proxy refused the tunnel");
            }

            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
