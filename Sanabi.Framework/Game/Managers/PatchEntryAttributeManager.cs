using System.Reflection;
using Sanabi.Framework.Game.Patches;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Handles <see cref="PatchEntryAttribute"/>.
/// </summary>
public static class PatchEntryAttributeManager
{
    /// <summary>
    ///     Invokes every method with <see cref="PatchEntryAttribute"/>
    ///         specified to the given <see cref="PatchRunLevel"/>.
    ///
    ///     Applicable methods with exactly 1 argument will be given a
    ///         dictionary of every assembly known by <see cref="AssemblyManager"/>.
    /// </summary>
    public static void ProcessRunLevel(PatchRunLevel runLevel)
    {
        Console.WriteLine($"Running Patch RunLevel: {runLevel}");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in assembly.GetTypes())
            {
                // Find all static methods with [PatchEntry]
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                foreach (var method in methods)
                {
                    var attribute = method.GetCustomAttribute<PatchEntryAttribute>();
                    if (attribute == null ||
                        !attribute.RunLevel.HasFlag(runLevel))
                        continue;

                    var parameters = method.GetParameters();
                    object?[]? invokedParameters = null;
                    if (parameters.Length == 1 &&
                        parameters[0].ParameterType == AssemblyManager.Assemblies.GetType())
                        invokedParameters = [AssemblyManager.Assemblies];

                    if (attribute.Async)
                        _ = Task.Run(async () => method.Invoke(null, invokedParameters));
                    else
                        method.Invoke(null, invokedParameters);

                    Console.WriteLine($"Entered patch at {method.DeclaringType?.FullName}");
                }
            }
        }
    }
}
