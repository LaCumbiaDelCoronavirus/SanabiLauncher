using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Sanabi.Framework.Misc;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Manages hiding assemblies from the 999999 different
///         places that list every assembly.
/// </summary>
public static class AssemblyHidingManager
{
    /// <summary>
    ///     Assembly names hidden from view.
    ///         Bool is whether to match exact name (true) or any string that contains the name (false).
    ///         Concurrent because hooks may read this from any thread while the game hides/unhides assemblies.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _hiddenAssemblies = new();

    /// <summary>
    ///     Callsites that will be treated as if theyre in a hidden assembly.
    /// </summary>
    public static readonly HashSet<MethodInfo> OverridenCallsites = [];

    /// <summary>
    ///     Callsites that will be treated as if theyre in a hidden assembly,
    ///         ONLY if they are never called outside of hidden assemblies/engine.
    /// </summary>
    public static readonly HashSet<MethodInfo> EngineOverridenCallsites = [];

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
    private static readonly string[] _engineNamespaces = ["Robust.Client", "Robust.Shared", "System.Private.CoreLib"];

    /// <summary>
    ///     Type name the sandbox uses for dynamically loaded assemblies; these are never reported to callers.
    /// </summary>
    private const string HiddenLoadContextName = "Assembly.Load(byte[], ...)";

    private static readonly Lock _patchLock = new();
    private static bool _detectionVectorsPatched;

    public static void HideBasicAssemblies()
    {
        HideAssembly("Harmony", exact: false);
        HideAssembly("Sanabi", exact: false);

        HideAssembly("MonoMod", exact: false);

        HideAssembly("SS14.Common", exact: true);
        HideAssembly("System.Reflection.Emit", exact: true);
    }

    /// <summary>
    ///     Hides the assemblies whose <see cref="Assembly.Name"/>
    ///         matches the given string. Calling again with the same identifier updates its match mode.
    /// </summary>
    /// <param name="exact">If true, then only hides assemblies whose name is exactly this id. Otherwise, hides those whose name contains this id.</param>
    /// <exception cref="ArgumentException">The identifier is empty. An empty non-exact identifier would match, and hide, every assembly.</exception>
    public static void HideAssembly(string identifier, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Assembly identifier must not be empty.", nameof(identifier));

        _hiddenAssemblies[identifier] = exact;
    }

    /// <summary>
    ///     Unhides the given assembly by ID.
    /// </summary>
    public static void UnhideAssembly(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return;

        _hiddenAssemblies.TryRemove(identifier, out _);
    }

    /// <summary>
    ///     Hides an assembly.
    /// </summary>
    public static void HideAssembly(Assembly assembly, bool exact = false)
    {
        HideAssembly(GetAssemblyName(assembly), exact: exact);
    }

