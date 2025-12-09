using System.Runtime.InteropServices;
using JetBrains.Annotations;
using Sanabi.Framework.Game.Patches;

namespace Sanabi.Framework.Data;

/// <summary>
///     Contains definitions for all SanabiLauncher-specific configuration values.
///         This is passed from launcher -> loader
///
///     This must be an unmanaged blittable strict; i.e. one with a fixed size,
///         so that it can be easily passed from launcher -> loader via IPC.
/// </summary>
[UsedImplicitly]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public record struct SanabiConfig()
{
    public static SanabiConfig ProcessConfig = new();

    public PatchRunLevel PatchRunLevel = PatchRunLevel.None;

    public bool RunHwidPatch = true;

    public ulong HwidPatchSeed = 1ul;

    public bool LoadInternalMods = false;

    public bool LoadExternalMods = false;
}

public static class SanabiConfigExtensions
{
    /// <summary>
    ///     Configures the given <see cref="SanabiConfig"/>
    ///         according to the CVars of the given DataManager.
    /// </summary>
    /// <returns>The configured <see cref="SanabiConfig"/>.</returns>
    public static SanabiConfig Configure(this SanabiConfig config, dynamic dataManager)
    {
        config.PatchRunLevel = dataManager.GetCVar(SanabiCVars.PatchingEnabled) ?
            (dataManager.GetCVar(SanabiCVars.PatchingLevel) ? PatchRunLevel.Full : PatchRunLevel.Engine) :
            PatchRunLevel.None;

        config.RunHwidPatch = dataManager.GetCVar(SanabiCVars.HwidPatchEnabled);
        config.HwidPatchSeed = BitConverter.ToUInt64(BitConverter.GetBytes(dataManager.GetCVar(SanabiCVars.SpoofedHwidSeed)), 0);
        config.LoadInternalMods = dataManager.GetCVar(SanabiCVars.LoadInternalMods);
        config.LoadExternalMods = dataManager.GetCVar(SanabiCVars.LoadExternalMods);

        return config;
    }

    /// <summary>
    ///     Generates a random ulong.
    /// </summary>
    public static ulong RegenerateHwidSeed()
    {
        var newUlValueBytes = (Span<byte>)stackalloc byte[8];
        new Random().NextBytes(newUlValueBytes);

        return BitConverter.ToUInt64(newUlValueBytes);
    }
}
