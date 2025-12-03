using SS14.Common.Data.CVars;

namespace SS14.Launcher.Models.Data;

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
    ///     Load internal patches that come with the launcher?
    /// </summary>
    public static readonly CVarDef<bool> LoadInternalMods = CVarDef.Create("LoadInternalMods", false);

    /// <summary>
    ///     Load external `.dll`'s that are in the launcher's
    ///         mods directory?
    /// </summary>
    public static readonly CVarDef<bool> LoadExternalMods = CVarDef.Create("LoadExternalMods", false);
}