    public static void PatchDetectionVectors()
    {
        lock (_patchLock)
        {
            // Harmony would apply each postfix twice if this were called again, so only patch once.
            if (_detectionVectorsPatched)
                return;

            MethodInfo?[] methods = [
                typeof(AppDomain).GetMethod(nameof(AppDomain.GetAssemblies)), // Assembly[]
                typeof(AssemblyLoadContext).GetProperty("Assemblies")?.GetGetMethod(), // IEnumerable<Assembly>
                typeof(AssemblyLoadContext).GetProperty("All")?.GetGetMethod(), // IEnumerable<AssemblyLoadContext>
                typeof(Assembly).GetMethod(nameof(Assembly.GetTypes)), // Type[]
                typeof(Assembly).GetMethod(nameof(Assembly.GetType), [typeof(string)]), //Type
                typeof(Assembly).GetProperty("DefinedTypes")?.GetGetMethod(), // IEnumerable<TypeInfo>
                Assembly.GetExecutingAssembly().GetType().GetMethod(nameof(Assembly.GetReferencedAssemblies)) // AssemblyName[]
            ];

            var patchMethod = PatchHelpers.GetMethod(DetectionVectorPatcher);
            foreach (var method in methods)
            {
                // A missing target means the runtime differs from what we expect. Skip it rather than failing startup.
                if (method is null)
                {
                    SanabiLogger.LogWarn("Detection vector method not found on this runtime; skipping.");
                    continue;
                }

                PatchHelpers.PatchMethod(
                    targetMethod: method,
                    patchMethod: patchMethod,
                    HarmonyPatchType.Postfix
                );
            }

            //Type (case-sens options, throwonerror options, the other other GetType uses this)
            var getTypeFull = typeof(Assembly).GetMethod(nameof(Assembly.GetType), [typeof(string), typeof(bool), typeof(bool)]);
            if (getTypeFull is null)
                SanabiLogger.LogWarn("Assembly.GetType(string, bool, bool) not found on this runtime; skipping.");
            else
                PatchHelpers.PatchMethod(
                    targetMethod: getTypeFull,
                    patchMethod: PatchHelpers.GetMethod(GetTypeCaseSensitiveThrowOnErrorPatch),
                    HarmonyPatchType.Postfix
                );

            _detectionVectorsPatched = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GetTypeCaseSensitiveThrowOnErrorPatch(ref Type? __result, ref string name, ref bool throwOnError, ref bool ignoreCase)
    {
        if (__result is null ||
            !ShouldHideType(__result) ||
            IsCallsiteThroughHiddenAssembly(2)) // ignore callsites of IsCallsiteThroughHiddenAssembly and this
            return;

        // Mimic the real failure for a type that does not exist.
        if (throwOnError)
            throw new TypeLoadException($"Type {name} could not be found");

        __result = null;
    }

    /// <returns>Whether the given assembly name matches any currently hidden entry.</returns>
    public static bool ShouldHideAssembly(string ourAssemblyName)
    {
        if (string.IsNullOrEmpty(ourAssemblyName))
            return false;

        foreach (var (hiddenAssemblyName, exact) in _hiddenAssemblies)
        {
            if (exact)
            {
                if (ourAssemblyName == hiddenAssemblyName)
                    return true;
            }
            else
            {
                if (ourAssemblyName.Contains(hiddenAssemblyName, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static string GetAssemblyName(Assembly assembly) => assembly.GetName().Name ?? "";

    private static AssemblyName[] HideHiddenAssemblyNames(AssemblyName[] names)
        => [.. names.Where(assemblyName => !ShouldHideAssembly(assemblyName.Name ?? ""))];

    private static Type[] HideHiddenTypes(Type[] unhiddenTypes)
        => [.. unhiddenTypes.Where(type => !ShouldHideType(type))];

    private static bool ShouldHideType(Type type) => ShouldHideAssembly(GetAssemblyName(type.Assembly));

    /// <summary>
    ///     Whether an assembly belongs to the engine (or is part of the runtime itself).
    /// </summary>
    private static bool IsEngineAssembly(string assemblyName)
    {
        foreach (var engineNamespace in _engineNamespaces)
        {
            if (assemblyName == engineNamespace ||
                assemblyName.StartsWith(engineNamespace + ".", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DetectionVectorPatcher(ref object __result)
    {
        // If called from framework or whatever then let it actually use the function
        if (IsCallsiteThroughHiddenAssembly(2)) // ignore callsites of IsCallsiteThroughHiddenAssembly and this
            return;

        if (__result is null)
            return;

        // Results are materialized with ToArray so that filtering runs once, now, instead of lazily
        // against whatever the hidden list looks like when the caller finally enumerates.
        switch (__result)
        {
            case Assembly[] originalAssemblies:
                __result = originalAssemblies.Where(assembly => !ShouldHideAssembly(GetAssemblyName(assembly))).ToArray();
                break;
            case IEnumerable<Assembly> assemblyEnumerable:
                __result = assemblyEnumerable.Where(assembly => !ShouldHideAssembly(GetAssemblyName(assembly))).ToArray();
                break;
            case IEnumerable<AssemblyLoadContext> assemblyLoadContextEnumerable:
                __result = assemblyLoadContextEnumerable.Where(context => context.Name != HiddenLoadContextName).ToArray();
                break;
            case Type[] types:
                __result = HideHiddenTypes(types);
                break;
            case Type type:
                if (ShouldHideType(type))
                    __result = null!;

                break;
            case IEnumerable<TypeInfo> assemblyTypeInfos:
                __result = assemblyTypeInfos.Where(typeInfo => !ShouldHideType(typeInfo)).ToArray();
                break;
            case AssemblyName[] assemblyNames:
                __result = HideHiddenAssemblyNames(assemblyNames);
                break;
            default:
                // Throwing here would surface inside arbitrary game code. Leave the result alone instead.
                SanabiLogger.LogWarn($"Unexpected detection vector result type {__result.GetType()}; returning it unfiltered.");
                break;
        }
    }

    /// <returns>Whether any call-site of this stack is in a currently hidden assembly, except for the first <paramref name="ignoredFirst"/> frames.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)] // Keeps the frame count that ignoredFirst relies on stable.
    public static bool IsCallsiteThroughHiddenAssembly(int ignoredFirst = 2)
    {
        var stackTrace = new StackTrace();
        var frames = stackTrace.GetFrames();
        var index = 0;

        var onlyInEngine = false;
        var wasOutsideEngine = false;

        foreach (var frame in frames)
        {
            if (++index <= ignoredFirst)
                continue;

            if (frame.GetMethod() is not { } methodInfo ||
                methodInfo.DeclaringType is not { } declaringType)
                continue;

            if (OverridenCallsites.Contains(methodInfo))
                return true;

            if (EngineOverridenCallsites.Contains(methodInfo))
                onlyInEngine = true;

            var assemblyName = GetAssemblyName(declaringType.Assembly);
            if (onlyInEngine && !IsEngineAssembly(assemblyName))
            {
                SanabiLogger.LogWarn($"CUR-LEFTUS: {assemblyName}");
                wasOutsideEngine = true;
            }

            if (ShouldHideAssembly(assemblyName))
                return true;
        }

        if (onlyInEngine)
            return !wasOutsideEngine;

        return false;
    }

    /// <returns>Whether any call-site of this stack is in the given assembly.</returns>
    public static bool IsCallsiteThroughAssembly(string assemblyName, bool exact = false)
    {
        if (string.IsNullOrEmpty(assemblyName))
            return false;

        var stackTrace = new StackTrace();
        var frames = stackTrace.GetFrames();

        foreach (var frame in frames)
        {
            if (frame.GetMethod() is not { } methodInfo ||
                methodInfo.DeclaringType is not { } declaringType)
                continue;

            var otherAssemblyName = GetAssemblyName(declaringType.Assembly);
            if (exact && otherAssemblyName == assemblyName)
                return true;
            else if (!exact && otherAssemblyName.Contains(assemblyName, StringComparison.Ordinal))
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
            if (method?.DeclaringType?.Namespace is not { Length: > 0 } methodNamespace)
                continue;

            foreach (var badNamespace in _contentNamespaces)
            {
                if (methodNamespace.StartsWith(badNamespace, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
