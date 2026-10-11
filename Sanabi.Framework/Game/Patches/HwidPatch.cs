using Sanabi.Framework.Game.Managers;
using HarmonyLib;
using Sanabi.Framework.Patching;
using Sanabi.Framework.Data;
using Sanabi.Framework.Misc;

namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Intercepts HWId read and spoofs it.
/// </summary>
public static class HwidPatch
{
    public static bool Enabled => SanabiConfig.ProcessConfig.RunHwidPatch;
    private static byte[] SpoofedHwid = [];

    [PatchEntry(PatchRunLevel.Engine)]
    public static void Patch()
    {
        if (!Enabled)
            return;

        if (!ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.HWId.BasicHWId", out var hwidType))
        {
            SanabiLogger.LogWarn("HwidPatch: couldn't resolve HWId implementation, skipping patch!");
            return;
        }

        var hwidByteLength = (int?)PatchHelpers.GetConstantFieldValue(hwidType, "LengthHwid") ?? 32;

        SpoofedHwid = new byte[hwidByteLength];
        new Pcg32(SanabiConfig.ProcessConfig.HwidPatchSeed).NextBytes(SpoofedHwid);

        PatchHelpers.PatchMethod(
            hwidType,
            "GetLegacy",
            PrefixLegacyOrModern,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            hwidType,
            "GetModern",
            PrefixLegacyOrModern,
            HarmonyPatchType.Prefix
        );
    }

    // Works for both legcay&modern hwid at time of writing
    private static bool PrefixLegacyOrModern(ref dynamic __instance, ref dynamic __result)
    {
        __result = SpoofedHwid;
        return false;
    }
}
