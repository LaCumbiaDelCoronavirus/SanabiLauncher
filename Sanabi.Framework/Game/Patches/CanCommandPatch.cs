using HarmonyLib;
using Sanabi.Framework.Data;
using Sanabi.Framework.Game.Managers;
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
        if (!SanabiConfig.ProcessConfig.LoadInternalMods)
            return;

        if (!ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.Console.ClientConsoleHost", out var clientConHostType))
            throw new InvalidOperationException("Couldn't resolve ClientConsoleHost!");

        PatchHelpers.PatchMethod(clientConHostType, "CanExecute", Prefix, HarmonyPatchType.Prefix);

        if (!ReflectionManager.TryGetTypeByQualifiedName("Content.Shared.Administration.AdminData", out var adminDataType))
            throw new InvalidOperationException("Couldn't resolve AdminData!");

        PatchHelpers.PatchMethod(adminDataType, "HasFlag", Prefix, HarmonyPatchType.Prefix);

        if (!ReflectionManager.TryGetTypeByQualifiedName("Content.Client.Administration.Managers.ClientAdminManager", out var clientAdminManType))
            throw new InvalidOperationException("Couldn't resolve ClientAdminManager!");

        PatchHelpers.PatchMethod(clientAdminManType, "IsActive", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientAdminManType, "CanCommand", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientAdminManType, "CanViewVar", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientAdminManType, "CanAdminPlace", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientAdminManType, "CanScript", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientAdminManType, "CanAdminMenu", Prefix, HarmonyPatchType.Prefix);

        if (!ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.Console.ClientConGroupController", out var clientConGroupControllerType))
            throw new InvalidOperationException("Couldn't resolve ClientConGroupController!");

        PatchHelpers.PatchMethod(clientConGroupControllerType, "CanCommand", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientConGroupControllerType, "CanViewVar", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientConGroupControllerType, "CanAdminPlace", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientConGroupControllerType, "CanScript", Prefix, HarmonyPatchType.Prefix);
        PatchHelpers.PatchMethod(clientConGroupControllerType, "CanAdminMenu", Prefix, HarmonyPatchType.Prefix);
    }

    private static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
