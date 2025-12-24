using HarmonyLib;
using Sanabi.Framework.Data;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     TOC to entry team, be advised, suspects have dug in and may have planted traps on the premises.
///
///     This patch disables Remote Command Execution.
/// </summary>
public static class RceTripwirePatch
{
    /// <summary>
    ///     Commands still allowed for RCE.
    /// </summary>
    public static readonly List<string> IgnoredCommands = ["observe", "joingame", "mapping", "toggleready", "suicide", "ghostroles", "deadmin", "readmin", "say", "whisper", "me", "ghost", "ooc", "looc", "adminremarks", "forcemap"];

    [PatchEntry(PatchRunLevel.Engine)]
    public static void Patch()
    {
        if (!SanabiConfig.ProcessConfig.LoadInternalMods)
            return;

        PatchHelpers.PatchMethod(
            "Robust.Client.Console.ClientConsoleHost",
            "RemoteExecuteCommand",
            Prefix,
            HarmonyPatchType.Prefix
        );
    }

    private static bool Prefix(dynamic? session, string command)
    {
        if (session == null)
            return true;

        foreach (var ignoredCommand in IgnoredCommands)
        {
            if (command.StartsWith(ignoredCommand))
            {
                Console.WriteLine($"Allowed command \"{command}\" to execute.");
                return true;
            }
        }

        Console.WriteLine($"Blocked command \"{command}\" from executing.");
        return false;
    }
}
