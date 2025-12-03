using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Manages hiding assemblies from the 999999 different
///         places that list every assembly.
/// </summary>
public static class AssemblyHidingManager
{
    /// <summary>
    ///     Assemblies hidden from view.
    /// </summary>
    private static readonly List<Assembly> _hiddenAssemblies = new();

    /*
    See: https://github.com/space-wizards/RobustToolbox/blob/9e8f7092ea32a2653776292703d20320f3f34cf5/Robust.Shared/ContentPack/Sandbox.yml#L15

    ```
    # EVERYTHING in these namespaces is allowed.
    # Note that, due to a historical bug in the sandbox, any namespace _prefixed_ with one of these
    # is also allowed. (For instance, RobustBats.X, or ContentFarm.Y)
    WhitelistedNamespaces:
    - Robust
    - Content
    - OpenDreamShared
    ```
    */
    private static readonly string[] _contentNamespaces = ["Robust", "Content", "OpenDreamShared"];

    public static void Initialise()
    {
        HarmonyManager.Initialise();
    }

    public static void HideBasicAssemblies()
    {
        HideAssembly("Harmony", once: false);
        HideAssembly("Sanabi", once: false);

        HideAssembly("MonoMod", once: false);

        HideAssembly("System.Reflection", once: true);
        HideAssembly("System.Reflection.Emit", once: true);
    }

    /// <summary>
    ///     Hides the assemblies whose <see cref="Assembly.FullName"/>
    ///         matches the given string.
    /// </summary>
    /// <param name="once">Only hide the first matching one?</param>
    public static void HideAssembly(string identifier, bool once = false)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            if (assembly.FullName is not { } fullName ||
                !fullName.Contains(identifier))
                continue;

            HideAssembly(assembly);
            if (once)
                break;
        }
    }

    /// <summary>
    ///     Hides an assembly.
    /// </summary>
    public static void HideAssembly(Assembly assembly)
    {
        _hiddenAssemblies.Add(assembly);
    }

    public static void PatchDetectionVectors()
    {
        MethodInfo?[] methods = [
            typeof(AppDomain).GetMethod(nameof(AppDomain.GetAssemblies)),
            typeof(AssemblyLoadContext).GetProperty("Assemblies")?.GetGetMethod(),
            typeof(AssemblyLoadContext).GetProperty("All")?.GetGetMethod(),
            typeof(Assembly).GetMethod(nameof(Assembly.GetTypes)),
            Assembly.GetExecutingAssembly().GetType().GetMethod(nameof(Assembly.GetReferencedAssemblies))
        ];

        var patchMethod = PatchHelpers.GetMethod(typeof(AssemblyHidingManager), "DetectionVectorPatcher");
        foreach (var method in methods)
            PatchHelpers.PatchMethod(
                targetMethod: method,
                patchMethod: patchMethod,
                HarmonyPatchType.Postfix
            );
    }

    private static AssemblyName[] HiddenAssemblyNames()
    {
        var list = new List<AssemblyName>();
        foreach (var hiddenAssembly in _hiddenAssemblies)
            list.Add(hiddenAssembly.GetName());

        return [.. list];
    }

    private static AssemblyName[] HideHiddenAssemblyNames(AssemblyName[] names)
    {
        var hiddenNames = HiddenAssemblyNames();
        return [.. names.Where(assemblyName => !hiddenNames.Contains(assemblyName))];
    }

    private static IEnumerable<Type> HideHiddenTypes(Type[] unhiddenTypes)
    {
        foreach (var type in unhiddenTypes)
        {
            if (_hiddenAssemblies.Contains(type.Assembly))
                continue;

            yield return type;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DetectionVectorPatcher(ref object __result)
    {
        // If called from framework or whatever then let it actually use the function
        // I think this is actually a bit overkill so it's not used.
        //if (IsCallsiteInHiddenAssembly())
        //    return;

        // Don't let content see
        if (!IsCallsiteFromGame())
            return;

        // i hate ts
        switch (__result)
        {
            case AssemblyName[] assemblyNames:
                __result = HideHiddenAssemblyNames(assemblyNames);
                break;
            case Assembly[] originalAssemblies:
                __result = originalAssemblies.Where(assembly => !_hiddenAssemblies.Contains(assembly)).ToArray();
                break;
            case Type[] types:
                __result = HideHiddenTypes(types).ToArray();
                break;
            case IEnumerable<Assembly> assemblyEnumerable:
                __result = assemblyEnumerable.Where(assembly => !_hiddenAssemblies.Contains(assembly));
                break;
            case IEnumerable<AssemblyLoadContext> assemblyLoadContextEnumerable:
                __result = assemblyLoadContextEnumerable.Where(context => context.Name != "Assembly.Load(byte[], ...)");
                break;
            default:
                throw new InvalidOperationException($"Bad type: {__result.GetType()}");
        }
    }

    /// <returns>Whether the call-site of this method is in a currently hidden assembly.</returns>
    public static bool IsCallsiteInHiddenAssembly()
    {
        var stackTrace = new StackTrace();
        var frames = stackTrace.GetFrames();

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method == null ||
                method.DeclaringType == null)
                continue;

            if (_hiddenAssemblies.Contains(method.DeclaringType.Assembly))
                return true;
        }

        return false;
    }

    /// <returns>Whether the stack-trace of this method's call-site was ever in any Robust/Content/OpenDreamShared namespace.</returns>
    public static bool IsCallsiteFromGame()
    {
        var stackTrace = new StackTrace();
        var frames = stackTrace.GetFrames();

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method == null ||
                method.DeclaringType?.Namespace is not { } methodNamespace ||
                methodNamespace.Length == 0)
                continue;

            foreach (var badNamespace in _contentNamespaces)
            {
                if (methodNamespace.StartsWith(badNamespace))
                    return true;
            }
        }

        return false;
    }
}
