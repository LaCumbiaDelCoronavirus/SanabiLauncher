using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sanabi.Framework.Proxy;

namespace SS14.Launcher.Models.SanabiProxy;

/// <summary>
///     What the launcher's HTTP stack needs from the proxy feature. <see cref="SanabiProxyManager"/> is the real
///         one; tests substitute their own.
/// </summary>
public interface ISanabiProxyGate
{
    /// <summary>
    ///     The proxy launcher HTTP traffic must go through right now, or null to connect directly.
    /// </summary>
    SanabiProxyEndpoint? LauncherProxy { get; }

    /// <summary>
    ///     Whether a proxy profile is in use at all (launcher, game, or both).
    /// </summary>
    bool IsProxyActive { get; }

    /// <summary>
    ///     Returns once traffic may flow. Waits out an in-progress first health check.
    /// </summary>
    /// <exception cref="SanabiProxyBlockedException">The active profile isn't working, so nothing may connect.</exception>
    ValueTask EnsureTrafficAllowedAsync(CancellationToken cancel);

    /// <summary>
    ///     A proxied connection just failed in a way that suggests the proxy is down; re-check it now rather than
    ///         at the next scheduled check.
    /// </summary>
    void ReportPossibleFailure();
}

/// <summary>
///     Thrown instead of connecting while the active proxy profile isn't working. Derives from
///         <see cref="HttpRequestException"/> so every existing "request failed" path handles it unchanged.
/// </summary>
public sealed class SanabiProxyBlockedException : HttpRequestException
{
    public SanabiProxyBlockedException(string profileName, string? reason)
        : base($"Connection blocked: proxy profile '{profileName}' is not working{(reason is { } ? $" ({reason})" : "")}.")
    {
    }
}

public static class SanabiProxyHttp
{
    /// <summary>
    ///     Builds the launcher's shared <see cref="HttpClient"/>: the usual Happy Eyeballs handler, routed through
    ///         whichever launcher proxy <paramref name="gate"/> says is active, behind a guard that refuses to
    ///         send anything while the active profile is down.
    /// </summary>
    public static HttpClient CreateHttpClient(ISanabiProxyGate gate)
        => new(CreateHandler(gate, HappyEyeballsHttp.CreateHttpHandler()));

    /// <summary>
    ///     Wraps <paramref name="innerHandler"/> with the proxy and the guard. Split out so tests can supply the
    ///         inner handler.
    /// </summary>
    public static HttpMessageHandler CreateHandler(ISanabiProxyGate gate, HttpMessageHandler innerHandler)
    {
        if (innerHandler is SocketsHttpHandler socketsHandler)
        {
            // With a proxy set, SocketsHttpHandler hands the proxy's address (not the target's) to its
            // ConnectCallback and then speaks the proxy protocol itself, so Happy Eyeballs keeps working.
            socketsHandler.UseProxy = true;
            socketsHandler.Proxy = new SanabiLauncherWebProxy(gate);
        }

        return new SanabiProxyGuardHandler(gate, innerHandler);
    }
}

/// <summary>
///     An <see cref="IWebProxy"/> that follows the active proxy profile. A handler's proxy can't be swapped
///         after its first request, so instead this one is asked afresh on every request. With no launcher
///         proxy active it defers to the system proxy, exactly as an unconfigured handler would.
/// </summary>
internal sealed class SanabiLauncherWebProxy : IWebProxy
{
    private readonly ISanabiProxyGate _gate;
    private readonly IWebProxy _systemProxy = HttpClient.DefaultProxy;

    public SanabiLauncherWebProxy(ISanabiProxyGate gate)
    {
        _gate = gate;
        Credentials = new DynamicCredentials(this);
    }

    // SocketsHttpHandler reads this once and keeps it, so it must itself look up the current profile's credentials.
    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination)
        => _gate.LauncherProxy is { } proxy ? proxy.ToUri() : _systemProxy.GetProxy(destination);

    public bool IsBypassed(Uri host)
        => _gate.LauncherProxy == null && _systemProxy.IsBypassed(host);

    private sealed class DynamicCredentials(SanabiLauncherWebProxy owner) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (owner._gate.LauncherProxy is { } proxy)
                return proxy.Matches(uri) ? proxy.GetCredential() : null;

            return owner._systemProxy.Credentials?.GetCredential(uri, authType);
        }
    }
}

/// <summary>
///     Refuses every request while the active proxy profile isn't working, so a dead proxy can never mean
///         "connect directly instead".
/// </summary>
internal sealed class SanabiProxyGuardHandler : DelegatingHandler
{
    private readonly ISanabiProxyGate _gate;

    public SanabiProxyGuardHandler(ISanabiProxyGate gate, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _gate = gate;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.EnsureTrafficAllowedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (IsPossibleProxyFailure(e))
        {
            _gate.ReportPossibleFailure();
            throw;
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _gate.EnsureTrafficAllowedAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

        try
        {
            return base.Send(request, cancellationToken);
        }
        catch (HttpRequestException e) when (IsPossibleProxyFailure(e))
        {
            _gate.ReportPossibleFailure();
            throw;
        }
    }

    private bool IsPossibleProxyFailure(HttpRequestException e)
        => _gate.LauncherProxy != null &&
           e.HttpRequestError is HttpRequestError.ProxyTunnelError or HttpRequestError.ConnectionError;
}
