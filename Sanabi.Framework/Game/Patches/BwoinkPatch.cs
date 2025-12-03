using HarmonyLib;
using Sanabi.Framework.Data;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Disables pop-up from bwoink.
/// </summary>
public static class BwoinkPatch
{
    [PatchEntry(PatchRunLevel.Content)]
    public static void Patch()
    {
        if (!SanabiConfig.ProcessConfig.LoadInternalMods)
            return;

        PatchHelpers.PatchMethod(
            "Content.Client.UserInterface.Systems.Bwoink",
            "ReceivedBwoink",
            Prefix,
            HarmonyPatchType.Prefix
        );
    }

    private static bool Prefix(ref dynamic __instance, dynamic? sender, dynamic message)
    {
        __instance.UnreadAHelpReceived();
        Console.WriteLine("Fuck off! Bwoink.");

        return false;
    }
}
