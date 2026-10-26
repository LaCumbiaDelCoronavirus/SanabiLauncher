using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SS14.Launcher.Tests;

/// <summary>
///     Just enough of a SOCKS5 server (RFC 1928/1929) on loopback to test against: CONNECT hands the tunnel to
///         <see cref="OnTunnel"/>, UDP ASSOCIATE really relays datagrams.
/// </summary>
internal sealed class FakeSocks5Server : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancel = new();
    private readonly ConcurrentBag<IDisposable> _resources = new();
    private readonly ConcurrentBag<TcpClient> _udpControls = new();

    public string? RequiredUsername { get; init; }
    public string? RequiredPassword { get; init; }

    /// <summary>
    ///     When false, UDP ASSOCIATE is answered with "command not supported", like a TCP-only proxy.
    /// </summary>
    public bool SupportsUdp { get; init; } = true;

    /// <summary>
    ///     While set, new UDP associations are refused (so a dropped one can't be re-established yet).
    /// </summary>
    public volatile bool RefuseUdpAssociations;

    /// <summary>
    ///     Runs on each CONNECT tunnel after the success reply. Default: hold it open until the client closes.
    /// </summary>
    public Func<Stream, Task>? OnTunnel { get; init; }

    public ConcurrentQueue<string> ConnectTargets { get; } = new();
    public ConcurrentQueue<string> UdpDestinations { get; } = new();

    /// <summary>
    ///     Datagrams the relay side received from the client, across every association.
    /// </summary>
    public int ClientDatagramsReceived => _clientDatagrams;

    private int _clientDatagrams;

    public FakeSocks5Server()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    ///     Closes every UDP association's control connection, as a proxy does when it drops the association.
    ///         The UDP sockets stay open and keep counting, so a client that keeps sending anyway is caught.
    /// </summary>
    public void DropUdpAssociations()
    {
        while (_udpControls.TryTake(out var control))
            control.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancel.Token);
            }
            catch (Exception)
            {
                return;
            }

            _resources.Add(client);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var cancel = _cancel.Token;

            var greeting = await ReadAsync(stream, 2, cancel);
            var methods = await ReadAsync(stream, greeting[1], cancel);

            var wantsAuth = RequiredUsername != null;
            var method = wantsAuth ? (byte)2 : (byte)0;
            if (Array.IndexOf(methods, method) < 0)
            {
                await stream.WriteAsync(new byte[] { 5, 0xFF }, cancel);
                return;
            }

            await stream.WriteAsync(new byte[] { 5, method }, cancel);

            if (wantsAuth)
            {
                await ReadAsync(stream, 1, cancel);
                var username = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1, cancel))[0], cancel));
                var password = Encoding.UTF8.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1, cancel))[0], cancel));
                var ok = username == RequiredUsername && password == RequiredPassword;
                await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, cancel);
                if (!ok)
                    return;
            }

            var head = await ReadAsync(stream, 4, cancel);
            var (host, port) = await ReadAddressAsync(stream, head[3], cancel);

            switch (head[1])
            {
                case 1: // CONNECT
                    ConnectTargets.Enqueue($"{host}:{port}");
                    await stream.WriteAsync(SuccessReply(IPAddress.Loopback, 0), cancel);
                    if (OnTunnel != null)
                        await OnTunnel(stream);
                    else
                        await stream.ReadAsync(new byte[1], cancel);
                    break;

                case 3 when SupportsUdp && !RefuseUdpAssociations: // UDP ASSOCIATE
                    var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    _resources.Add(udp);
                    _udpControls.Add(client);
                    _ = RelayUdpAsync(udp, cancel);

                    // Reply with the unspecified address, as many real servers do: the client must substitute
                    // the address it's talking to.
                    await stream.WriteAsync(SuccessReply(IPAddress.Any, ((IPEndPoint)udp.Client.LocalEndPoint!).Port), cancel);
                    await stream.ReadAsync(new byte[1], cancel);
                    break;

                default:
                    await stream.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, cancel);
                    break;
            }
        }
        catch (Exception)
        {
            // Client went away or the test is over.
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task RelayUdpAsync(UdpClient udp, CancellationToken cancel)
    {
        IPEndPoint? clientEndPoint = null;
        var destinations = new HashSet<IPEndPoint>();

        while (!cancel.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(cancel);
            }
            catch (Exception)
            {
                return;
            }

            if (destinations.Contains(received.RemoteEndPoint))
            {
                if (clientEndPoint == null)
                    continue;

                // Reply from the destination: wrap it for the client.
                var source = received.RemoteEndPoint;
                var reply = new byte[10 + received.Buffer.Length];
                reply[3] = 1;
                source.Address.MapToIPv4().GetAddressBytes().CopyTo(reply, 4);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)source.Port);
                received.Buffer.CopyTo(reply, 10);
                await udp.SendAsync(reply, clientEndPoint, cancel);
                continue;
            }

            Interlocked.Increment(ref _clientDatagrams);
            clientEndPoint = received.RemoteEndPoint;

            var datagram = received.Buffer;
            if (datagram.Length < 4 || datagram[2] != 0)
                continue;

            var offset = 4;
            string host;
            switch (datagram[3])
            {
                case 1:
                    host = new IPAddress(datagram.AsSpan(4, 4)).ToString();
                    offset += 4;
                    break;
                case 4:
                    host = new IPAddress(datagram.AsSpan(4, 16)).ToString();
                    offset += 16;
                    break;
                case 3:
                    host = Encoding.ASCII.GetString(datagram, 5, datagram[4]);
                    offset += 1 + datagram[4];
                    break;
                default:
                    continue;
            }

            var port = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(offset));
            offset += 2;
            UdpDestinations.Enqueue($"{host}:{port}");

            // The fake resolves "localhost"-style names itself, like a real proxy would.
            var address = IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Loopback;
            var destination = new IPEndPoint(address, port);
            destinations.Add(destination);
            await udp.SendAsync(datagram.AsMemory(offset), destination, cancel);
        }
    }

    private static byte[] SuccessReply(IPAddress address, int port)
    {
        var reply = new byte[10];
        reply[0] = 5;
        reply[3] = 1;
        address.GetAddressBytes().CopyTo(reply, 4);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)port);
        return reply;
    }

    private static async Task<(string, int)> ReadAddressAsync(Stream stream, byte addressType, CancellationToken cancel)
    {
        var host = addressType switch
        {
            1 => new IPAddress(await ReadAsync(stream, 4, cancel)).ToString(),
            4 => new IPAddress(await ReadAsync(stream, 16, cancel)).ToString(),
            3 => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1, cancel))[0], cancel)),
            _ => throw new InvalidDataException(),
        };

        var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(stream, 2, cancel));
        return (host, port);
    }

    internal static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken cancel)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cancel);
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancel.CancelAsync();
        _listener.Stop();
        foreach (var resource in _resources)
            resource.Dispose();
    }
}

/// <summary>
///     A loopback HTTP proxy that only understands CONNECT, optionally demanding Basic credentials.
/// </summary>
internal sealed class FakeHttpConnectProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancel = new();

    public string? RequiredUsername { get; init; }
    public string? RequiredPassword { get; init; }

    public ConcurrentQueue<string> ConnectTargets { get; } = new();

    public FakeHttpConnectProxy()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoopAsync()
    {
        while (!_cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancel.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new StringBuilder();
                var one = new byte[1];
                while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(one, _cancel.Token);
                    request.Append((char)one[0]);
                }

                var lines = request.ToString().Split("\r\n");
                ConnectTargets.Enqueue(lines[0].Split(' ')[1]);

                if (RequiredUsername != null)
                {
                    var expected = "Proxy-Authorization: Basic " +
                                   Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RequiredUsername}:{RequiredPassword}"));
                    if (Array.IndexOf(lines, expected) < 0)
                    {
                        await stream.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), _cancel.Token);
                        return;
                    }
                }

                await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), _cancel.Token);
                await stream.ReadAsync(one, _cancel.Token);
            }
            catch (Exception)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancel.CancelAsync();
        _listener.Stop();
    }
}
