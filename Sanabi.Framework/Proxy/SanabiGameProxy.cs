using System.Net.Http;
using Sanabi.Framework.Data;
using Sanabi.Framework.Misc;

namespace Sanabi.Framework.Proxy;

/// <summary>
///     Game-process half of the proxy feature. The game's UDP never needs touching here: the launcher points
///         the game at a local relay (<see cref="SanabiSocks5UdpRelay"/>) instead of the real server. What's
///         left is the game's own HTTP(S), e.g. the auth server <c>session/join</c> call made while connecting.
/// </summary>
public static class SanabiGameProxy
{
    /// <summary>
    ///     Routes every <see cref="HttpClient"/> in the process that doesn't pick its own proxy through the
    ///         configured game proxy. That covers the engine's <c>HttpClientHolder</c>, whose handler leaves
    ///         <c>Proxy</c> unset. Must run before the engine builds its HTTP clients, i.e. before its entry point.
    ///
    ///     There is no fallback: if the proxy is down, those requests fail rather than go out directly.
    /// </summary>
    public static void Apply(SanabiConfig config)
    {
        if (config.GameProxy is not { } proxy)
            return;

        HttpClient.DefaultProxy = proxy.CreateWebProxy();
        SanabiLogger.LogInfo("Game HTTP traffic is proxied.");
    }
}
