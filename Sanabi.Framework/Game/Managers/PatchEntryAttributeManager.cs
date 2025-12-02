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
    /// </summary>
    public static void ProcessRunLevel(PatchRunLevel runLevel)
    {
        Console.WriteLine($"Running Patch RunLevel: {runLevel}");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                // Find all static parameterless static methods with [PatchEntry]
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                foreach (var method in methods)
                {
                    var attribute = method.GetCustomAttribute<PatchEntryAttribute>();
                    if (attribute == null ||
                        attribute.RunLevel != runLevel)
                        continue;

                    Console.WriteLine($"Starting patching {method.DeclaringType?.FullName}");
                    method.Invoke(null, null);
                    Console.WriteLine($"Patched {method.DeclaringType?.FullName}");
                }
            }
        }
    }
}
