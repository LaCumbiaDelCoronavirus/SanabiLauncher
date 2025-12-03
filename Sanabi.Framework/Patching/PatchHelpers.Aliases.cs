using System.Reflection;
using HarmonyLib;
using Sanabi.Framework.Game.Managers;

namespace Sanabi.Framework.Patching;

public static partial class PatchHelpers
{
    /// <summary>
    ///     Tries to patch a method on a type given their names via
    ///         a given <see cref="HarmonyPatchType"/>.
    /// </summary>
    /// <returns>True if there was success.</returns>
    public static bool TryPatchMethod(string typeName, string methodName, MethodInfo patch, HarmonyPatchType patchType)
    {
        if (!ReflectionManager.TryGetTypeByQualifiedName(typeName, out var type) ||
            type.GetMethod(methodName) is not { } methodInfo)
            return false;

        return TryPatchMethod(methodInfo, patch, patchType);
    }

    /// <summary>
    ///     Tries to patch a method on a type given it's name via
    ///         a given <see cref="HarmonyPatchType"/>.
    /// </summary>
    /// <returns>True if there was success.</returns>
    public static bool TryPatchMethod(Type? type, string methodName, MethodInfo patch, HarmonyPatchType patchType)
    {
        if (type == null ||
            type.GetMethod(methodName) is not { } methodInfo)
            return false;

        return TryPatchMethod(methodInfo, patch, patchType);
    }

    /// <summary>
    ///     Tries to patch a method via
    ///         a given <see cref="HarmonyPatchType"/>.
    /// </summary>
    /// <returns>True if there was success.</returns>
    public static bool TryPatchMethod(MethodBase? method, MethodInfo? patch, HarmonyPatchType patchType)
    {
        if (method == null ||
            patch == null)
        {
            Console.WriteLine($"TRYPATCH FAIL: METHOD {(method == null ? "NULL" : "NOTNULL")} or PATCH {(patch == null ? "NULL" : "NOTNULL")}");
            return false;
        }

        switch (patchType)
        {
            case HarmonyPatchType.Prefix:
                Prefix(method, patch);
                break;
            case HarmonyPatchType.Postfix:
                Postfix(method, patch);
                break;
            case HarmonyPatchType.Transpiler:
                Transpiler(method, patch);
                break;
            case HarmonyPatchType.Finalizer:
                Finalizer(method, patch);
                break;
            case HarmonyPatchType.ReversePatch:
                ReversePatch(method, patch);
                break;
            default:
                Console.WriteLine($"TRYPATCH FAIL: Bad patch-type: {patchType}");
                return false;
        }

        return true;
    }

    private static void Prefix(MethodBase method, MethodInfo prefix)
        => HarmonyManager.Harmony.Patch(method, prefix: prefix);

    private static void Postfix(MethodBase method, MethodInfo postfix)
        => HarmonyManager.Harmony.Patch(method, postfix: postfix);

    private static void Transpiler(MethodBase method, MethodInfo transpiler)
        => HarmonyManager.Harmony.Patch(method, transpiler: transpiler);

    private static void Finalizer(MethodBase method, MethodInfo finalizer)
        => HarmonyManager.Harmony.Patch(method, finalizer: finalizer);

    private static void ReversePatch(MethodBase method, MethodInfo reversePatch)
    {
        var reversePatcher = HarmonyManager.Harmony.CreateReversePatcher(method, reversePatch);
        reversePatcher.Patch();
    }
}
