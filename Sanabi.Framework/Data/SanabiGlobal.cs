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
    "SANABI-220-7"
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
}
