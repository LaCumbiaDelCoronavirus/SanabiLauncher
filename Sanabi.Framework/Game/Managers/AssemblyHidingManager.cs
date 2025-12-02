using System.Reflection;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Manages hiding assemblies from the 999999 different
///         places that list every assembly.
/// </summary>
public static class AssemblyHidingManager
{
    /// <summary>
    ///     Assemblies hidden from view.
    /// </summary>
    private static List<Assembly> _hiddenAssemblies = new();

    public static void Initialise()
    {
        HarmonyManager.Initialise();
    }

    /// <summary>
    ///     Hides the first assembly whose <see cref="Assembly.FullName"/>
    ///         matches the given string.
    /// </summary>
    public static void HideAssembly(string identifier)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
        {
            if (assembly.FullName is not { } fullName ||
                !fullName.Contains(identifier))
                continue;

            HideAssembly(assembly);
        }
    }

    /// <summary>
    ///     Hides an assembly.
    /// </summary>
    public static void HideAssembly(Assembly assembly)
        => _hiddenAssemblies.Add(assembly);

    private static void PatchDetectionVectors()
    {
        //PatchHelpers.PatchMethod()
    }
}
