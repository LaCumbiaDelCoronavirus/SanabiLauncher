using System.Reflection;

// These stub types mirror, by qualified name and member shape, the two engine members that
// AssemblyHidingManager.PatchEngineEnumerators() hooks. They let the stealth harness exercise those
// hooks (which it otherwise could not, with no real engine present). They carry no behaviour of their
// own: each exposes a list the harness fills and a getter/method the hook is applied to.

namespace Robust.Shared.Reflection
{
    /// <summary>
    ///     Stub of the engine's <c>ReflectionManager</c>. Only the members Sanabi hooks exist:
    ///         the <see cref="Assemblies"/> getter and <see cref="FindAllTypes"/>.
    /// </summary>
    public sealed class ReflectionManager
    {
        /// <summary>Backing list the harness populates with assemblies to enumerate.</summary>
        public List<Assembly> AssembliesList { get; } = [];

        /// <summary>Backing list the harness populates with types to enumerate.</summary>
        public List<Type> TypesList { get; } = [];

        // Matches ReflectionManager.Assemblies (IReadOnlyList<Assembly> getter).
        public IReadOnlyList<Assembly> Assemblies => AssembliesList;

        // Matches ReflectionManager.FindAllTypes() (parameterless, IEnumerable<Type>).
        public IEnumerable<Type> FindAllTypes() => TypesList;
    }
}

namespace Robust.Shared.ContentPack
{
    /// <summary>
    ///     Stub of the engine's <c>BaseModLoader</c>. Only <see cref="LoadedModules"/> exists, which Sanabi hooks.
    /// </summary>
    public sealed class BaseModLoader
    {
        /// <summary>Backing list the harness populates with "loaded" module assemblies.</summary>
        public List<Assembly> Modules { get; } = [];

        // Matches BaseModLoader.LoadedModules (IEnumerable<Assembly> getter).
        public IEnumerable<Assembly> LoadedModules => Modules;
    }
}
