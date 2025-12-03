using Sanabi.Framework.Game.Managers;
using HarmonyLib;
using Sanabi.Framework.Patching;
using Sanabi.Framework.Data;
using System.Security.Cryptography;

namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Intercepts HWId and spoofs it.
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
            throw new InvalidOperationException("Couldn't resolve HWId implementation!");

        var hwidByteLength = (int?)PatchHelpers.GetConstantFieldValue(hwidType, "LengthHwid") ?? 32;

        SpoofedHwid = new byte[hwidByteLength];
        RandomNumberGenerator.Fill(SpoofedHwid);

        PatchHelpers.PatchMethod(
            hwidType,
            "GetLegacy",
            PrefixLegacy,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            hwidType,
            "GetModern",
            PrefixModern,
            HarmonyPatchType.Prefix
        );
    }

    private static bool PrefixLegacy(ref dynamic __instance, ref dynamic __result)
    {
        __result = SpoofedHwid;
        return false;
    }

    private static bool PrefixModern(ref dynamic __instance, ref dynamic __result)
    {
        __result = SpoofedHwid;
        return false;
    }
}
