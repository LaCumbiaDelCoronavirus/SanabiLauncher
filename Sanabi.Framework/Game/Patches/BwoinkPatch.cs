using HarmonyLib;
using Sanabi.Framework.Game.Managers;
using Sanabi.Framework.Data;
using Sanabi.Framework.Misc;
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

        // Forks may have moved or renamed this, so skip rather than throw
        if (!ReflectionManager.TryGetTypeByQualifiedName("Content.Client.UserInterface.Systems.Bwoink.AHelpUIController", out var ahelpType) ||
            PatchHelpers.GetMethod(ahelpType, "ReceivedBwoink") is not { } receivedBwoink)
        {
            SanabiLogger.LogWarn("BwoinkPatch: couldn't resolve AHelpUIController.ReceivedBwoink, skipping patch!");
            return;
        }

        PatchHelpers.PatchMethod(
            receivedBwoink,
            Prefix,
            HarmonyPatchType.Prefix
        );
    }

    private static bool Prefix(ref dynamic __instance, dynamic? sender, dynamic message)
    {
        //__instance.UnreadAHelpReceived();
        Console.WriteLine("Fuck off! Bwoink.");
        return false;
    }
}
