using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Sanabi.Framework.Proxy;

/// <summary>
///     Thrown when a proxy is unreachable, refuses us, or speaks something we don't understand.
/// </summary>
public sealed class SanabiProxyException : Exception
{
    public SanabiProxyException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
///     Minimal client side of the proxy protocols in <see cref="SanabiProxyProtocol"/>.
///
///     The launcher's HTTP traffic is proxied by <see cref="System.Net.Http.SocketsHttpHandler"/>'s own
///         implementation; this exists for what that can't do: SOCKS5 UDP ASSOCIATE (the game's UDP), and
///         health probes that open a tunnel without sending any application data through it.
/// </summary>
public static class SanabiProxyClient
{
    private const byte Socks5Version = 5;
    private const byte Socks5CommandConnect = 1;
    private const byte Socks5CommandUdpAssociate = 3;
    private const byte Socks5MethodNoAuth = 0;
    private const byte Socks5MethodUserPass = 2;
    private const byte Socks5MethodNoneAcceptable = 0xFF;

    public const byte AddressTypeIpv4 = 1;
    public const byte AddressTypeDomain = 3;
    public const byte AddressTypeIpv6 = 4;

    /// <summary>
    ///     Opens a TCP tunnel through <paramref name="proxy"/> to <paramref name="host"/>:<paramref name="port"/>.
    ///         The returned stream talks to the target; dispose it to close the tunnel.
    /// </summary>
    public static async Task<Stream> OpenTunnelAsync(SanabiProxyEndpoint proxy, string host, int port, CancellationToken cancel)
    {
        var (_, stream) = await ConnectToProxyAsync(proxy, cancel).ConfigureAwait(false);

        try
        {
            switch (proxy.Protocol)
            {
                case SanabiProxyProtocol.Http:
                case SanabiProxyProtocol.Https:
                    await HttpConnectAsync(stream, proxy, host, port, cancel).ConfigureAwait(false);
                    break;
                case SanabiProxyProtocol.Socks4:
                case SanabiProxyProtocol.Socks4a:
                    await Socks4ConnectAsync(stream, proxy, host, port, cancel).ConfigureAwait(false);
                    break;
                case SanabiProxyProtocol.Socks5:
                    await Socks5AuthenticateAsync(stream, proxy, cancel).ConfigureAwait(false);
                    await Socks5CommandAsync(stream, Socks5CommandConnect, host, port, cancel).ConfigureAwait(false);
                    break;
                default:
                    throw new SanabiProxyException($"Unsupported proxy protocol {proxy.Protocol}.");
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Asks a SOCKS5 proxy for a UDP association. The association lives exactly as long as the returned
    ///         control connection (RFC 1928 §7), so keep it open for as long as datagrams should flow.
    /// </summary>
    public static async Task<SanabiSocks5UdpAssociation> Socks5UdpAssociateAsync(SanabiProxyEndpoint proxy, CancellationToken cancel)
    {
        if (proxy.Protocol != SanabiProxyProtocol.Socks5)
            throw new SanabiProxyException($"{proxy.Protocol} proxies can't carry UDP; SOCKS5 is required.");

        var (socket, stream) = await ConnectToProxyAsync(proxy, cancel).ConfigureAwait(false);

        try
        {
            await Socks5AuthenticateAsync(stream, proxy, cancel).ConfigureAwait(false);

            // We don't know which local address/port the datagrams will come from ahead of time, so send the
            // all-zeroes address, which per RFC 1928 means "unknown".
            var relay = await Socks5CommandAsync(stream, Socks5CommandUdpAssociate, IPAddress.Any.ToString(), 0, cancel)
                .ConfigureAwait(false);

            // Many servers answer with the unspecified address, meaning "the address you're already talking to".
            if (relay.Address.Equals(IPAddress.Any) || relay.Address.Equals(IPAddress.IPv6Any))
                relay = new IPEndPoint(((IPEndPoint)socket.RemoteEndPoint!).Address, relay.Port);

            return new SanabiSocks5UdpAssociation(stream, Normalize(relay));
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Writes the RFC 1928 §7 UDP request header for <paramref name="host"/>:<paramref name="port"/>.
    ///         Hostnames are sent as-is, so the proxy resolves them and no DNS query leaves this machine.
    /// </summary>
    public static byte[] BuildUdpHeader(string host, int port)
    {
        using var buffer = new MemoryStream();
        buffer.Write([0, 0, 0]); // RSV RSV FRAG
        WriteSocksAddress(buffer, host, port);
        return buffer.ToArray();
    }

    /// <summary>
    ///     Parses an RFC 1928 §7 UDP header off the front of a datagram received from the relay.
    /// </summary>
    /// <returns>Where the payload starts, or -1 if the datagram is malformed or fragmented (we don't reassemble).</returns>
    public static int ParseUdpHeader(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 4 || datagram[0] != 0 || datagram[1] != 0 || datagram[2] != 0)
            return -1;

        var addressLength = datagram[3] switch
        {
            AddressTypeIpv4 => 4,
            AddressTypeIpv6 => 16,
            AddressTypeDomain when datagram.Length > 4 => 1 + datagram[4],
            _ => -1,
        };

        if (addressLength < 0)
            return -1;

        var payloadOffset = 4 + addressLength + 2;
        return datagram.Length >= payloadOffset ? payloadOffset : -1;
    }

    /// <summary>
    ///     IPv4 addresses as plain IPv4, so endpoints compare equal regardless of which socket family saw them.
    /// </summary>
    public static IPEndPoint Normalize(IPEndPoint endPoint)
        => endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;

    private static async Task<(Socket, Stream)> ConnectToProxyAsync(SanabiProxyEndpoint proxy, CancellationToken cancel)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(proxy.Host.Trim('[', ']'), proxy.Port, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            socket.Dispose();
            throw new SanabiProxyException($"Couldn't reach proxy {proxy.DisplayString}: {e.Message}", e);
        }

        // Proxies tend to drop idle connections; keepalive stops a quiet UDP association's control socket from
        // being reaped behind our back.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        Stream stream = new NetworkStream(socket, ownsSocket: true);

        if (proxy.Protocol == SanabiProxyProtocol.Https)
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = proxy.Host.Trim('[', ']') },
                    cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException)
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
                throw new SanabiProxyException($"TLS handshake with proxy {proxy.DisplayString} failed: {e.Message}", e);
            }

            stream = sslStream;
        }

