using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Sense.Crm.Modules.Integrations.Application;

namespace Sense.Crm.Modules.Integrations.Infrastructure.Delivery;

/// <summary>Ad çözümleme portu (D5, SSRF): teslimat başına <b>tek çözümleme</b>; sonuç sınıflandırılır ve bağlantı doğrulanan IP'ye kurulur. Çözülemeyen ad → boş liste (<c>dns_error</c>).</summary>
public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>
/// Üretimde <c>Integrations:Webhooks:DnsServer</c> (egress-dns) üzerinden doğrudan UDP (TCP yedekli) A + AAAA sorgusu yapan asgari çözümleyici; sunucu yapılandırılmamışsa (yalnız
/// Development/Testing) sistem çözümleyicisi. Ele geçirilmiş bir Worker'ın rastgele çözümleyiciye gitmesi yerine tek denetimli çözümleyici kullanılır.
/// </summary>
public sealed class WebhookDnsResolver(IOptions<IntegrationsOptions> options) : IDnsResolver
{
    private const int MaxAnswers = 16;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        var server = ParseServer(options.Value.Webhooks.DnsServer);
        try
        {
            if (server is null)
            {
                var system = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                return [.. system.Take(MaxAnswers)];
            }

            var a = QueryAsync(server, host, 1, ct);
            var aaaa = QueryAsync(server, host, 28, ct);
            await Task.WhenAll(a, aaaa).ConfigureAwait(false);
            return [.. a.Result.Concat(aaaa.Result).Distinct().Take(MaxAnswers)];
        }
        catch (Exception ex) when (ex is SocketException or InvalidDataException or TimeoutException or IOException)
        {
            return [];
        }
    }

    private static IPEndPoint? ParseServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (IPEndPoint.TryParse(value.Trim(), out var endpoint))
        {
            return endpoint.Port == 0 ? new IPEndPoint(endpoint.Address, 53) : endpoint;
        }

        return IPAddress.TryParse(value.Trim(), out var address) ? new IPEndPoint(address, 53) : null;
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryAsync(IPEndPoint server, string host, ushort type, CancellationToken ct)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(0, ushort.MaxValue + 1);
        var query = BuildQuery(id, host, type);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server);
            await udp.SendAsync(query, timeout.Token).ConfigureAwait(false);
            var response = (await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false)).Buffer;
            var parsed = Parse(response, id, type);
            if (parsed.Truncated)
            {
                return await QueryTcpAsync(server, query, id, type, timeout.Token).ConfigureAwait(false);
            }

            return parsed.Addresses;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("DNS query timed out.");
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryTcpAsync(IPEndPoint server, byte[] query, ushort id, ushort type, CancellationToken ct)
    {
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return Parse(buffer, id, type).Addresses;
    }

    public static byte[] BuildQuery(ushort id, string host, ushort type)
    {
        var labels = host.TrimEnd('.').Split('.');
        var name = new List<byte>();
        foreach (var label in labels)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new InvalidDataException("Invalid DNS label.");
            }

            name.Add((byte)bytes.Length);
            name.AddRange(bytes);
        }

        name.Add(0);
        var message = new byte[12 + name.Count + 4];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1);
        name.CopyTo(message, 12);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(12 + name.Count), type);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(14 + name.Count), 1);
        return message;
    }

    public static (IReadOnlyList<IPAddress> Addresses, bool Truncated) Parse(byte[] message, ushort expectedId, ushort type)
    {
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message) != expectedId)
        {
            throw new InvalidDataException("DNS response id mismatch.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2));
        var truncated = (flags & 0x0200) != 0;
        var rcode = flags & 0x000F;
        if (rcode != 0)
        {
            return ([], truncated);
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            offset = SkipName(message, offset) + 4;
        }

        var result = new List<IPAddress>();
        for (var i = 0; i < answers && result.Count < MaxAnswers; i++)
        {
            offset = SkipName(message, offset);
            if (offset + 10 > message.Length)
            {
                throw new InvalidDataException("DNS response truncated.");
            }

            var recordType = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
            offset += 10;
            if (offset + length > message.Length)
            {
                throw new InvalidDataException("DNS response truncated.");
            }

            if (recordType == type && ((type == 1 && length == 4) || (type == 28 && length == 16)))
            {
                result.Add(new IPAddress(message.AsSpan(offset, length)));
            }

            offset += length;
        }

        return (result, truncated);
    }

    private static int SkipName(byte[] message, int offset)
    {
        var guard = 0;
        while (offset < message.Length && guard++ < 128)
        {
            var length = message[offset];
            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2;
            }

            offset += length + 1;
        }

        throw new InvalidDataException("Invalid DNS name.");
    }
}
