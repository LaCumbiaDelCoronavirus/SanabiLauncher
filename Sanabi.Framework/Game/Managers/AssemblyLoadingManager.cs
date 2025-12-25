using System.Reflection;
using HarmonyLib;
using Sanabi.Framework.Data;
using Sanabi.Framework.Game.Patches;
using Sanabi.Framework.Misc;
using Sanabi.Framework.Patching;
using SS14.Launcher;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Handles loading mods from the mods directory,
///         into the game.
/// </summary>
public static class AssemblyLoadingManager
{
    private static readonly Stack<Assembly> _assembliesPendingLoad = new();
    private static MethodInfo _modInitMethod = default!;

    /// <summary>
    ///     Invokes a static method and enters it. The method may
    ///         have no parameters. If the method has one parameter,
    ///         whose type is a `Dictionary<string, Assembly?>`, the
    ///         method will be invoked with <see cref="AssemblyManager.Assemblies"/>
    ///         as the only parameter.
    /// </summary>
    /// <param name="async">Whether to run the method on another task.</param>
    public static void Enter(MethodInfo entryMethod, bool async = false)
    {
        var parameters = entryMethod.GetParameters();
        object?[]? invokedParameters = null;
        if (parameters.Length == 1 &&
            parameters[0].ParameterType == AssemblyManager.Assemblies.GetType())
            invokedParameters = [AssemblyManager.Assemblies];

        if (async)
            _ = Task.Run(async () => entryMethod.Invoke(null, invokedParameters));
        else
            entryMethod.Invoke(null, invokedParameters);

        Console.WriteLine($"Entered patch at {entryMethod.DeclaringType?.FullName}");
    }

    [PatchEntry(PatchRunLevel.Engine)]
    private static void Start()
    {
        if (!SanabiConfig.ProcessConfig.LoadExternalMods)
            return;

        var internalModLoader = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.ModLoader");

        // Find the internal method that accepts Assembly[] and cache it
        if (_modInitMethod == null && internalModLoader != null)
        {
            var candidates = new List<string>();
                foreach (var m in internalModLoader.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = m.GetParameters();
                    if (ps.Length != 1)
                    {
                        candidates.Add($"{m.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        continue;
                    }

                    var pType = ps[0].ParameterType;
                    var ok =
                        pType == typeof(Assembly[]) ||
                        (pType.IsArray && pType.GetElementType() == typeof(Assembly)) ||
                        typeof(IEnumerable<Assembly>).IsAssignableFrom(pType) ||
                        pType == typeof(Assembly);

                    if (ok)
                    {
                        _modInitMethod = m;
                        SanabiLogger.LogInfo($"Found mod init method: {m.Name} ({pType.FullName})");
                        break;
                    }

                    candidates.Add($"{m.Name}({pType.FullName})");
                }

            if (_modInitMethod == null)
                SanabiLogger.LogError($"Mod init method not found on ModLoader; checked candidates: {string.Join(", ", candidates)}");
        }

        PatchHelpers.PatchMethod(
            internalModLoader,
            "TryLoadModules",
            ModLoaderPostfix,
            HarmonyPatchType.Postfix
        );

        var externalDlls = Directory.GetFiles(LauncherPaths.SanabiModsPath, "*.dll", SearchOption.TopDirectoryOnly);
        if (externalDlls.Length == 0)
            return;

        foreach (var dll in externalDlls)
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                _assembliesPendingLoad.Push(asm);
                SanabiLogger.LogInfo($"Loaded mod dll: {dll} -> {asm.FullName}");
            }
            catch (Exception ex)
            {
                SanabiLogger.LogError($"Failed to Assembly.LoadFrom('{dll}'): {ex}");
            }
        }
    }

    private static void ModLoaderPostfix(ref dynamic __instance)
    {
        if (__instance == null)
        {
            SanabiLogger.LogError("ModLoaderPostfix: __instance is null");
            return;
        }

        while (_assembliesPendingLoad.TryPop(out var assembly))
            LoadModAssembly(ref __instance, assembly);
    }

    /// <summary>
    ///     Tries to get the entry point for a mod assembly.
    ///         This is compatible with Marsey patches.
    /// </summary>
    public static MethodInfo? GetModAssemblyEntryPoint(Assembly assembly)
    {
        var entryPointType = assembly.GetType("PatchEntry") ?? assembly.GetType("EntryPoint") ?? assembly.GetType("MarseyEntry");
        return entryPointType?.GetMethod("Entry", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    }

    private static void LogDelegate(AssemblyName asm, string message)
    {
        SanabiLogger.LogInfo($"PRT-{asm.FullName}: {message}");
    }

    /// <summary>
    ///     Ports MarseyLogger to work with a mod assembly patch;
    ///         i.e. makes it print here.
    /// </summary>
    /// <param name="assembly">The mod assembly.</param>
    public static void PortModMarseyLogger(Assembly assembly)
    {
        if (assembly.GetType("MarseyLogger") is not { } loggerType ||
            assembly.GetType("MarseyLogger+Forward") is not { } delegateType)
            return;

        var marseyLogDelegate = Delegate.CreateDelegate(delegateType, PatchHelpers.GetMethod(LogDelegate));

        var loggerForwardDelegateType = loggerType.GetField("logDelegate");
        loggerForwardDelegateType?.SetValue(null, marseyLogDelegate);
    }

    private static void LoadModAssembly(ref dynamic modLoader, Assembly modAssembly)
    {
        try
        {
            if (modLoader == null)
            {
                SanabiLogger.LogError("LoadModAssembly: modLoader is null");
                return;
            }

            AssemblyHidingManager.HideAssembly(modAssembly);
            PortModMarseyLogger(modAssembly);

            if (_modInitMethod == null)
            {
                SanabiLogger.LogError("Mod init method not found on ModLoader; cannot load mod.");
                return;
            }

            var p = _modInitMethod.GetParameters().FirstOrDefault()?.ParameterType;
            if (p == null)
            {
                SanabiLogger.LogError("Mod init method has no parameters; cannot call it.");
                return;
            }

            object arg;
            if (p == typeof(Assembly))
                arg = modAssembly;
            else if (p.IsArray && p.GetElementType() == typeof(Assembly))
                arg = new Assembly[] { modAssembly };
            else if (typeof(IEnumerable<Assembly>).IsAssignableFrom(p))
                arg = (IEnumerable<Assembly>)new Assembly[] { modAssembly };
            else
            {
                SanabiLogger.LogError($"Unsupported mod init parameter type: {p.FullName}");
                return;
            }

            var target = _modInitMethod.IsStatic ? null : (object)modLoader;
            _modInitMethod.Invoke(target, new object[] { arg });
            SanabiLogger.LogInfo($"Invoked ModLoader init for {modAssembly.FullName} (param: {p.FullName})");

            if (GetModAssemblyEntryPoint(modAssembly) is { } modEntry)
                Enter(modEntry, async: true);
        }
        catch (Exception ex)
        {
            SanabiLogger.LogError($"LoadModAssembly error for {modAssembly.FullName}: {ex}");
        }
    }
}
