namespace Sanabi.Framework.Data;

/// <summary>
///     Global variables for Sanabi-exclusive content
/// </summary>
public static class SanabiGlobal
{
    /// <summary>
    ///     Maximum number of queries done when pinging a server.
    /// </summary>
    public const int MaximumPingQueryAttempts = 6;

    /// <summary>
    ///     Minimum number of queries to a server that must be successful.
    /// </summary>
    public const int MinimumSuccessfulPingQueryAttempts = 3;

    // Do not indent
    public const string FallbackLauncherInfoData = """
{
  "messages": {
    "en-US": [
      "rnd_krik2.ogg [your launcher didn't load properly btw]"
    ]
  },
  "allowedVersions": [
    "ERROR"
  ],
  "overrideAssets": {},
  "changelog": [],
  "changelogMediaUrl": ""
}
""";

    /// <summary>
    ///     Minimum of random amount of time to pass between ping queries.
    ///         This is used as ping interval when random-ping-delay is disabled.
    /// </summary>
    public static readonly TimeSpan MinPingQueryInterval = TimeSpan.FromMilliseconds(50); // 50ms seems to be most successful without taking years

    /// <summary>
    ///     Maximum of random amount of time to pass between ping queries.
    /// </summary>
    public static readonly TimeSpan MaxPingQueryInterval = TimeSpan.FromMilliseconds(85);

    /// <summary>
    ///     Where proxy health probes open their test tunnel to. The auth server, since the launcher talks to it
    ///         anyway; the probe only connects and never sends anything through.
    /// </summary>
    public const string ProxyProbeHost = "auth.spacestation14.com";

    public const int ProxyProbePort = 443;

    /// <summary>
    ///     How long one proxy probe may take before the proxy counts as not working.
    /// </summary>
    public static readonly TimeSpan ProxyProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How often a working proxy profile is re-probed.
    /// </summary>
    public static readonly TimeSpan ProxyHealthyRecheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     How often a failing proxy profile is re-probed, i.e. how quickly connections unblock once it recovers.
    /// </summary>
    public static readonly TimeSpan ProxyFailedRecheckInterval = TimeSpan.FromSeconds(5);
}
