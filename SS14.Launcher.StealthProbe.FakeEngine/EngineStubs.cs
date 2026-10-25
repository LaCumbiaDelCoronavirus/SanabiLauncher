using System.Reflection;

// This stub type mirrors, by qualified name and member shape, the engine members that
// AssemblyHidingManager.PatchEngineEnumerators() hooks. It lets the stealth harness exercise those
// hooks (which it otherwise could not, with no real engine present). It carries no behaviour of its
// own: it exposes lists the harness fills and the getter/method the hook is applied to.

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
