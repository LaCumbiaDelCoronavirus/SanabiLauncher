using System.Runtime.InteropServices;
using JetBrains.Annotations;
using Sanabi.Framework.Game.Patches;

namespace Sanabi.Framework.Data;

/// <summary>
///     Contains definitions for all SanabiLauncher-specific configuration values.
///         This is passed from launcher -> loader
/// </summary>
[UsedImplicitly]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public record struct SanabiConfig()
{
    public static SanabiConfig ProcessConfig = new();

    public PatchRunLevel PatchRunLevel = PatchRunLevel.None;

    public bool RunHwidPatch = true;

    public bool LoadInternalMods = false;

    public bool LoadExternalMods = false;
}
