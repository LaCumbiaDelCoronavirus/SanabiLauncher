using System.Reflection;
using System.Runtime.Loader;

namespace SS14.Launcher.StealthProbe;

/// <summary>
///     The "fake game" detection battery. Each probe runs one of the detection vectors the real SS14 engine
///         (RobustToolbox) uses to enumerate assemblies/types, and reports every hidden-assembly identifier it
///         managed to see. An empty list means the probe saw nothing to betray Sanabi.
///     The hidden-name predicate here is an INDEPENDENT re-statement of what a detector would look for — it does
///         not call into Sanabi, so a probe cannot "agree with itself" into a false pass.
/// </summary>
public static class Probes
{
    /// <summary>The fully-qualified type a detector would try to resolve to prove Sanabi is present.</summary>
    public const string SanabiTypeName = "Sanabi.Framework.Game.Managers.AssemblyHidingManager";

    /// <returns>Whether an assembly simple-name would betray Sanabi (mirrors HideBasicAssemblies' set).</returns>
    public static bool IsHiddenName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name.Contains("Harmony", StringComparison.Ordinal)   // 0Harmony, HarmonySharedState, ...
            || name.Contains("Sanabi", StringComparison.Ordinal)    // Sanabi.Framework
            || name.Contains("MonoMod", StringComparison.Ordinal)
            || name == "SS14.Common"
            || name == "System.Reflection.Emit"
            || name == "Anonymously Hosted DynamicMethods Assembly";
    }

    /// <returns>Whether a type's full name would betray Sanabi. Name-based, so it never trusts the (hooked)
    ///     Type.Assembly getter — a detector reading type names cannot be lied to this way.</returns>
    public static bool IsHiddenTypeName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return false;

        return fullName.Contains("Sanabi", StringComparison.Ordinal)
            || fullName.Contains("HarmonyLib", StringComparison.Ordinal)
            || fullName.Contains("MonoMod", StringComparison.Ordinal);
    }

    /// <summary>Vector #0: the engine's lethal static-ctor scan over AppDomain.GetAssemblies() for "0Harmony".</summary>
    public static List<string> AppDomainAssemblies()
    {
        // Rebuild "0Harmony" from chars exactly as GameController.Standalone does, so a crude string search of
        // this harness can't be used to argue the probe is cheating.
        var zeroHarmony = "0" + "H" + "a" + "r" + "m" + "o" + "n" + "y";

        var leaks = new List<string>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = SafeName(assembly);
            if (name == zeroHarmony || IsHiddenName(name))
                leaks.Add($"AppDomain.GetAssemblies -> {name}");
        }

        return leaks;
    }

    /// <summary>AssemblyLoadContext.All and each context's Assemblies (the engine's `lsasm` surface).</summary>
    public static List<string> LoadContexts()
    {
        var leaks = new List<string>();
        foreach (var context in AssemblyLoadContext.All)
        {
            if (context.Name == "Assembly.Load(byte[], ...)")
                leaks.Add($"AssemblyLoadContext.All -> context '{context.Name}'");

            foreach (var assembly in context.Assemblies)
            {
                var name = SafeName(assembly);
                if (IsHiddenName(name))
                    leaks.Add($"AssemblyLoadContext.Assemblies -> {name}");
            }
        }

        return leaks;
    }

    /// <summary>Assembly.GetTypes() and DefinedTypes called on the (hidden) Sanabi assembly.</summary>
    public static List<string> TypesOfHiddenAssembly(Assembly hiddenAssembly)
    {
        var leaks = new List<string>();

        foreach (var type in hiddenAssembly.GetTypes())
            if (IsHiddenTypeName(type.FullName))
                leaks.Add($"Assembly.GetTypes -> {type.FullName}");

        foreach (var typeInfo in hiddenAssembly.DefinedTypes)
            if (IsHiddenTypeName(typeInfo.FullName))
                leaks.Add($"Assembly.DefinedTypes -> {typeInfo.FullName}");

        return leaks;
    }

    /// <summary>Assembly.GetType(string) and the throw-on-error overload, resolving the Sanabi type by name.</summary>
    public static List<string> TypeLookups(Assembly hiddenAssembly)
    {
        var leaks = new List<string>();

        if (hiddenAssembly.GetType(SanabiTypeName) is { } found)
            leaks.Add($"Assembly.GetType(string) -> {found.FullName}");

        try
        {
            // Hidden: should throw TypeLoadException (mimics a genuinely missing type). Visible: returns the type.
            if (hiddenAssembly.GetType(SanabiTypeName, throwOnError: true, ignoreCase: false) is { } thrown)
                leaks.Add($"Assembly.GetType(string,bool,bool) -> {thrown.FullName}");
        }
        catch (TypeLoadException)
        {
            // Correctly hidden.
        }

        return leaks;
    }

    /// <summary>Assembly.GetReferencedAssemblies() on the harness, which genuinely references Sanabi.Framework.</summary>
    public static List<string> ReferencedAssemblies()
    {
        var leaks = new List<string>();
        var self = Assembly.GetEntryAssembly() ?? typeof(Probes).Assembly;
        foreach (var reference in self.GetReferencedAssemblies())
        {
            if (IsHiddenName(reference.Name))
                leaks.Add($"Assembly.GetReferencedAssemblies -> {reference.Name}");
        }

        return leaks;
    }

    /// <summary>Assembly.Location on the (hidden) Sanabi assembly: must not reveal a Sanabi path.</summary>
    public static List<string> LocationOfHiddenAssembly(Assembly hiddenAssembly)
    {
        var leaks = new List<string>();
        var location = hiddenAssembly.Location;
        if (location.Contains("Sanabi", StringComparison.OrdinalIgnoreCase))
            leaks.Add($"Assembly.Location -> {location}");

        return leaks;
    }

    /// <summary>Type.Assembly on a hidden type: the reported assembly name must not be Sanabi's.</summary>
    public static List<string> TypeAssemblyName(Type hiddenType)
    {
        var leaks = new List<string>();
        var name = SafeName(hiddenType.Assembly);
        if (IsHiddenName(name))
            leaks.Add($"Type.Assembly -> {name}");

        return leaks;
    }

    /// <summary>Environment.StackTrace / Exception.StackTrace: no line may mention a hidden assembly.</summary>
    public static List<string> StackTraces(string environmentTrace, string exceptionTrace)
    {
        var leaks = new List<string>();
        AddStackLeaks(leaks, "Environment.StackTrace", environmentTrace);
        AddStackLeaks(leaks, "Exception.StackTrace", exceptionTrace);
        return leaks;
    }

    /// <summary>
    ///     The engine's own enumerators, hooked via the Robust.Shared shim. (BaseModLoader.LoadedModules is
    ///         deliberately NOT probed: Sanabi loads mods without registering them in the engine's Mods list, so
    ///         they never enter LoadedModules — there is no hook to exercise and nothing to scrub.)
    /// </summary>
    public static List<string> EngineEnumerators(
        Robust.Shared.Reflection.ReflectionManager reflectionManager)
    {
        var leaks = new List<string>();

        foreach (var assembly in reflectionManager.Assemblies)
            if (IsHiddenName(SafeName(assembly)))
                leaks.Add($"ReflectionManager.Assemblies -> {SafeName(assembly)}");

        foreach (var type in reflectionManager.FindAllTypes())
            if (IsHiddenTypeName(type.FullName))
                leaks.Add($"ReflectionManager.FindAllTypes -> {type.FullName}");

        return leaks;
    }

    private static void AddStackLeaks(List<string> leaks, string source, string trace)
    {
        foreach (var line in trace.Split('\n'))
        {
            if (line.Contains("Harmony", StringComparison.Ordinal) ||
                line.Contains("Sanabi", StringComparison.Ordinal) ||
                line.Contains("MonoMod", StringComparison.Ordinal))
                leaks.Add($"{source} -> {line.Trim()}");
        }
    }

    private static string SafeName(Assembly assembly)
    {
        try { return assembly.GetName().Name ?? ""; }
        catch { return ""; }
    }
}

/// <summary>
///     A decoy whose type name contains "Harmony", used only to put a betraying frame on the stack so the
///         Environment.StackTrace / Exception.StackTrace hooks have a real line to scrub. The harness assembly
///         itself is not hidden, so these frames are filtered by the stack-trace hook, not by [StackTraceHidden].
/// </summary>
public static class HarmonyFrameDecoy
{
    // Patched with an empty postfix purely to make Harmony materialise its in-memory assemblies
    // (HarmonySharedState / Anonymously Hosted DynamicMethods Assembly) in both harness modes.
    public static void NoOp() { }
    public static void NoOpPostfix() { }

    public static void Capture(out string environmentTrace, out string exceptionTrace)
    {
        environmentTrace = Environment.StackTrace;

        try
        {
            throw new InvalidOperationException("stealth-probe decoy");
        }
        catch (InvalidOperationException ex)
        {
            exceptionTrace = ex.StackTrace ?? "";
        }
    }
}
