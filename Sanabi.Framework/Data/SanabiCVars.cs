using JetBrains.Annotations;
using SS14.Common.Data.CVars;

namespace SS14.Launcher.Models.Data;

/// <summary>
///     Contains definitions for all SanabiLauncher-specific configuration values.
/// </summary>
[UsedImplicitly]
public static partial class SanabiCVars
{
    /// <summary>
    ///     Include "SS14-Launcher-Fingerprint" and this launcher's fingerprint, in
    ///         DefaultRequestHeaders for *every* single HTTP query we make?
    ///
    ///     This may be a detection vector.
    /// </summary>
    public static readonly CVarDef<bool> PassFingerprint = CVarDef.Create("PassFingerprint", true);

    /// <summary>
    ///     Generates a random value for "SS14-Launcher-Fingerprint" to be used for the duration of every launch.
    ///         Original fingerprint is still saved and will be used when this CVar is turned
    ///         off.
    ///
    ///     Not passed if <see cref="PassFingerprint"/> is off.
    /// </summary>
    public static readonly CVarDef<bool> PassSpoofedFingerprint = CVarDef.Create("SpoofFingerprint", false);

    /// <summary>
    ///     Whenever logging in on any account, generates a new spoofed fingerprint to be used if set to pass
    ///         spoofed fingerprints to servers.
    /// </summary>
    public static readonly CVarDef<bool> SpoofFingerprintOnLogin = CVarDef.Create("SpoofFingerprintOnLogin", true);

    /// <summary>
    ///     Allow sending HWID to server?
    /// </summary>
    public static readonly CVarDef<bool> AllowHwid = CVarDef.Create("AllowHwid", false);
}
