using System.Reflection;
using Sanabi.Framework.Game.Managers;
using HarmonyLib;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Intercepts `Robust.Shared.ContentPack.BaseModLoader.InitMod(Assembly assembly)`.
///         Assumes that the type can be found.
///
///     This adds all loaded and initialised mod assemblies to <see cref="AssemblyManager.Assemblies"/>.
/// </summary>
public static class AssemblyLoadPatch
{
    [PatchEntry(PatchRunLevel.Engine)]
    public static void Patch()
    {
        PatchHelpers.PatchMethod(
            "Robust.Shared.ContentPack.BaseModLoader",
            "InitMod",
            Prefix,
            HarmonyPatchType.Prefix
        );
    }

    private static bool Prefix(Assembly assembly)
    {
        Console.WriteLine($"Trying to intercept assembly...");
        // If it doesn't have a fullname then we probably don't care whatever.
        if (assembly.FullName is not { } fullName)
            return true;

        Console.WriteLine($"Intercepted assembly: {fullName}");
        AssemblyManager.Assemblies[fullName] = assembly;

        return true;
    }
}
