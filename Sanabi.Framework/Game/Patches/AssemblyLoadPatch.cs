using System.Diagnostics;
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
    [StackTraceHidden]
    public static void Patch()
    {
        PatchHelpers.PatchMethod(
            "Robust.Shared.ContentPack.BaseModLoader",
            "InitMod",
            Prefix,
            HarmonyPatchType.Prefix
        );
    }

    [StackTraceHidden]
    private static void Prefix(Assembly assembly)
    {
        // If it doesn't have a fullname then we probably don't care whatever.
        if (assembly.GetName().Name is not { } fullName)
            return;

        Console.WriteLine($"Intercepted assembly: {fullName}");
        AssemblyManager.Assemblies[assembly.GetName().Name ?? fullName] = assembly;

        return;
    }
}
