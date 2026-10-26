using Sanabi.Framework.Data;

namespace Sanabi.Framework.Proxy;

/// <summary>
///     Health checks for proxy profiles. A probe only opens a tunnel (and, for the game proxy, a UDP
///         association) and closes it again; no application data is sent through it.
/// </summary>
public static class SanabiProxyProbe
{
    /// <summary>
    ///     Probes every proxy in <paramref name="profile"/>.
    /// </summary>
    /// <returns>Null if all of them work, otherwise a short English description of the first failure.</returns>
    public static async Task<string?> ProbeAsync(SanabiProxyProfile profile, CancellationToken cancel)
    {
        if (profile.Launcher is { } launcher &&
            await ProbeEndpointAsync(launcher, requireUdp: false, cancel).ConfigureAwait(false) is { } launcherError)
        {
            return $"Launcher proxy: {launcherError}";
        }

        if (profile.Game is { } game &&
            await ProbeEndpointAsync(game, requireUdp: true, cancel).ConfigureAwait(false) is { } gameError)
        {
            return $"Game proxy: {gameError}";
        }

        return null;
    }

    /// <returns>Null if the endpoint works, otherwise why not.</returns>
    public static async Task<string?> ProbeEndpointAsync(SanabiProxyEndpoint endpoint, bool requireUdp, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(SanabiGlobal.ProxyProbeTimeout);

        try
        {
            await using (await SanabiProxyClient.OpenTunnelAsync(endpoint, SanabiGlobal.ProxyProbeHost, SanabiGlobal.ProxyProbePort, timeout.Token)
                             .ConfigureAwait(false))
            {
            }

            if (requireUdp)
            {
                await using (await SanabiProxyClient.Socks5UdpAssociateAsync(endpoint, timeout.Token).ConfigureAwait(false))
                {
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return $"timed out after {SanabiGlobal.ProxyProbeTimeout.TotalSeconds:0}s";
        }
        catch (SanabiProxyException e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
        {
            return e.Message;
        }
    }
}
