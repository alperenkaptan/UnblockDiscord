using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UnblockDiscord;

// Only the Discord folder is directed here by ProxiFyre. DNS correction happens
// inside these connections; the OS DNS cache, adapters and hosts file are untouched.
internal sealed class DiscordRelay : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, (IPAddress Address, DateTime Expires)> cache = new();
    private readonly HttpClient dns = new(new SocketsHttpHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim connections = new(512);
    private readonly int upstreamPort;

    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal DiscordRelay(int upstreamPort)
    {
        this.upstreamPort = upstreamPort;
        listener.Start();
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (!connections.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient { NoDelay = true })
        using (var setup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            setup.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var local = client.GetStream();
                var greeting = await ReadAsync(local, 2, setup.Token);
                if (greeting[0] != 5) return;
                var methods = await ReadAsync(local, greeting[1], setup.Token);
                if (!methods.Contains((byte)0))
                {
                    await local.WriteAsync(new byte[] { 5, 255 }, setup.Token);
                    return;
                }
                await local.WriteAsync(new byte[] { 5, 0 }, setup.Token);
                var header = await ReadAsync(local, 4, setup.Token);
                if (header[0] != 5 || header[2] != 0 || header[1] is not (1 or 3)) return;
                var address = await ReadAddressAsync(local, header[3], setup.Token);
                var portBytes = await ReadAsync(local, 2, setup.Token);
                var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
                var host = header[3] == 3 ? Encoding.ASCII.GetString(address.AsSpan(1)) : new IPAddress(address).ToString();
                byte[] initial = [];

                if (header[1] == 1)
                {
                    // ProxiFyre sends destination IPs, including poisoned DNS answers.
                    // Acknowledge locally to obtain the original ClientHello/SNI first.
                    await local.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, setup.Token);
                    if (port == 443)
                    {
                        (initial, var name) = await ReadHelloAsync(local, setup.Token);
                        if (name != null && DiscordTraffic.IsDiscordHost(name)) host = name;
                    }
                    if (DiscordTraffic.IsDiscordHost(host)) host = (await ResolveAsync(host, setup.Token)).ToString();
                }

                await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, setup.Token);
                var remote = upstream.GetStream();
                await remote.WriteAsync(new byte[] { 5, 1, 0 }, setup.Token);
                var method = await ReadAsync(remote, 2, setup.Token);
                if (method[0] != 5 || method[1] != 0) throw new IOException("ByeDPI rejected SOCKS negotiation.");
                byte[] target;
                byte kind;
                if (IPAddress.TryParse(host, out var ip))
                {
                    target = ip.GetAddressBytes();
                    kind = target.Length == 4 ? (byte)1 : (byte)4;
                }
                else
                {
                    var nameBytes = Encoding.ASCII.GetBytes(host);
                    target = [(byte)nameBytes.Length, .. nameBytes];
                    kind = 3;
                }
                await remote.WriteAsync(new byte[] { 5, header[1], 0, kind }.Concat(target).Concat(portBytes).ToArray(), setup.Token);
                var reply = await ReadAsync(remote, 4, setup.Token);
                var bound = await ReadAddressAsync(remote, reply[3], setup.Token);
                var boundPort = await ReadAsync(remote, 2, setup.Token);
                if (reply[1] != 0) throw new IOException($"ByeDPI connection failed ({reply[1]}).");
                if (header[1] == 3)
                {
                    // UDP datagrams go straight to ByeDPI's loopback UDP relay.
                    if (reply[3] == 1 && bound.All(b => b == 0)) bound = IPAddress.Loopback.GetAddressBytes();
                    await local.WriteAsync(reply.Concat(bound).Concat(boundPort).ToArray(), setup.Token);
                }
                else if (initial.Length > 0) await remote.WriteAsync(initial, setup.Token);

                setup.CancelAfter(Timeout.InfiniteTimeSpan);
                using var transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var sending = local.CopyToAsync(remote, transfer.Token);
                var receiving = remote.CopyToAsync(local, transfer.Token);
                await Task.WhenAny(sending, receiving);
                await transfer.CancelAsync();
                try { await Task.WhenAll(sending, receiving); } catch (OperationCanceledException) { }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or
                HttpRequestException or JsonException or InvalidDataException or ObjectDisposedException) { }
            finally { connections.Release(); }
        }
    }

    private async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
    {
        if (cache.TryGetValue(host, out var entry) && entry.Expires > DateTime.UtcNow) return entry.Address;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://1.1.1.1/dns-query?name=" + Uri.EscapeDataString(host) + "&type=A");
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await dns.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (json.RootElement.GetProperty("Status").GetInt32() != 0 ||
            !json.RootElement.TryGetProperty("Answer", out var answers))
            throw new IOException("Encrypted DNS could not resolve " + host);
        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.GetProperty("type").GetInt32() != 1 ||
                !IPAddress.TryParse(answer.GetProperty("data").GetString(), out var ip)) continue;
            if (cache.Count > 512) cache.Clear();
            var ttl = Math.Clamp(answer.GetProperty("TTL").GetInt32(), 0, 300);
            cache[host] = (ip, DateTime.UtcNow.AddSeconds(ttl));
            return ip;
        }
        throw new IOException("Encrypted DNS returned no IPv4 address for " + host);
    }

    private static async Task<(byte[], string?)> ReadHelloAsync(NetworkStream stream, CancellationToken token)
    {
        using var wire = new MemoryStream();
        using var handshake = new MemoryStream();
        while (wire.Length < 65536)
        {
            var header = await ReadAsync(stream, 5, token);
            wire.Write(header);
            if (header[0] != 22 || header[1] != 3) return (wire.ToArray(), null);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
            if (length == 0 || length > 18432) throw new InvalidDataException("Invalid TLS record length.");
            var payload = await ReadAsync(stream, length, token);
            wire.Write(payload);
            handshake.Write(payload);
            var data = handshake.ToArray();
            if (data.Length < 4) continue;
            var wanted = 4 + (data[1] << 16 | data[2] << 8 | data[3]);
            if (data[0] != 1 || data.Length >= wanted)
                return (wire.ToArray(), DiscordTraffic.ServerName(data));
        }
        throw new InvalidDataException("ClientHello is too large.");
    }

    private static async Task<byte[]> ReadAddressAsync(NetworkStream stream, byte kind, CancellationToken token)
    {
        if (kind == 1) return await ReadAsync(stream, 4, token);
        if (kind == 4) return await ReadAsync(stream, 16, token);
        if (kind != 3) throw new InvalidDataException("Invalid SOCKS address.");
        var size = await ReadAsync(stream, 1, token);
        return [size[0], .. await ReadAsync(stream, size[0], token)];
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int size, CancellationToken token)
    {
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, token);
        return data;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        listener.Stop();
        dns.Dispose();
        // Active copies observe cancellation and release their connections.
    }
}
