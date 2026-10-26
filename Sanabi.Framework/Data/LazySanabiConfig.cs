namespace Sanabi.Framework.Data;

/// <summary>
///     Lazy way of transferring data in same process.
/// </summary>
public static class LazySanabiConfig
{
    // For launcher process
    /// <inheritdoc cref="SanabiCVars.PingServers"/>
    public static bool PingServers = true;

    /// <inheritdoc cref="SanabiCVars.RandomiseServerPingQueryDelay"/>
    public static bool RandomiseServerPingQueryDelay = true;

    /// <summary>
    ///     Whether a proxy profile is in use. Server pings (ICMP) and their DNS lookups can't go through a proxy,
    ///         so they're skipped while this is set rather than reveal the real address to the server.
    /// </summary>
    public static volatile bool ProxyActive;

    // For loader/game process
    public static string RobustClientExecutable = string.Empty;
}
