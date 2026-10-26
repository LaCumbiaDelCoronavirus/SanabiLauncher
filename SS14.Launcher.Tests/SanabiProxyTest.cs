using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Sanabi.Framework.Data;
using Sanabi.Framework.Proxy;
using SS14.Launcher.Models.SanabiProxy;

namespace SS14.Launcher.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class SanabiProxyTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static SanabiProxyEndpoint Endpoint(SanabiProxyProtocol protocol, int port, string username = "", string password = "")
        => new() { Protocol = protocol, Host = "127.0.0.1", Port = port, Username = username, Password = password };

    // PROBES

    [Test]
    public async Task ProbeSucceedsThroughSocks5WithCredentials()
    {
        await using var proxy = new FakeSocks5Server { RequiredUsername = "user", RequiredPassword = "hunter2" };

        var error = await SanabiProxyProbe.ProbeEndpointAsync(
            Endpoint(SanabiProxyProtocol.Socks5, proxy.Port, "user", "hunter2"), requireUdp: true, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(proxy.ConnectTargets, Does.Contain($"{SanabiGlobal.ProxyProbeHost}:{SanabiGlobal.ProxyProbePort}"),
            "The probe should tunnel to the probe host by name, so the proxy (not us) resolves it.");
    }

    [Test]
    public async Task ProbeFailsWithWrongSocks5Password()
    {
        await using var proxy = new FakeSocks5Server { RequiredUsername = "user", RequiredPassword = "hunter2" };

        var error = await SanabiProxyProbe.ProbeEndpointAsync(
            Endpoint(SanabiProxyProtocol.Socks5, proxy.Port, "user", "wrong"), requireUdp: false, CancellationToken.None);

        Assert.That(error, Does.Contain("rejected the username/password"));
    }

    [Test]
    public async Task ProbeFailsWhenGameProxyCantCarryUdp()
    {
        await using var proxy = new FakeSocks5Server { SupportsUdp = false };
        var endpoint = Endpoint(SanabiProxyProtocol.Socks5, proxy.Port);

        // Negative control: the same proxy passes when UDP isn't asked for, so the failure below is about UDP.
        Assert.That(await SanabiProxyProbe.ProbeEndpointAsync(endpoint, requireUdp: false, CancellationToken.None), Is.Null);

        var error = await SanabiProxyProbe.ProbeEndpointAsync(endpoint, requireUdp: true, CancellationToken.None);
        Assert.That(error, Does.Contain("command not supported"));
    }

    [Test]
    public async Task ProbeHandlesHttpConnectAuthentication()
    {
        await using var proxy = new FakeHttpConnectProxy { RequiredUsername = "user", RequiredPassword = "hunter2" };

        Assert.That(await SanabiProxyProbe.ProbeEndpointAsync(
            Endpoint(SanabiProxyProtocol.Http, proxy.Port, "user", "hunter2"), requireUdp: false, CancellationToken.None), Is.Null);

        Assert.That(await SanabiProxyProbe.ProbeEndpointAsync(
            Endpoint(SanabiProxyProtocol.Http, proxy.Port), requireUdp: false, CancellationToken.None),
            Does.Contain("requires a username/password"));
    }

    [Test]
    public async Task ProbeFailsWhenProxyIsUnreachable()
    {
        // Grab a free port, then close it, so nothing is listening there.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await SanabiProxyProbe.ProbeEndpointAsync(Endpoint(SanabiProxyProtocol.Socks5, port), requireUdp: false, CancellationToken.None);

        Assert.That(error, Does.Contain("Couldn't reach proxy"));
    }

    // UDP RELAY

    [Test]
    public async Task RelayCarriesGameUdpBothWays()
    {
        await using var proxy = new FakeSocks5Server();
        using var server = new UdpEchoServer();

        await using var relay = await SanabiSocks5UdpRelay.StartAsync(
            Endpoint(SanabiProxyProtocol.Socks5, proxy.Port), "127.0.0.1", server.EndPoint.Port, CancellationToken.None);

        using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        Assert.That(await SendAndReceiveAsync(game, relay.LocalEndPoint, "hello"), Is.EqualTo("hello"));
        Assert.That(proxy.UdpDestinations, Does.Contain($"127.0.0.1:{server.EndPoint.Port}"));
        Assert.That(relay.LocalEndPoint.Address, Is.EqualTo(IPAddress.Loopback), "The game must only ever be given a loopback address.");
    }

    [Test]
    public async Task RelaySendsHostnamesForTheProxyToResolve()
    {
        await using var proxy = new FakeSocks5Server();
        using var server = new UdpEchoServer();

        await using var relay = await SanabiSocks5UdpRelay.StartAsync(
            Endpoint(SanabiProxyProtocol.Socks5, proxy.Port), "game.example.test", server.EndPoint.Port, CancellationToken.None);

        using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        Assert.That(await SendAndReceiveAsync(game, relay.LocalEndPoint, "hi"), Is.EqualTo("hi"));
        Assert.That(proxy.UdpDestinations, Does.Contain($"game.example.test:{server.EndPoint.Port}"));
    }

    /// <summary>
    ///     Fail-closed: once the proxy drops the association, not a single datagram may go out until a new one
    ///         is up. The same send succeeding before the drop and after recovery is the control.
    /// </summary>
    [Test]
    public async Task RelayStopsSendingWhileAssociationIsDown()
    {
        await using var proxy = new FakeSocks5Server();
        using var server = new UdpEchoServer();

        await using var relay = await SanabiSocks5UdpRelay.StartAsync(
            Endpoint(SanabiProxyProtocol.Socks5, proxy.Port), "127.0.0.1", server.EndPoint.Port, CancellationToken.None);

        var lost = new TaskCompletionSource();
        var restored = new TaskCompletionSource();
        relay.AssociationLost += _ => lost.TrySetResult();
        relay.AssociationRestored += () => restored.TrySetResult();

        using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.That(await SendAndReceiveAsync(game, relay.LocalEndPoint, "before"), Is.EqualTo("before"));

        proxy.RefuseUdpAssociations = true;
        proxy.DropUdpAssociations();
        await lost.Task.WaitAsync(Timeout);

        var sentBefore = proxy.ClientDatagramsReceived;
        await game.SendAsync("during"u8.ToArray(), relay.LocalEndPoint);
        await Task.Delay(300);

        Assert.That(proxy.ClientDatagramsReceived, Is.EqualTo(sentBefore), "A datagram left the relay with no association.");
        Assert.That(relay.IsAssociated, Is.False);

        proxy.RefuseUdpAssociations = false;
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(await SendAndReceiveAsync(game, relay.LocalEndPoint, "after"), Is.EqualTo("after"));
    }

    [Test]
    public void UdpHeaderRoundTrips()
    {
        var header = SanabiProxyClient.BuildUdpHeader("10.1.2.3", 1212);
        var datagram = header.Concat("payload"u8.ToArray()).ToArray();

        Assert.That(header, Is.EqualTo(new byte[] { 0, 0, 0, 1, 10, 1, 2, 3, 0x04, 0xBC }));
        Assert.That(SanabiProxyClient.ParseUdpHeader(datagram), Is.EqualTo(header.Length));

        // Fragments (FRAG != 0) aren't reassembled; they must be dropped, not passed on as garbage.
        datagram[2] = 1;
        Assert.That(SanabiProxyClient.ParseUdpHeader(datagram), Is.EqualTo(-1));
    }

    // LAUNCHER HTTP

    /// <summary>
    ///     The real thing: the launcher's handler stack, through a SOCKS5 proxy that needs credentials. Proves
    ///         the per-request proxy lookup and the dynamic credentials work with SocketsHttpHandler.
    /// </summary>
    [Test]
    public async Task LauncherHttpGoesThroughSocks5Proxy()
    {
        await using var proxy = new FakeSocks5Server
        {
            RequiredUsername = "user",
            RequiredPassword = "hunter2",
            OnTunnel = ServeOkAsync,
        };

        var gate = new StubGate { LauncherProxy = Endpoint(SanabiProxyProtocol.Socks5, proxy.Port, "user", "hunter2") };
        using var http = SanabiProxyHttp.CreateHttpClient(gate);

        var body = await http.GetStringAsync("http://sanabi-proxy-test.invalid/status");

        Assert.That(body, Is.EqualTo("ok"));
        Assert.That(proxy.ConnectTargets, Does.Contain("sanabi-proxy-test.invalid:80"),
            "The hostname should reach the proxy unresolved (no local DNS).");
    }

    [Test]
    public async Task GuardBlocksEveryRequestWhileProfileIsDown()
    {
        var inner = new CountingHandler();
        var gate = new StubGate { Blocked = true };
        using var http = new HttpClient(SanabiProxyHttp.CreateHandler(gate, inner));

        Assert.ThrowsAsync<SanabiProxyBlockedException>(() => http.GetAsync("http://example.invalid/"));
        Assert.That(inner.Calls, Is.Zero, "A blocked request reached the network handler.");
    }

    /// <summary>
    ///     Negative control for the test above: with the gate open, the same request does reach the inner handler,
    ///         so "zero calls" there means the guard stopped it, not that the stub can't be reached.
    /// </summary>
    [Test]
    public async Task GuardPassesRequestsWhenAllowed()
    {
        var inner = new CountingHandler();
        using var http = new HttpClient(SanabiProxyHttp.CreateHandler(new StubGate(), inner));

        await http.GetAsync("http://example.invalid/");

        Assert.That(inner.Calls, Is.EqualTo(1));
    }

    [Test]
    public void WebProxyFollowsTheActiveProfile()
    {
        var gate = new StubGate();
        var webProxy = new SanabiLauncherWebProxy(gate);
        var destination = new Uri("https://hub.spacestation14.com/");

        gate.LauncherProxy = Endpoint(SanabiProxyProtocol.Socks5, 1080, "user", "hunter2");
        Assert.That(webProxy.GetProxy(destination), Is.EqualTo(new Uri("socks5://127.0.0.1:1080")));
        Assert.That(webProxy.IsBypassed(destination), Is.False);

        var credential = webProxy.Credentials!.GetCredential(new Uri("socks5://127.0.0.1:1080"), "socks5");
        Assert.That(credential?.UserName, Is.EqualTo("user"));
        Assert.That(webProxy.Credentials.GetCredential(new Uri("socks5://10.0.0.1:1080"), "socks5"), Is.Null,
            "Credentials must only be handed to the proxy they belong to.");

        // Switching profiles takes effect on the very next request, with no new handler.
        gate.LauncherProxy = Endpoint(SanabiProxyProtocol.Http, 3128);
        Assert.That(webProxy.GetProxy(destination), Is.EqualTo(new Uri("http://127.0.0.1:3128")));
    }

    // PROFILES

    [Test]
    public void ProfilesRoundTripThroughTheCVar()
    {
        var profile = new SanabiProxyProfile
        {
            Name = "Test",
            Launcher = Endpoint(SanabiProxyProtocol.Https, 443, "user", "hunter2"),
            Game = Endpoint(SanabiProxyProtocol.Socks5, 1080),
        };

        var parsed = SanabiProxyProfilesCVar.Parse(SanabiProxyProfilesCVar.Serialize([profile])).Single();

        Assert.That(parsed.Id, Is.EqualTo(profile.Id));
        Assert.That(parsed.Launcher!.Protocol, Is.EqualTo(SanabiProxyProtocol.Https));
        Assert.That(parsed.Launcher.Password, Is.EqualTo("hunter2"));
        Assert.That(parsed.Game!.Port, Is.EqualTo(1080));
        Assert.That(SanabiProxyProfilesCVar.Serialize([profile]), Does.Contain("\"Socks5\""), "Protocols should be stored by name.");
    }

    [Test]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{\"oops\": 1}")]
    public void CorruptProfilesCVarMeansNoProfiles(string raw)
    {
        Assert.That(SanabiProxyProfilesCVar.Parse(raw), Is.Empty);
    }

    [Test]
    public void GameProxyMustBeSocks5()
    {
        var profile = new SanabiProxyProfile { Name = "Test", Game = Endpoint(SanabiProxyProtocol.Http, 3128) };

        Assert.That(profile.IsValid(out var error), Is.False);
        Assert.That(error, Is.EqualTo(SanabiProxyValidationError.NoUdp));

        // The same endpoint is fine for the launcher.
        profile = new SanabiProxyProfile { Name = "Test", Launcher = Endpoint(SanabiProxyProtocol.Http, 3128) };
        Assert.That(profile.IsValid(out _), Is.True);
    }

    // HELPERS

    private static async Task<string> SendAndReceiveAsync(UdpClient client, IPEndPoint target, string message)
    {
        await client.SendAsync(Encoding.UTF8.GetBytes(message), target);

        using var cancel = new CancellationTokenSource(Timeout);
        var reply = await client.ReceiveAsync(cancel.Token);

        Assert.That(reply.RemoteEndPoint, Is.EqualTo(target), "Replies must appear to come from the relay's local endpoint.");
        return Encoding.UTF8.GetString(reply.Buffer);
    }

    private static async Task ServeOkAsync(Stream tunnel)
    {
        // Read the request headers, then answer.
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await tunnel.ReadExactlyAsync(one);
            request.Append((char)one[0]);
        }

        await tunnel.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
        await tunnel.FlushAsync();
    }

    private sealed class UdpEchoServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cancel = new();

        public UdpEchoServer()
        {
            _ = Task.Run(async () =>
            {
                while (!_cancel.IsCancellationRequested)
                {
                    try
                    {
                        var received = await _udp.ReceiveAsync(_cancel.Token);
                        await _udp.SendAsync(received.Buffer, received.RemoteEndPoint, _cancel.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

        public void Dispose()
        {
            _cancel.Cancel();
            _udp.Dispose();
        }
    }

    private sealed class StubGate : ISanabiProxyGate
    {
        public SanabiProxyEndpoint? LauncherProxy { get; set; }
        public bool Blocked { get; init; }
        public bool IsProxyActive => LauncherProxy != null || Blocked;

        public ValueTask EnsureTrafficAllowedAsync(CancellationToken cancel)
            => Blocked ? ValueTask.FromException(new SanabiProxyBlockedException("Test", "down")) : ValueTask.CompletedTask;

        public void ReportPossibleFailure()
        {
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
