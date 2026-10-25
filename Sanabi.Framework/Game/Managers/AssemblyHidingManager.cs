using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
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
    ///     Dynamic assembly names (Harmony's emitted assemblies) that are always hidden by exact match,
    ///         regardless of what is registered in <see cref="_hiddenAssemblies"/>.
    ///         Matched by name rather than by probing for dynamic assemblies, since the sandbox's probe changes between builds.
    /// </summary>
    private static readonly string[] _dynamicHiddenNames =
    [
        "Anonymously Hosted DynamicMethods Assembly",
        "HarmonySharedState",
    ];

    /// <summary>
    ///     Type name the sandbox uses for dynamically loaded assemblies; these are never reported to callers.
    /// </summary>
    private const string HiddenLoadContextName = "Assembly.Load(byte[], ...)";

    /// <summary>
    ///     Cache of sanitized stack trace strings, so repeated reads don't re-split and re-allocate.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> _stackTraceCache = new();
    private const int MaxStackTraceCacheSize = 512;

    /// <summary>
    ///     Fully qualified names of the Robust engine members that also enumerate assemblies/types,
    ///         patched the same way as the runtime detection vectors.
    /// </summary>
    private const string ReflectionManagerTypeName = "Robust.Shared.Reflection.ReflectionManager";
    private const string BaseModLoaderTypeName = "Robust.Shared.ContentPack.BaseModLoader";

    /// <summary>
    ///     Reentrancy guard for <see cref="LieTypeAssembly"/>. Reading Type.Assembly inside the hook
    ///         would otherwise recurse through the same patched getter.
    /// </summary>
    [ThreadStatic] private static bool _inLyingTypeAssembly;

    /// <summary>
    ///     Surrogate assembly returned in place of a hidden type's real assembly. Cached because
    ///         resolving it walks every loaded assembly.
    /// </summary>
    private static Assembly? _cachedSurrogateAssembly;

    /// <summary>
    ///     When set on the current thread, call-site checks pretend the caller is external, so that
    ///         even hidden code sees the filtered (engine) view. Used via <see cref="ForceEngineView"/>.
    /// </summary>
    [ThreadStatic] private static bool _forceEngineView;

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
        ClearStackTraceCache();
    }

    /// <summary>
    ///     Unhides the given assembly by ID.
    /// </summary>
    public static void UnhideAssembly(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return;

        _hiddenAssemblies.TryRemove(identifier, out _);
        ClearStackTraceCache();
    }

    private static void ClearStackTraceCache() => _stackTraceCache.Clear();

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

            var patchMethod = PatchHelpers.GetMethod(DetectionVectorPatcher);

            MethodInfo?[] methods = [
                typeof(AppDomain).GetMethod(nameof(AppDomain.GetAssemblies)), // Assembly[]
                typeof(AssemblyLoadContext).GetProperty("Assemblies")?.GetGetMethod(), // IEnumerable<Assembly>
                typeof(AssemblyLoadContext).GetProperty("All")?.GetGetMethod(), // IEnumerable<AssemblyLoadContext>
                typeof(Assembly).GetMethod(nameof(Assembly.GetTypes)), // Type[]
                typeof(Assembly).GetMethod(nameof(Assembly.GetType), [typeof(string)]), //Type
                Assembly.GetExecutingAssembly().GetType().GetMethod(nameof(Assembly.GetReferencedAssemblies)) // AssemblyName[]
            ];

            foreach (var method in methods)
            {
                // A missing target means the runtime differs from what we expect. Skip it rather than failing startup.
                if (method is null)
                {
                    SanabiLogger.LogWarn("Detection vector method not found on this runtime; skipping.");
                    continue;
                }

                PatchHelpers.PatchMethod(method, patchMethod, HarmonyPatchType.Postfix);
            }

            // These Assembly members are virtual, and the implementation that actually runs is the override on
            // the concrete RuntimeAssembly, not the base declaration. Patching typeof(Assembly) leaves the real
            // getter unhooked and leaking (the stealth harness proved this for DefinedTypes/Location/GetType).
            // So patch the concrete override directly.
            PatchRuntimeAssemblyMember(runtime => runtime.GetProperty("DefinedTypes")?.GetGetMethod(), patchMethod, "RuntimeAssembly.DefinedTypes"); // IEnumerable<TypeInfo>
            PatchRuntimeAssemblyMember(runtime => runtime.GetProperty(nameof(Assembly.Location))?.GetGetMethod(), PatchHelpers.GetMethod(LieLocation), "RuntimeAssembly.Location");

            // Type (case-sens options, throwonerror options; the other GetType overloads route through this).
            PatchRuntimeAssemblyMember(
                runtime => runtime.GetMethod(nameof(Assembly.GetType), [typeof(string), typeof(bool), typeof(bool)]),
                PatchHelpers.GetMethod(GetTypeCaseSensitiveThrowOnErrorPatch),
                "RuntimeAssembly.GetType(string, bool, bool)");

            // Closes Environment.StackTrace and Exception.StackTrace leaks.
            PatchStackTraceGetter(typeof(Environment), nameof(LieStackTrace));
            PatchStackTraceGetter(typeof(Exception), nameof(LieExceptionStackTrace));

            // Closes the Type.Assembly (GetType().Assembly) leak.
            PatchTypeAssemblyGetter();

            // Closes the engine's own enumerators: ReflectionManager and the mod loader each list
            // every assembly/type independently of the runtime vectors above.
            PatchEngineEnumerators();

            _detectionVectorsPatched = true;
        }
    }

    /// <summary>The concrete assembly type real instances actually are (System.Reflection.RuntimeAssembly).</summary>
    private static readonly Type _runtimeAssemblyType = typeof(object).Assembly.GetType();

    /// <summary>
    ///     Patches the concrete override of a virtual <see cref="Assembly"/> member on
    ///         <see cref="_runtimeAssemblyType"/> — the implementation that actually runs for real instances.
    /// </summary>
    private static void PatchRuntimeAssemblyMember(Func<Type, MethodInfo?> resolve, MethodInfo patchMethod, string description)
    {
        if (resolve(_runtimeAssemblyType) is not { IsAbstract: false } target)
        {
            SanabiLogger.LogWarn($"{description} not found on this runtime; skipping.");
            return;
        }

        PatchHelpers.TryPatchMethod(target, patchMethod, HarmonyPatchType.Postfix);
    }

    private static void PatchStackTraceGetter(Type owner, string postfixName)
    {
        var target = owner.GetProperty("StackTrace")?.GetGetMethod();
        if (target is null)
        {
            SanabiLogger.LogWarn($"{owner.Name}.StackTrace getter not found on this runtime; skipping.");
            return;
        }

        var postfix = typeof(AssemblyHidingManager).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
        PatchHelpers.TryPatchMethod(target, postfix, HarmonyPatchType.Postfix);
    }

    private static void PatchTypeAssemblyGetter()
    {
        // The getter on Type is abstract; the concrete one lives on the runtime type (RuntimeType),
        // which is what every real Type instance actually is. Patch whichever concrete getters we find.
        var targets = new List<MethodInfo>();

        var baseGetter = typeof(Type).GetProperty(nameof(Type.Assembly))?.GetGetMethod();
        if (baseGetter is { IsAbstract: false })
            targets.Add(baseGetter);

        var runtimeType = typeof(object).GetType();
        var runtimeGetter = runtimeType.GetProperty(nameof(Type.Assembly), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetGetMethod()
            ?? runtimeType.GetProperty(nameof(Type.Assembly))?.GetGetMethod();
        if (runtimeGetter is { IsAbstract: false } && !targets.Contains(runtimeGetter))
            targets.Add(runtimeGetter);

        if (targets.Count == 0)
        {
            SanabiLogger.LogWarn("No concrete Type.Assembly getter found on this runtime; skipping.");
            return;
        }

        var postfix = PatchHelpers.GetMethod(LieTypeAssembly);
        foreach (var target in targets)
            PatchHelpers.TryPatchMethod(target, postfix, HarmonyPatchType.Postfix);
    }

    /// <summary>
    ///     Postfix on Type.Assembly. Returns a plausible surrogate assembly for hidden types instead of their real one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [StackTraceHidden]
    private static void LieTypeAssembly(ref Assembly __result, Type __instance)
    {
        // Every path below walks the stack (IsCallsiteThroughHiddenAssembly / IsInternalReflectionCaller),
        // and each frame read does declaringType.Assembly, which re-enters this very getter. Set the
        // reentrancy guard FIRST and keep it set across those walks, so the nested reads return the real
        // assembly and terminate instead of recursing forever. Those nested walks still detect hidden callers
        // via the Harmony trampoline's dynamic-method assembly (hidden by name), so detection stays correct.
        if (_inLyingTypeAssembly)
            return;

        _inLyingTypeAssembly = true;
        try
        {
            if (IsCallsiteThroughHiddenAssembly(2)) // ignore callsites of IsCallsiteThroughHiddenAssembly and this
                return;

            // The runtime and reflection internals legitimately read this; don't lie to them or we break loading.
            if (IsInternalReflectionCaller())
                return;

            if (!ShouldHideAssembly(GetAssemblyName(__result)) &&
                !ShouldHideType(__instance))
                return;

            __result = GetSurrogateAssembly();
        }
        finally
        {
            _inLyingTypeAssembly = false;
        }
    }

    /// <returns>A non-dynamic engine assembly that looks unremarkable, falling back to CoreLib.</returns>
    private static Assembly GetSurrogateAssembly()
    {
        if (_cachedSurrogateAssembly is { IsDynamic: false })
            return _cachedSurrogateAssembly;

        _cachedSurrogateAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => GetAssemblyName(assembly) == "Robust.Client" && !assembly.IsDynamic);

        return _cachedSurrogateAssembly ?? typeof(object).Assembly;
    }

    /// <returns>Whether the stack above this hook is the runtime's own reflection/patching machinery.</returns>
    [StackTraceHidden]
    private static bool IsInternalReflectionCaller()
    {
        // Engine-view scope wants the filtered result regardless of who is asking.
        if (_forceEngineView)
            return false;

        var frames = new StackTrace(2, false).GetFrames();

        foreach (var frame in frames.Take(10))
        {
            if (frame.GetMethod() is not { } method ||
                method.DeclaringType is not { } declaringType)
                continue;

            var assemblyName = GetAssemblyName(declaringType.Assembly);
            if (assemblyName.Contains("Harmony", StringComparison.Ordinal) ||
                assemblyName.Contains("MonoMod", StringComparison.Ordinal) ||
                assemblyName.Contains("Mono.Cecil", StringComparison.Ordinal))
                return true;

            if (declaringType.FullName is { } fullName &&
                (fullName.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                 fullName.StartsWith("System.RuntimeMethodHandle", StringComparison.Ordinal) ||
                 fullName.StartsWith("System.RuntimeType", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Patches the engine members that enumerate assemblies/types on their own:
    ///         ReflectionManager.Assemblies, ReflectionManager.FindAllTypes and BaseModLoader.LoadedModules.
    ///         All three return assembly/type sequences handled by <see cref="DetectionVectorPatcher"/>.
    /// </summary>
    private static void PatchEngineEnumerators()
    {
        var patchMethod = PatchHelpers.GetMethod(DetectionVectorPatcher);

        if (ReflectionManager.TryGetTypeByQualifiedName(ReflectionManagerTypeName, out var reflectionManager))
        {
            PatchEngineMember(reflectionManager.GetProperty("Assemblies")?.GetGetMethod(), "ReflectionManager.Assemblies", patchMethod);
            PatchEngineMember(
                reflectionManager.GetMethod("FindAllTypes", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null),
                "ReflectionManager.FindAllTypes",
                patchMethod);
        }
        else
        {
            SanabiLogger.LogWarn($"{ReflectionManagerTypeName} not found; skipping its detection vectors.");
        }

        // NOTE: BaseModLoader.LoadedModules is intentionally NOT hooked. AssemblyLoadingManager loads mods
        // detached from the engine ModLoader (it never calls InitMod), so our mods are never added to the
        // engine's Mods list and so never appear in LoadedModules — it is inherently clean, with nothing to scrub.
    }

    private static void PatchEngineMember(MethodInfo? target, string description, MethodInfo patchMethod)
    {
        if (target is null)
        {
            SanabiLogger.LogWarn($"{description} not found on this engine build; skipping.");
            return;
        }

        PatchHelpers.TryPatchMethod(target, patchMethod, HarmonyPatchType.Postfix);
    }

    /// <summary>
    ///     Postfix on Assembly.Location. Returns a plausible path for hidden assemblies instead of their real one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [StackTraceHidden]
    private static void LieLocation(ref string __result, Assembly __instance)
    {
        if (IsCallsiteThroughHiddenAssembly(2)) // ignore callsites of IsCallsiteThroughHiddenAssembly and this
            return;

        if (!ShouldHideAssembly(GetAssemblyName(__instance)))
            return;

        // CoreLib's location is guaranteed to exist and looks like any other runtime assembly.
        __result = typeof(object).Assembly.Location;
    }

    /// <summary>
    ///     Postfix on Environment.StackTrace. Drops lines that mention hidden assemblies.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [StackTraceHidden]
    private static void LieStackTrace(ref string __result)
    {
        if (IsCallsiteThroughHiddenAssembly(2)) // ignore callsites of IsCallsiteThroughHiddenAssembly and this
            return;

        __result = FilterStackTrace(__result);
    }

    /// <summary>
    ///     Postfix on Exception.StackTrace. Drops lines that mention hidden assemblies.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [StackTraceHidden]
    private static void LieExceptionStackTrace(ref string __result)
    {
        if (IsCallsiteThroughHiddenAssembly(2)) // ignore callsites of IsCallsiteThroughHiddenAssembly and this
            return;

        __result = FilterStackTrace(__result);
    }

    /// <summary>
    ///     Removes every line of the stack trace that mentions a hidden assembly.
    ///         Results are cached so that repeated reads of the same trace stay cheap.
    /// </summary>
    [StackTraceHidden]
    private static string FilterStackTrace(string original)
    {
        if (string.IsNullOrEmpty(original))
            return original;

        if (_stackTraceCache.TryGetValue(original, out var cached))
            return cached;

        var sb = new StringBuilder(original.Length);
        var hadAny = false;
        var removedAny = false;
        foreach (var line in original.Split('\n'))
        {
            if (StackLineMentionsHidden(line))
            {
                removedAny = true;
                continue;
            }

            if (hadAny)
                sb.Append('\n');

            sb.Append(line);
            hadAny = true;
        }

        if (!removedAny)
            return original;

        var result = sb.ToString();
        if (_stackTraceCache.Count < MaxStackTraceCacheSize)
            _stackTraceCache.TryAdd(original, result);

        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [StackTraceHidden]
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
    [StackTraceHidden]
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

        foreach (var dynamicHiddenName in _dynamicHiddenNames)
        {
            if (string.Equals(ourAssemblyName, dynamicHiddenName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <returns>Whether the given line of a stack trace mentions any currently hidden assembly.</returns>
    [StackTraceHidden]
    private static bool StackLineMentionsHidden(string line)
    {
        foreach (var hiddenAssemblyName in _hiddenAssemblies.Keys)
        {
            if (line.Contains(hiddenAssemblyName, StringComparison.Ordinal))
                return true;
        }

        foreach (var dynamicHiddenName in _dynamicHiddenNames)
        {
            if (line.Contains(dynamicHiddenName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [StackTraceHidden]
    private static string GetAssemblyName(Assembly assembly) => assembly.GetName().Name ?? "";

    [StackTraceHidden]
    private static AssemblyName[] HideHiddenAssemblyNames(AssemblyName[] names)
        => [.. names.Where(assemblyName => !ShouldHideAssembly(assemblyName.Name ?? ""))];

    [StackTraceHidden]
    private static Type[] HideHiddenTypes(Type[] unhiddenTypes)
        => [.. unhiddenTypes.Where(type => !ShouldHideType(type))];

    [StackTraceHidden]
    private static bool ShouldHideType(Type type) => ShouldHideAssembly(GetAssemblyName(type.Assembly));

    /// <summary>
    ///     Whether an assembly belongs to the engine (or is part of the runtime itself).
    /// </summary>
    [StackTraceHidden]
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
    [StackTraceHidden]
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
            // Must stay below IEnumerable<TypeInfo>: covariance makes a TypeInfo sequence match this too.
            case IEnumerable<Type> typeEnumerable:
                __result = typeEnumerable.Where(type => !ShouldHideType(type)).ToList();
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
    [StackTraceHidden]
    public static bool IsCallsiteThroughHiddenAssembly(int ignoredFirst = 2)
    {
        // Engine-view scope pretends every caller is external, so hidden code also sees the filtered result.
        if (_forceEngineView)
            return false;

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

    /// <summary>
    ///     Runs a block as if the caller were external engine code: every call-site check inside it
    ///         returns the filtered view, even from hidden assemblies. Dispose to restore the previous state.
    /// </summary>
    /// <example>
    ///     <code>
    ///     using (AssemblyHidingManager.ForceEngineView())
    ///     {
    ///         // Assembly.GetTypes() etc. here see exactly what the engine would see.
    ///     }
    ///     </code>
    /// </example>
    public static IDisposable ForceEngineView() => new EngineViewScope();

    /// <returns>Whether an engine-view scope is currently active on this thread.</returns>
    public static bool IsEngineViewForced() => _forceEngineView;

    private sealed class EngineViewScope : IDisposable
    {
        private readonly bool _previous;

        public EngineViewScope()
        {
            _previous = _forceEngineView;
            _forceEngineView = true;
        }

        public void Dispose() => _forceEngineView = _previous;
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
