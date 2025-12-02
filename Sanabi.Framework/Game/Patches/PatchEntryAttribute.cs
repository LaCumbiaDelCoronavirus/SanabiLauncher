namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Attribute put on methods that run on the game process
///         on different RunLevels.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class PatchEntryAttribute(PatchRunLevel RunLevel) : Attribute
{
    public PatchRunLevel RunLevel { get; } = RunLevel;
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
