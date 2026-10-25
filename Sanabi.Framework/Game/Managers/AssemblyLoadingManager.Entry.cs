using System.Reflection;
using Sanabi.Framework.Misc;
using Sanabi.Framework.Patching;
using SS14.Launcher;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Handles loading external mods from the mods directory,
///         into the game.
/// </summary>
public static partial class AssemblyLoadingManager
{
    private static readonly Queue<MethodInfo> _pendingEntSysManUpdateCallbacks = new();

    /// <summary>
    ///     GameShared entry-point instances of our loaded mods. We drive their lifecycle ourselves (see the
    ///         BroadcastRunLevel/BroadcastUpdate/Shutdown postfixes) instead of registering them in the engine's
    ///         Mods list, so our mods never surface in <c>BaseModLoader.LoadedModules</c>.
    /// </summary>
    private static readonly List<object> _detachedEntryPoints = new();

    /// <summary>
    ///     Invokes a static method and enters it. The method may
    ///         have no parameters.
    /// </summary>
    /// <param name="async">Whether to run the method on another task.</param>
    public static void Enter(MethodInfo entryMethod, bool async = false)
    {
        var parameters = entryMethod.GetParameters();
        object?[]? invokedParameters = null;
        if (parameters.Length == 1 &&
            parameters[0].ParameterType == AssemblyManager.Assemblies.GetType())
            invokedParameters = [AssemblyManager.Assemblies];

        /*
        when only parameter is string:
        - give mod data path
        when only parameter is assemblies dict:
        - give assemblies dict
        when first parameter is assemblies dict and second parameter is string:
        - give assemblies dict
        - give mod data path
        */

        if (parameters.Length >= 1)
        {
            if (parameters[0].ParameterType == AssemblyManager.Assemblies.GetType())
            {
                if (parameters.Length == 2 &&
                    parameters[1].ParameterType == typeof(string))
                    invokedParameters = [AssemblyManager.Assemblies, LauncherPaths.SanabiModDataPath];
                else
                    invokedParameters = [AssemblyManager.Assemblies];
            }
            else if (parameters[0].ParameterType == typeof(string))
                invokedParameters = [LauncherPaths.SanabiModDataPath];
        }

        if (async)
            _ = Task.Run(() => InvokeEntry(entryMethod, invokedParameters));
        else
            InvokeEntry(entryMethod, invokedParameters);

        Console.WriteLine($"Entered patch at {entryMethod.DeclaringType?.FullName}");
    }

    /// <summary>
    ///     Invokes an entry method, logging instead of throwing so one failing
    ///         patch can't stop the rest.
    /// </summary>
    private static void InvokeEntry(MethodInfo entryMethod, object?[]? invokedParameters)
    {
        try
        {
            entryMethod.Invoke(null, invokedParameters);
        }
        catch (Exception exception)
        {
            var actual = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            SanabiLogger.LogError($"Patch entry {entryMethod.DeclaringType?.FullName}.{entryMethod.Name} failed: {actual}");
        }
    }

    public static void EnterDb(MethodInfo entryMethod, bool async = false)
    {
        var parameters = entryMethod.GetParameters();
        object?[]? invokedParameters = null;
        if (parameters.Length == 1 &&
            parameters[0].ParameterType == AssemblyManager.Assemblies.GetType())
            invokedParameters = [AssemblyManager.Assemblies];

        if (async)
            _ = Task.Run(() => InvokeEntry(entryMethod, invokedParameters));
        else
            InvokeEntry(entryMethod, invokedParameters);

        Console.WriteLine($"Entered patch at {entryMethod.DeclaringType?.FullName}");
    }

    private static void ModLoaderPostfix(ref dynamic __instance)
    {
        while (_dataPendingAssemblyLoad.TryDequeue(out var modData))
        {
            if (modData.Assembly != null)
                LoadModAssemblyIntoGame((object)__instance, modData);
        }
    }