        return (socket, stream);
    }

    private static async Task HttpConnectAsync(Stream stream, SanabiProxyEndpoint proxy, string host, int port, CancellationToken cancel)
    {
        var authority = $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}";
        var request = new StringBuilder()
            .Append($"CONNECT {authority} HTTP/1.1\r\n")
            .Append($"Host: {authority}\r\n");

        if (proxy.HasCredentials)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password}"));
            request.Append($"Proxy-Authorization: Basic {token}\r\n");
        }

        request.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);

        // Read byte-by-byte up to the blank line, so not a single byte of the tunnel's payload is swallowed.
        var response = new StringBuilder();
        var one = new byte[1];
        while (!response.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (response.Length > 16 * 1024)
                throw new SanabiProxyException("HTTP proxy sent an oversized response to CONNECT.");

            await ReadExactlyAsync(stream, one, cancel).ConfigureAwait(false);
            response.Append((char)one[0]);
        }

        var statusLine = response.ToString().Split("\r\n", 2)[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out var status))
            throw new SanabiProxyException($"HTTP proxy sent a malformed response: '{statusLine}'.");

        if (status == 407)
            throw new SanabiProxyException(proxy.HasCredentials
                ? "HTTP proxy rejected the username/password."
                : "HTTP proxy requires a username/password.");

        if (status is < 200 or > 299)
            throw new SanabiProxyException($"HTTP proxy refused the tunnel: '{statusLine}'.");
    }

    private static async Task Socks4ConnectAsync(Stream stream, SanabiProxyEndpoint proxy, string host, int port, CancellationToken cancel)
    {
        using var request = new MemoryStream();
        request.Write([4, 1]); // VN, CD=CONNECT
        WritePort(request, port);

        string? remoteHost = null;
        if (IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            request.Write(address.GetAddressBytes());
        else if (proxy.Protocol == SanabiProxyProtocol.Socks4a)
        {
            // 0.0.0.x with x != 0 tells a SOCKS4a server to resolve the hostname that follows the user ID.
            request.Write([0, 0, 0, 1]);
            remoteHost = host;
        }
        else
        {
            // Plain SOCKS4 only speaks IPv4, so we have to resolve here.
            var resolved = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancel).ConfigureAwait(false))
                .FirstOrDefault() ?? throw new SanabiProxyException($"SOCKS4 needs an IPv4 address, but '{host}' has none.");
            request.Write(resolved.GetAddressBytes());
        }

        request.Write(Encoding.UTF8.GetBytes(proxy.Username));
        request.WriteByte(0);

        if (remoteHost != null)
        {
            request.Write(Encoding.ASCII.GetBytes(remoteHost));
            request.WriteByte(0);
        }

        await stream.WriteAsync(request.ToArray(), cancel).ConfigureAwait(false);

        var reply = new byte[8];
        await ReadExactlyAsync(stream, reply, cancel).ConfigureAwait(false);

        if (reply[0] != 0)
            throw new SanabiProxyException("Proxy didn't answer like a SOCKS4 server.");

        if (reply[1] != 90)
        {
            throw new SanabiProxyException(reply[1] switch
            {
                92 or 93 => "SOCKS4 proxy rejected the user ID.",
                _ => $"SOCKS4 proxy refused the connection (code {reply[1]}).",
            });
        }
    }

    private static async Task Socks5AuthenticateAsync(Stream stream, SanabiProxyEndpoint proxy, CancellationToken cancel)
    {
        byte[] greeting = proxy.HasCredentials
            ? [Socks5Version, 2, Socks5MethodNoAuth, Socks5MethodUserPass]
            : [Socks5Version, 1, Socks5MethodNoAuth];

        await stream.WriteAsync(greeting, cancel).ConfigureAwait(false);

        var choice = new byte[2];
        await ReadExactlyAsync(stream, choice, cancel).ConfigureAwait(false);

        if (choice[0] != Socks5Version)
            throw new SanabiProxyException("Proxy didn't answer like a SOCKS5 server.");

        switch (choice[1])
        {
            case Socks5MethodNoAuth:
                return;
            case Socks5MethodUserPass when proxy.HasCredentials:
                break;
            case Socks5MethodNoneAcceptable:
            case Socks5MethodUserPass:
                throw new SanabiProxyException(proxy.HasCredentials
                    ? "SOCKS5 proxy accepts none of our authentication methods."
                    : "SOCKS5 proxy requires a username/password.");
            default:
                throw new SanabiProxyException($"SOCKS5 proxy chose an unsupported authentication method ({choice[1]}).");
        }

        // RFC 1929 username/password sub-negotiation.
        var username = Encoding.UTF8.GetBytes(proxy.Username);
        var password = Encoding.UTF8.GetBytes(proxy.Password);

        using var request = new MemoryStream();
        request.WriteByte(1);
        request.WriteByte((byte)username.Length);
        request.Write(username);
        request.WriteByte((byte)password.Length);
        request.Write(password);

        await stream.WriteAsync(request.ToArray(), cancel).ConfigureAwait(false);

        var status = new byte[2];
        await ReadExactlyAsync(stream, status, cancel).ConfigureAwait(false);

        if (status[1] != 0)
            throw new SanabiProxyException("SOCKS5 proxy rejected the username/password.");
    }

    /// <returns>The bound address from the reply (for UDP ASSOCIATE, where to send datagrams).</returns>
    private static async Task<IPEndPoint> Socks5CommandAsync(Stream stream, byte command, string host, int port, CancellationToken cancel)
    {
        using var request = new MemoryStream();
        request.Write([Socks5Version, command, 0]);
        WriteSocksAddress(request, host, port);

        await stream.WriteAsync(request.ToArray(), cancel).ConfigureAwait(false);

        var head = new byte[4];
        await ReadExactlyAsync(stream, head, cancel).ConfigureAwait(false);

        if (head[0] != Socks5Version)
            throw new SanabiProxyException("Proxy didn't answer like a SOCKS5 server.");

        if (head[1] != 0)
            throw new SanabiProxyException($"SOCKS5 proxy refused the request: {DescribeSocks5Reply(head[1])}.");

        IPAddress boundAddress;
        switch (head[3])
        {
            case AddressTypeIpv4:
            case AddressTypeIpv6:
                var addressBytes = new byte[head[3] == AddressTypeIpv4 ? 4 : 16];
                await ReadExactlyAsync(stream, addressBytes, cancel).ConfigureAwait(false);
                boundAddress = new IPAddress(addressBytes);
                break;
            case AddressTypeDomain:
                var length = new byte[1];
                await ReadExactlyAsync(stream, length, cancel).ConfigureAwait(false);
                var name = new byte[length[0]];
                await ReadExactlyAsync(stream, name, cancel).ConfigureAwait(false);
                boundAddress = (await Dns.GetHostAddressesAsync(Encoding.ASCII.GetString(name), cancel).ConfigureAwait(false))
                    .FirstOrDefault() ?? IPAddress.Any;
                break;
            default:
                throw new SanabiProxyException($"SOCKS5 proxy sent an unknown address type ({head[3]}).");
        }

        var portBytes = new byte[2];
        await ReadExactlyAsync(stream, portBytes, cancel).ConfigureAwait(false);

        return new IPEndPoint(boundAddress, BinaryPrimitives.ReadUInt16BigEndian(portBytes));
    }

    private static void WriteSocksAddress(Stream target, string host, int port)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            target.WriteByte(address.AddressFamily == AddressFamily.InterNetworkV6 ? AddressTypeIpv6 : AddressTypeIpv4);
            target.Write(address.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255)
                throw new SanabiProxyException($"Hostname '{host}' is too long for SOCKS5.");

            target.WriteByte(AddressTypeDomain);
            target.WriteByte((byte)name.Length);
            target.Write(name);
        }

        WritePort(target, port);
    }

    private static void WritePort(Stream target, int port)
    {
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        target.Write(portBytes);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancel).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new SanabiProxyException("Proxy closed the connection mid-handshake.", e);
        }
        catch (IOException e)
        {
            throw new SanabiProxyException($"Proxy connection failed mid-handshake: {e.Message}", e);
        }
    }

    private static string DescribeSocks5Reply(byte reply) => reply switch
    {
        1 => "general failure",
        2 => "not allowed by ruleset",
        3 => "network unreachable",
        4 => "host unreachable",
        5 => "connection refused",
        6 => "TTL expired",
        7 => "command not supported (the proxy may not support UDP)",
        8 => "address type not supported",
        _ => $"code {reply}",
    };
}

/// <summary>
///     A live SOCKS5 UDP association: datagrams go to <see cref="RelayEndPoint"/> while <see cref="Control"/>
///         stays open.
/// </summary>
public sealed class SanabiSocks5UdpAssociation : IAsyncDisposable
{
    public Stream Control { get; }
    public IPEndPoint RelayEndPoint { get; }

    public SanabiSocks5UdpAssociation(Stream control, IPEndPoint relayEndPoint)
    {
        Control = control;
        RelayEndPoint = relayEndPoint;
    }

    public ValueTask DisposeAsync() => Control.DisposeAsync();
}
