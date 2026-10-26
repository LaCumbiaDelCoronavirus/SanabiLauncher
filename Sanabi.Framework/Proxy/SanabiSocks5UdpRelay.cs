using System.Net;
using System.Net.Sockets;
using Sanabi.Framework.Misc;

namespace Sanabi.Framework.Proxy;

/// <summary>
///     Carries one game connection's UDP through a SOCKS5 proxy.
///
///     The engine's networking (Lidgren) sends through raw <c>sendto</c>/<c>recvfrom</c> P/Invokes, which no
///         managed hook can intercept. So instead the game is pointed at <see cref="LocalEndPoint"/> (loopback),
///         and this relay wraps each datagram in a SOCKS5 UDP header for the real destination, and unwraps the
///         replies. The game never learns the real server address, so it has no direct route to fall back on:
///         if the association drops, traffic simply stops (fail-closed) until it is re-established.
/// </summary>
public sealed class SanabiSocks5UdpRelay : IAsyncDisposable
{
    private const int MaxDatagramSize = 65535;

    // SIO_UDP_CONNRESET: stops Windows reporting a stray ICMP port-unreachable as an error on the next receive.
    private const int SioUdpConnectionReset = unchecked((int)0x9800000C);

    private static readonly TimeSpan ReassociateDelay = TimeSpan.FromSeconds(3);

    private readonly SanabiProxyEndpoint _proxy;
    private readonly byte[] _header;
    private readonly IPAddress? _destinationAddress;
    private readonly Socket _localSocket;
    private readonly Socket _proxySocket;
    private readonly CancellationTokenSource _cancelSource = new();

    private volatile SanabiSocks5UdpAssociation? _association;
    private volatile IPEndPoint? _gameEndPoint;

    /// <summary>
    ///     Loopback endpoint to give the game as its server address.
    /// </summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    ///     Whether datagrams are currently flowing. False while the association is being re-established.
    /// </summary>
    public bool IsAssociated => _association != null;

    /// <summary>
    ///     The proxy dropped the association. Raised on a thread-pool thread.
    /// </summary>
    public event Action<Exception?>? AssociationLost;

    /// <summary>
    ///     A dropped association was re-established. Raised on a thread-pool thread.
    /// </summary>
    public event Action? AssociationRestored;

    private SanabiSocks5UdpRelay(SanabiProxyEndpoint proxy, string destinationHost, int destinationPort, SanabiSocks5UdpAssociation association)
    {
        _proxy = proxy;
        _header = SanabiProxyClient.BuildUdpHeader(destinationHost, destinationPort);
        _destinationAddress = IPAddress.TryParse(destinationHost, out var address) ? address : null;
        _association = association;

        _localSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _localSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        LocalEndPoint = (IPEndPoint)_localSocket.LocalEndPoint!;

        // Dual-mode, so one socket can follow the relay across IPv4/IPv6 if a re-association moves it.
        _proxySocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        _proxySocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        if (OperatingSystem.IsWindows())
        {
            _localSocket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
            _proxySocket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
        }

        var cancel = _cancelSource.Token;
        _ = Task.Run(() => ForwardGameToProxyAsync(cancel), cancel);
        _ = Task.Run(() => ForwardProxyToGameAsync(cancel), cancel);
        _ = Task.Run(() => SuperviseAssociationAsync(association, cancel), cancel);
    }

