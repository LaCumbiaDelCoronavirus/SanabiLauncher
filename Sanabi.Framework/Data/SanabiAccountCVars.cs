using JetBrains.Annotations;
using SS14.Common.Data.CVars;

namespace Sanabi.Framework.Data;

/// <summary>
///     Contains definitions for all SanabiLauncher-specific configuration values.
/// </summary>
[UsedImplicitly]
public static partial class SanabiAccountCVars
{
    /// <summary>
    ///     Seed to be used for generating HWID in <see cref="Sanabi.Framework.Game.Patches.HwidPatch"/>.
    ///         This is an ulong value bit-interpreted as a long. This is done because DataManager SQLite
    ///         is weird with ulong values.
    /// </summary>
    public static readonly CVarDef<long> SpoofedHwidSeed = CVarDef.Create("SpoofedHwidSeed", 1L);
}
