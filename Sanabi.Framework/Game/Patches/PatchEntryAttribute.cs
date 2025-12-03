namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Attribute put only on static methods, to be invoked on the game process
///         on different RunLevels.
///
///     Methods must either have no arguments, or have their only
///         argument be a `Dictionary<string, Assembly?>`. When invoked,
///         this dictionary will be <see cref="Managers.AssemblyManager.Assemblies"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class PatchEntryAttribute(PatchRunLevel runLevel, bool async = false) : Attribute
{
    public PatchRunLevel RunLevel { get; } = runLevel;

    /// <summary>
    ///     Is this patch run on another thread when entered?
    /// </summary>
    public bool Async { get; } = async;
}

[Flags]
public enum PatchRunLevel : byte
{
    None = 0,

    /// <summary>
    ///     Run when engine assemblies are done being loaded, but
    ///         the game entry point hasn't been started.
    /// </summary>
    Engine = 1 << 0,

    /// <summary>
    ///     Run when all game assemblies are loaded and initialised,
    ///         after the game entry point has been started.
    /// </summary>
    Content = 1 << 1,

    /// <summary>
    ///     Both engine and content.
    /// </summary>
    Full = Engine | Content
}
