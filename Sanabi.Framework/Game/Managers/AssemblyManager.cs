using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sanabi.Framework.Game.Managers;

public static class AssemblyManager
{
    /// <summary>
    ///     Assemblies we need to reference in the future.
    /// </summary>
    private static readonly string[] _necessaryAssemblyNames =
    {
        "Robust.Client",
        "Robust.Shared",
        "Content.Client",
        "Content.Shared"
    };

    private static bool _fulfilled = false;

    /// <summary>
    ///     Guards every dictionary here; assembly load events fire on arbitrary threads.
    ///         Callbacks are never invoked while holding it.
    /// </summary>
    private static readonly object _lock = new();

    /// <summary>
    ///     All assemblies ever found.
    /// </summary>
    public static readonly Dictionary<string, Assembly> AssembliesIncludingUnnecessary = [];

    /// <summary>
    ///     Located necessary assemblies.
    /// </summary>
    public static readonly Dictionary<string, Assembly> Assemblies = [];

    /// <summary>
    ///     Assembly names and list of actions that should be invoked when they
    ///         get loaded.
    /// </summary>
    public static readonly Dictionary<string, List<Action<Assembly>>> SpecificAssemblyCallbacks = new();

    /// <summary>
    ///     Called once when every necessary assembly has
    ///         been resolved.
    /// </summary>
#pragma warning disable CA2211 // Non-constant fields should not be visible
    public static Action? OnAssembliesFulfilled;
#pragma warning restore CA2211

    /// <summary>
    ///     Tries to retrieve an assembly from cache. Can switch between <see cref="Assemblies"/> and
    ///         <see cref="AssembliesIncludingUnnecessary"/>.
    /// </summary>
    public static bool TryGetAssembly(string assemblyName, [MaybeNullWhen(false)] out Assembly assembly, bool includeUnnecessary = false)
    {
        lock (_lock)
            return (includeUnnecessary ? AssembliesIncludingUnnecessary : Assemblies).TryGetValue(assemblyName, out assembly);
    }

    /// <summary>
    ///     Runs a callback once ever when the given assembly gets loaded.
    ///         The callback is run immediately if already loaded.
    /// </summary>
    public static void SubscribeSpecificAssemblyOnce(string assemblyName, Action<Assembly> callback)
    {
        Assembly? existingAssembly;
        lock (_lock)
        {
            if (!AssembliesIncludingUnnecessary.TryGetValue(assemblyName, out existingAssembly))
            {
                ref var reference = ref CollectionsMarshal.GetValueRefOrAddDefault(SpecificAssemblyCallbacks, assemblyName, out var callbacksExist);
                if (!callbacksExist)
                    reference = [];

                reference!.Add(callback);
                return;
            }
        }

        callback.Invoke(existingAssembly);
    }

    /// <summary>
    ///     Subscribes to assembly loads to look
    ///         for the ones we need.
    /// </summary>
    public static void Subscribe()
    {
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    /// <summary>
    ///     Unsubscribes from assembly loads.
    /// </summary>
    public static void Unsubscribe()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
    }

    private static void CheckFulfillment()
    {
        int fulfilledCount;
        lock (_lock)
        {
            if (_fulfilled)
                return;

            fulfilledCount = 0;
            foreach (var (assemblyName, _) in Assemblies)
            {
                if (_necessaryAssemblyNames.Contains(assemblyName))
                    fulfilledCount++;
            }

            Debug.Assert(fulfilledCount <= _necessaryAssemblyNames.Length, "fulfilledCount was higher than #_necessaryAssemblyNames");
            if (fulfilledCount != _necessaryAssemblyNames.Length)
                return;

            _fulfilled = true;
        }

        OnAssembliesFulfilled?.Invoke();
        Console.WriteLine($"Assembly-Mng-Fulfilled: {fulfilledCount}");
    }

    /// <summary>
    ///     Looks at all existing assemblies in the current
    ///         <see cref="AppDomain"/> and saves the ones
    ///         that are necessary.
    /// </summary>
    public static void QueryAssemblies()
    {
        var pendingCallbacks = new List<(Action<Assembly> Callback, Assembly Assembly)>();

        lock (_lock)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name is not { } name)
                    continue;

                AssembliesIncludingUnnecessary[name] = assembly;

                foreach (var necessaryAssemblyName in _necessaryAssemblyNames)
                {
                    // Dont do Contains; Robust.Shared.Maths shouldn't count for Robust.Shared
                    if (name != necessaryAssemblyName)
                        continue;

                    Assemblies[necessaryAssemblyName] = assembly;
                    Console.WriteLine($"Assembly-Mng-BruteForce-Found: {necessaryAssemblyName}");
                }
            }

            foreach (var (assemblyCallbackKey, assemblyCallbacks) in SpecificAssemblyCallbacks)
            {
                if (!AssembliesIncludingUnnecessary.TryGetValue(assemblyCallbackKey, out var callbackAssembly))
                    continue;

                foreach (var assemblyCallback in assemblyCallbacks)
                    pendingCallbacks.Add((assemblyCallback, callbackAssembly));
            }
        }

        foreach (var (assemblyCallback, callbackAssembly) in pendingCallbacks)
            assemblyCallback.Invoke(callbackAssembly);

        CheckFulfillment();
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        var loadedAssembly = args.LoadedAssembly;
        if (loadedAssembly.GetName().Name is not { } assemblyName)
            return;

        List<Action<Assembly>>? callbacks;
        lock (_lock)
        {
            AssembliesIncludingUnnecessary[assemblyName] = loadedAssembly;

            foreach (var necessaryAssemblyName in _necessaryAssemblyNames)
            {
                // Exact match; Content.Shared.Database shouldn't count for Content.Shared
                if (string.Equals(assemblyName, necessaryAssemblyName, StringComparison.Ordinal))
                {
                    Assemblies[necessaryAssemblyName] = loadedAssembly;
                    Console.WriteLine($"Assembly-Mng-Loaded-Found: {necessaryAssemblyName}");
                }
            }

            SpecificAssemblyCallbacks.Remove(assemblyName, out callbacks);
        }

        if (callbacks != null)
        {
            foreach (var callback in callbacks)
                callback.Invoke(loadedAssembly);
        }

        CheckFulfillment();
    }
}
