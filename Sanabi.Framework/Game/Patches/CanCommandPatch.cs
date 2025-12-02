using HarmonyLib;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Lets you use absolutely any command.
/// </summary>
public static class CanCommandPatch
{
    [PatchEntry(PatchRunLevel.Content)]
    public static void Patch()
    {
        PatchHelpers.PatchMethod(
            "Content.Client.Administration.Managers.ClientAdminManager",
            "CanCommand",
            Prefix,
            HarmonyPatchType.Prefix
        );
    }

    private static bool Prefix(ref bool __result, string cmdName)
    {
        Console.WriteLine($"Patching CanCommand value for command \"{cmdName}\".");
        __result = true;

        return false;
    }
}