    // GameShared lifecycle for our detached mods: the engine's BroadcastRunLevel/Update/Shutdown only iterate
    // its own Mods list (which our mods are deliberately not in), so we postfix those broadcasts and drive our
    // entry points at the exact same phase. __args avoids referencing the engine's ModRunLevel/ModUpdateLevel.
    private static void BroadcastRunLevelPostfix(object[] __args)
    {
        var level = __args[0]?.ToString();
        var lifecycle = level switch
        {
            "PreInit" => _gsPreInit,
            "Init" => _gsInit,
            "PostInit" => _gsPostInit,
            _ => null,
        };

        if (lifecycle is null)
            return;

        foreach (var entryPoint in _detachedEntryPoints)
            lifecycle.Invoke(entryPoint, null);
    }

    private static void BroadcastUpdatePostfix(object[] __args)
    {
        foreach (var entryPoint in _detachedEntryPoints)
            _gsUpdate.Invoke(entryPoint, [__args[0], __args[1]]); // (ModUpdateLevel level, FrameEventArgs args)
    }

    private static void ShutdownPostfix()
    {
        foreach (var entryPoint in _detachedEntryPoints)
        {
            _gsShutdown.Invoke(entryPoint, null);
            _gsDispose.Invoke(entryPoint, null);
        }
    }

    private static void EntSysManInitPostfix()
    {
        while (_pendingEntSysManUpdateCallbacks.TryDequeue(out var callbackInfo))
            callbackInfo.Invoke(null, null);
    }

    /// <summary>
    ///     Tries to get the entry point type for a mod assembly.
    ///         This is compatible with Marsey patches.
    /// </summary>
    public static Type? GetModAssemblyEntryType(Assembly assembly)
        => assembly.GetType("PatchEntry") ?? assembly.GetType("ModEntry") ?? assembly.GetType("MarseyEntry");

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

    /// <summary>
    ///     Loads a mod assembly into the game WITHOUT routing through the engine's <c>BaseModLoader.InitMod</c>
    ///         (which would add it to the engine's Mods list and so to <c>LoadedModules</c>). We reproduce the
    ///         one load-bearing effect — registering the assembly's types with the engine ReflectionManager —
    ///         and drive the GameShared lifecycle ourselves via <see cref="_detachedEntryPoints"/>.
    /// </summary>
    /// <param name="modLoader">The live engine ModLoader instance (to read its ReflectionManager/dependencies).</param>
    private static void LoadModAssemblyIntoGame(object modLoader, ILoadedModData modData)
    {
        var assembly = modData.Assembly!;

        AssemblyHidingManager.HideAssembly(assembly);
        PortModMarseyLogger(assembly);

        // 1. Register types with the engine so EntitySystems/Components/Prototypes/serializers get discovered.
        //    This is the only engine step a mod genuinely needs; it does not touch the Mods/LoadedModules list.
        var reflectionManager = _modLoaderReflectionManagerField.GetValue(modLoader);
        _loadAssembliesMethod.Invoke(reflectionManager, [(IEnumerable<Assembly>)[assembly]]);

        // 2. Discover + instantiate GameShared entry points ourselves (mirroring InitMod, minus Mods.Add).
        //    We are a hidden-assembly caller, so GetTypes() returns the real, unfiltered type list here.
        var dependencies = _modLoaderDependenciesField.GetValue(modLoader);
        var testingCallbacks = _modLoaderTestingCallbacksField.GetValue(modLoader);
        foreach (var entryType in assembly.GetTypes().Where(type => _gameSharedType.IsAssignableFrom(type)))
        {
            var entryPoint = Activator.CreateInstance(entryType)!;
            _gsDependenciesProperty.SetValue(entryPoint, dependencies);
            if (testingCallbacks != null)
                _gsSetTestingCallbacks.Invoke(entryPoint, [testingCallbacks]);

            _detachedEntryPoints.Add(entryPoint);
        }

        if (GetModAssemblyEntryType(assembly) is { } modEntryType)
        {
            if (modEntryType?.GetMethod("Entry", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is { } modEntryMethod)
                Enter(modEntryMethod, async: false); // Non-async makes it possible to print logs properly

            if (modEntryType?.GetMethod("AfterEntitySystemsLoaded", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is { } entitySystemsLoadedMethod)
                _pendingEntSysManUpdateCallbacks.Enqueue(entitySystemsLoadedMethod);
        }
    }
}
