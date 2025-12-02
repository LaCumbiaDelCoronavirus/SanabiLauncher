using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Static container for a <see cref="HarmonyContainer"/>, which
///         itself is just a wrapper for a <see cref="Harmony"/>.
/// </summary>
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
    ///     Located assemblies.
    /// </summary>
    public static readonly Dictionary<string, Assembly?> Assemblies = new();

    /// <summary>
    ///     Called once when every necessary assembly has
    ///         been resolved.
    /// </summary>
#pragma warning disable CA2211 // Non-constant fields should not be visible
    public static Action? OnAssembliesFulfilled;
#pragma warning restore CA2211

    /// <summary>
    ///     Tries to retrieve an assembly from cache.
    /// </summary>
    public static bool TryGetAssembly(string assemblyName, [MaybeNullWhen(false)] out Assembly assembly)
        => Assemblies.TryGetValue(assemblyName, out assembly);

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
        if (_fulfilled)
            return;

        var fulfilledCount = 0;
        foreach (var (assemblyName, _) in Assemblies)
        {
            if (_necessaryAssemblyNames.Contains(assemblyName))
                fulfilledCount++;
        }

        Debug.Assert(fulfilledCount <= _necessaryAssemblyNames.Length, "fulfilledCount was higher than #_necessaryAssemblyNames");
        if (fulfilledCount == _necessaryAssemblyNames.Length)
        {
            _fulfilled = true;
            OnAssembliesFulfilled?.Invoke();

            return;
        }
    }

    /// <summary>
    ///     Looks at all existing assemblies in the current
    ///         <see cref="AppDomain"/> and saves the ones
    ///         that are necessary.
    /// </summary>
    public static void QueryAssemblies()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var necessaryAssemblyName in _necessaryAssemblyNames)
            {
                if (assembly.FullName?.Contains(necessaryAssemblyName) == true)
                {
                    Assemblies[necessaryAssemblyName] = assembly;
                    Console.WriteLine($"BruteForce-Assembly-Found: {necessaryAssemblyName}");
                }
            }
        }

        CheckFulfillment();
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        var loadedAssembly = args.LoadedAssembly;

        foreach (var necessaryAssemblyName in _necessaryAssemblyNames)
        {
            if (loadedAssembly.FullName?.Contains(necessaryAssemblyName) == true)
            {
                Assemblies[necessaryAssemblyName] = loadedAssembly;
                Console.WriteLine($"Loaded-Assembly-Found: {necessaryAssemblyName}");
            }
        }

        CheckFulfillment();
    }
}
