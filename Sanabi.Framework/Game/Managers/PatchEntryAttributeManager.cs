using System.Reflection;
using Sanabi.Framework.Game.Patches;
using Sanabi.Framework.Misc;

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
    public static void ProcessRunLevel(PatchRunLevel runLevel, Assembly[]? targetAssemblies = null)
    {
        foreach (var assembly in targetAssemblies ?? AppDomain.CurrentDomain.GetAssemblies())
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

                    // Enter logs failures itself, but guard here too so nothing can abort the loop
                    try
                    {
                        AssemblyLoadingManager.Enter(method, attribute.Async);
                    }
                    catch (Exception exception)
                    {
                        SanabiLogger.LogError($"Patch entry {method.DeclaringType?.FullName}.{method.Name} failed: {exception}");
                    }
                }
            }
        }
    }
}