    /// <summary>
    ///     Associates with <paramref name="proxy"/> and starts relaying to
    ///         <paramref name="destinationHost"/>:<paramref name="destinationPort"/>. A hostname is resolved by the
    ///         proxy, not locally.
    /// </summary>
    /// <exception cref="SanabiProxyException">The proxy is unreachable or refused UDP.</exception>
    public static async Task<SanabiSocks5UdpRelay> StartAsync(
        SanabiProxyEndpoint proxy,
        string destinationHost,
        int destinationPort,
        CancellationToken cancel)
    {
        var association = await SanabiProxyClient.Socks5UdpAssociateAsync(proxy, cancel).ConfigureAwait(false);

        try
        {
            return new SanabiSocks5UdpRelay(proxy, destinationHost, destinationPort, association);
        }
        catch
        {
            await association.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ForwardGameToProxyAsync(CancellationToken cancel)
    {
        // The header sits in front of the receive area, so a received datagram is sent on without copying.
        var buffer = new byte[_header.Length + MaxDatagramSize];
        _header.CopyTo(buffer, 0);

        EndPoint anyEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (!cancel.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _localSocket.ReceiveFromAsync(buffer.AsMemory(_header.Length), SocketFlags.None, anyEndPoint, cancel)
                    .ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            _gameEndPoint = (IPEndPoint)received.RemoteEndPoint;

            // No association: drop. Never send anywhere else.
            if (_association is not { } association)
                continue;

            try
            {
                await _proxySocket.SendToAsync(
                    buffer.AsMemory(0, _header.Length + received.ReceivedBytes),
                    SocketFlags.None,
                    association.RelayEndPoint,
                    cancel).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Oversized or transiently unroutable datagram; UDP is lossy anyway.
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task ForwardProxyToGameAsync(CancellationToken cancel)
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint anyEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!cancel.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _proxySocket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, cancel)
                    .ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            if (_association is not { } association || _gameEndPoint is not { } gameEndPoint)
                continue;

            // Only the relay may talk to us; anything else reaching this port is not from our proxy.
            if (!SanabiProxyClient.Normalize((IPEndPoint)received.RemoteEndPoint).Equals(association.RelayEndPoint))
                continue;

            var datagram = buffer.AsSpan(0, received.ReceivedBytes);
            var payloadOffset = SanabiProxyClient.ParseUdpHeader(datagram);
            if (payloadOffset < 0 || !IsFromDestination(datagram))
                continue;

            try
            {
                await _localSocket.SendToAsync(
                    buffer.AsMemory(payloadOffset, received.ReceivedBytes - payloadOffset),
                    SocketFlags.None,
                    gameEndPoint,
                    cancel).ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    ///     When the destination is an IP, drops replies the proxy says came from somewhere else. With a hostname
    ///         destination, replies carry whatever IP the proxy resolved it to, so there's nothing to compare.
    /// </summary>
    private bool IsFromDestination(ReadOnlySpan<byte> datagram)
    {
        if (_destinationAddress == null)
            return true;

        var addressBytes = datagram[3] switch
        {
            SanabiProxyClient.AddressTypeIpv4 => datagram.Slice(4, 4),
            SanabiProxyClient.AddressTypeIpv6 => datagram.Slice(4, 16),
            _ => ReadOnlySpan<byte>.Empty,
        };

        if (addressBytes.IsEmpty)
            return false;

        var source = new IPAddress(addressBytes);
        if (source.IsIPv4MappedToIPv6)
            source = source.MapToIPv4();

        return source.Equals(_destinationAddress.IsIPv4MappedToIPv6 ? _destinationAddress.MapToIPv4() : _destinationAddress);
    }

    /// <summary>
    ///     Watches the control connection; when it closes, the proxy has torn down the association, so stop
    ///         forwarding and keep trying to get a new one until disposed.
    /// </summary>
    private async Task SuperviseAssociationAsync(SanabiSocks5UdpAssociation association, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            Exception? error = null;
            try
            {
                var discard = new byte[64];
                while (await association.Control.ReadAsync(discard, cancel).ConfigureAwait(false) > 0)
                {
                    // A well-behaved server sends nothing more on the control connection.
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                error = e;
            }

            if (cancel.IsCancellationRequested)
                return;

            _association = null;
            await association.DisposeAsync().ConfigureAwait(false);
            SanabiLogger.LogWarn($"SOCKS5 UDP association with {_proxy.DisplayString} was lost; game traffic is paused. {error?.Message}");
            AssociationLost?.Invoke(error);

            while (true)
            {
                try
                {
                    await Task.Delay(ReassociateDelay, cancel).ConfigureAwait(false);
                    association = await SanabiProxyClient.Socks5UdpAssociateAsync(_proxy, cancel).ConfigureAwait(false);

                    // Disposed while we were associating: don't leave the new control connection behind.
                    if (cancel.IsCancellationRequested)
                    {
                        await association.DisposeAsync().ConfigureAwait(false);
                        return;
                    }

                    break;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Still down; keep trying.
                }
            }

            _association = association;
            SanabiLogger.LogInfo($"SOCKS5 UDP association with {_proxy.DisplayString} re-established.");
            AssociationRestored?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancelSource.IsCancellationRequested)
            return;

        await _cancelSource.CancelAsync().ConfigureAwait(false);

        _localSocket.Dispose();
        _proxySocket.Dispose();

        if (_association is { } association)
        {
            _association = null;
            await association.DisposeAsync().ConfigureAwait(false);
        }

        // _cancelSource is deliberately not disposed: the loops may still be observing its token as they unwind.
    }
}
