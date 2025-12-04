using HarmonyLib;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Container for a <see cref="HarmonyLib.Harmony"/> instance.
/// </summary>
public static class HarmonyManager
{
    private static Harmony _harmony = default!;

    /// <summary>
    ///     Null if the manager hasn't been initialised
    ///         yet.
    /// </summary>
    public static Harmony Harmony => _harmony;

    /// <summary>
    ///     O algo
    /// </summary>
    public static void Initialise()
    {
        Console.WriteLine($"Inited harmony");
        _harmony ??= new("our.sanabi.goida.raiders.2025.nabegali");
    }

    /*
        GEEEG ENGINECACAS WHAT IS THIS?
        https://github.com/space-wizards/RobustToolbox/blob/6bbeaeeba6bc6a20583af00b99e4f1adb0f00410/Robust.Client/GameController/GameController.Standalone.cs#L77C1-L94C10

        [SuppressMessage("ReSharper", "FunctionNeverReturns")]
        static unsafe GameController()
        {
            var n = "0" +"H"+"a"+"r"+"m"+ "o"+"n"+"y";

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == n)
                {
                    uint fuck;
                    var you = &fuck;
                    while (true)
                    {
                        *(you++) = 0;
                    }
                }
            }
        }
    */
    public static void BypassAnticheat()
    {
        PatchHelpers.PatchPrefixFalse(ReflectionManager.GetTypeByQualifiedName("Robust.Client.GameController").TypeInitializer!);
    }
}
