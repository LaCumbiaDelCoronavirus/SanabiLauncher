using JetBrains.Annotations;
using SS14.Common.Data.CVars;

namespace Sanabi.Framework.Data;

/// <summary>
///     Contains definitions for all SanabiLauncher-specific configuration values.
/// </summary>
[UsedImplicitly]
public static partial class SanabiAccountCVars
{
    public const string AuthServersDefaultSerializedNullEscapeValue = "<NO_VALUE_PLEASE_SET_DEFAULT>"; // DO NOT CHANGE EVER

    /// <summary>
    ///     Vertical-bar-separated (which is |) list of auth-server URL sets this uses, with individual fallback URLs separated by commas
    /// </summary>
    public static readonly CVarDef<string> AuthServers = CVarDef.Create("AuthServers", AuthServersDefaultSerializedNullEscapeValue); // intentional so authmanager can handle this bs

    /// <summary>
    ///     Whether to generate a new spoofing seed when setting this account as the active one.
    ///         Set to false when done.
    /// </summary>
    public static readonly CVarDef<bool> ShouldRegenerateSeed = CVarDef.Create("ShouldRegenerateSeed", true);

    /// <summary>
    ///     Seed to be used for generating HWID in <see cref="Game.Patches.HwidPatch"/>, and spoofed fingerprint.
    ///         This is an ulong value bit-interpreted as a long. This is done because SQLite
    ///         is weird with ulong values.
    /// </summary>
    public static readonly CVarDef<long> SpoofingSeed = CVarDef.Create("SpoofingSeed", 1L);

    /// <summary>
    ///     <see cref="Proxy.SanabiProxyProfile.Id"/> of the proxy profile this account uses; empty (or an id that
    ///         no longer exists) means no proxy. The profiles themselves are global: <see cref="SanabiCVars.ProxyProfiles"/>.
    /// </summary>
    public static readonly CVarDef<string> ProxyProfile = CVarDef.Create("ProxyProfile", string.Empty);
}
