using SS14.Common.Data.CVars;

namespace Sanabi.Framework.Data;

// These are CVars relating to patches.

public static partial class SanabiCVars
{
    /// <summary>
    ///     Do we include any patches at all?
    /// </summary>
    public static readonly CVarDef<bool> PatchingEnabled = CVarDef.Create("PatchingEnabled", false);

    /// <summary>
    ///     Do we patch content+engine, or only engine?
    /// </summary>
    public static readonly CVarDef<bool> PatchingLevel = CVarDef.Create("PatchingLevel", false);

    /// <summary>
    ///     If patching is enabled for it, do we patch
    ///         the HWID spoofer?
    /// </summary>
    public static readonly CVarDef<bool> HwidPatchEnabled = CVarDef.Create("HwidPatchEnabled", true);

    /// <summary>
    ///     Is the patch for loading better fullscreen (patch normal RT fullscreen into borderless windowed)
    ///         loaded?
    /// </summary>
    public static readonly CVarDef<bool> BetterFullscreenPatchEnabled = CVarDef.Create("BetterFullscreenPatchEnabled", false);

    /// <summary>
    ///     Content.Client.Administration.Systems.AdminInfoSystem
    /// </summary>
    public static readonly CVarDef<bool> UserDataPatchEnabled = CVarDef.Create("UserDataPatchEnabled", false);

    /// <summary>
    ///     Load internal patches that come with the launcher?
    /// </summary>
    public static readonly CVarDef<bool> LoadInternalMods = CVarDef.Create("LoadInternalMods", false);

    /// <summary>
    ///     Load external `.dll`'s that are in the launcher's
    ///         mods directory?
    /// </summary>
    public static readonly CVarDef<bool> LoadExternalMods = CVarDef.Create("LoadExternalMods", false);

    /// <summary>
    ///     Use harmony on debug mode on next load?
    /// </summary>
    public static readonly CVarDef<bool> HarmonyDebug = CVarDef.Create("HarmonyDebug", false);

    /// <summary>
    ///     When launching, should we wait for a debugger to attach to the process?
    /// </summary>
    public static readonly CVarDef<bool> WaitForDebugger = CVarDef.Create("WaitForDebugger", false);

    /// <summary>
    ///     The external mods the user enabled, stored as a JSON array of their names (see
    ///         <see cref="EnabledExternalModsCVar"/>). Replaces the old packed-bitflag <c>long</c> so selection
    ///         is keyed by name rather than directory order.
    /// </summary>
    public static readonly CVarDef<string> EnabledExternalMods = CVarDef.Create("EnabledExternalMods", "");
}
