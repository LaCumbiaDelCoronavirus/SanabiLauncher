using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Sanabi.Framework.Game.Patches;

namespace Sanabi.Framework.Data;

/// <summary>
///     Source-generated JSON metadata for the types sent over IPC. Source generation (rather than reflection)
///         keeps (de)serialization working in the trimmed game-process publish (<c>RobustILLink</c>), where the
///         reflection-based serializer would be stripped and silently fall back to defaults.
/// </summary>
[JsonSerializable(typeof(SanabiConfig))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class SanabiJsonContext : JsonSerializerContext;

/// <summary>
///     Contains all SanabiLauncher-specific configuration values, passed from the launcher UI process to the
///         loader (game) process over IPC.
///
///     Serialized as JSON (see <see cref="Game.Managers.IpcManager.RunObjectPipeServer{T}"/>), so unlike the old
///         blittable struct it may carry variable-length data such as strings and lists of strings.
/// </summary>
[UsedImplicitly]
public sealed class SanabiConfig
{
    public static SanabiConfig ProcessConfig = new();

    public PatchRunLevel PatchRunLevel { get; set; } = PatchRunLevel.None;

    public bool RunHwidPatch { get; set; } = true;
    public bool RunBetterFullscreenPatch { get; set; } = true;
    public bool RunUserDataPatch { get; set; } = true;

    public ulong HwidPatchSeed { get; set; } = 1ul;

    public bool LoadInternalMods { get; set; } = false;
    public bool LoadExternalMods { get; set; } = false;

    public bool WaitForDebugger { get; set; } = false;

    /// <summary>
    ///     Names (<see cref="Game.Managers.ILoadedModData.Name"/>) of the external mods the user enabled. Replaces
    ///         the old packed bit-flags field; order is not significant.
    /// </summary>
    public List<string> EnabledExternalMods { get; set; } = new();
}

public static class SanabiConfigExtensions
{
    /// <summary>
    ///     Configures the given <see cref="SanabiConfig"/>
    ///         according to the CVars of the given DataManager.
    /// </summary>
    /// <returns>The configured <see cref="SanabiConfig"/>.</returns>
    public static SanabiConfig Configure(this SanabiConfig config, dynamic dataManager)
    {
        config.PatchRunLevel = dataManager.GetCVar(SanabiCVars.PatchingEnabled) ?
            (dataManager.GetCVar(SanabiCVars.PatchingLevel) ? PatchRunLevel.Full : PatchRunLevel.Engine) :
            PatchRunLevel.None;

        config.RunHwidPatch = dataManager.GetCVar(SanabiCVars.HwidPatchEnabled);
        config.RunBetterFullscreenPatch = dataManager.GetCVar(SanabiCVars.BetterFullscreenPatchEnabled);
        config.RunUserDataPatch = dataManager.GetCVar(SanabiCVars.UserDataPatchEnabled);
        config.HwidPatchSeed = BitConverter.ToUInt64(BitConverter.GetBytes(dataManager.GetActiveAccountCVarOrDefault(SanabiAccountCVars.SpoofingSeed)), 0);
        config.LoadInternalMods = dataManager.GetCVar(SanabiCVars.LoadInternalMods);
        config.LoadExternalMods = dataManager.GetCVar(SanabiCVars.LoadExternalMods);
        config.WaitForDebugger = dataManager.GetCVar(SanabiCVars.WaitForDebugger);
        config.EnabledExternalMods = EnabledExternalModsCVar.Parse((string)dataManager.GetCVar(SanabiCVars.EnabledExternalMods));

        return config;
    }
}

/// <summary>
///     (De)serialization for the <see cref="SanabiCVars.EnabledExternalMods"/> CVar. The config DB only persists
///         primitive-typed CVars, so the enabled-mod name list rides inside a string as a JSON array.
/// </summary>
public static class EnabledExternalModsCVar
{
    public static List<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new();

        try
        {
            return JsonSerializer.Deserialize(raw, SanabiJsonContext.Default.ListString) ?? new();
        }
        catch (JsonException)
        {
            // Corrupt/legacy value: treat as "nothing selected" rather than crashing the launcher.
            return new();
        }
    }

    public static string Serialize(IEnumerable<string> modNames)
        => JsonSerializer.Serialize(modNames.ToList(), SanabiJsonContext.Default.ListString);
}
