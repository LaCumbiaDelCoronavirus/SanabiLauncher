using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
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
    private static bool _stopPretending = true;

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

    // Bitmap will fix it
    public static bool GetIsModEnabled(long bitmap, int index)
        => (bitmap & (1L << index)) != 0;
    private static void ModLoaderPostfix(ref dynamic __instance)
    {
        _stopPretending = false;
        while (_dataPendingAssemblyLoad.TryDequeue(out var modData))
        {
            if (modData.Assembly != null)
                LoadModAssemblyIntoGame(ref __instance, modData);
        }
        _stopPretending = true;
    }

    // Dont change signature
    private static IEnumerable<Type> TransformEntryPoints(IEnumerable<Type> originalTypes, Assembly affectedAssembly)
    {
        if (_stopPretending)
            return originalTypes;

        var thisMethod = (MethodInfo)MethodBase.GetCurrentMethod()!;
        if (AssemblyHidingManager.ShouldHideAssembly(affectedAssembly.GetName().FullName))
            return affectedAssembly.GetTypes().Where(t => _gameSharedType.IsAssignableFrom(t));

        return originalTypes;
    }

    /*
        IL_006F: call       static System.Collections.Generic.IEnumerable`1<System.Type> System.Linq.Enumerable::Where(System.Collections.Generic.IEnumerable`1<System.Type> source, System.Func`2<System.Type, System.Boolean> predicate)
        [INSERT] ldarg.15
        [INSERT] call TransformEntryPoints
        IL_0074: callvirt   abstract virtual System.Collections.Generic.IEnumerator`1<System.Type> System.Collections.Generic.IEnumerable`1<System.Type>::GetEnumerator()
        IL_0079: stloc.1
    */
    private static IEnumerable<CodeInstruction> ModInitTranspiler(IEnumerable<CodeInstruction> instructionsEnum)
    {
        var instructions = new List<CodeInstruction>(instructionsEnum);

        var whereMethod = typeof(Enumerable)
            .GetMethods()
            .First(m => m.Name == "Where" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Type));

        var overrideMethod = ((Delegate)TransformEntryPoints).Method;

        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (instruction.opcode != OpCodes.Call ||
                !Equals(instruction.operand, whereMethod))
                continue;

            // i = ::Where(...), i+1 = ::GetEnumerator()

            CodeInstruction[] addedInstr = [
                // load first param: assembly
                new CodeInstruction(OpCodes.Ldarg_1),

                // the enumerable is already on the stack from Where call before this
                // now call our method which takes (Assembly, IEnumerable<Type>)
                new CodeInstruction(OpCodes.Call, ((Delegate)TransformEntryPoints).Method)
            ];

            // add after Where call
            instructions.InsertRange(index + 1, addedInstr);
            break;
        }

        return instructions;
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

    private static void LoadModAssemblyIntoGame(ref dynamic modLoader, ILoadedModData modData)
    {
        AssemblyHidingManager.HideAssembly(modData.Assembly!);
        PortModMarseyLogger(modData.Assembly!);

        _modInitMethod.Invoke(modLoader, (Assembly[])[modData.Assembly!]);

        if (GetModAssemblyEntryType(modData.Assembly!) is { } modEntryType)
        {
            if (modEntryType?.GetMethod("Entry", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is { } modEntryMethod)
                Enter(modEntryMethod, async: false); // Non-async makes it possible to print logs properly

            if (modEntryType?.GetMethod("AfterEntitySystemsLoaded", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is { } entitySystemsLoadedMethod)
                _pendingEntSysManUpdateCallbacks.Enqueue(entitySystemsLoadedMethod);
        }
    }
}
