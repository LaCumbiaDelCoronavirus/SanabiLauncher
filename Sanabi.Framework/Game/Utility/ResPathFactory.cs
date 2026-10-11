using System.Reflection;
using Sanabi.Framework.Game.Managers;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Utility;

public static class ResPathFactory
{
    // Resolved lazily so a missing engine member only fails when actually used, not on type init
    private static readonly Lazy<Type> _resPathType = new(()
        => ReflectionManager.GetTypeByQualifiedName("Robust.Shared.Utility.ResPath", except: true));
    private static readonly Lazy<ConstructorInfo> _resPathConstructor = new(()
        => PatchHelpers.GetConstructor(ResPathType, [typeof(string)])
            ?? throw new InvalidOperationException("Couldn't resolve ResPath(string) constructor!"));
    private static readonly Lazy<MethodInfo> _resPathToRootedPath = new(()
        => PatchHelpers.GetMethod(ResPathType, "ToRootedPath", except: true)!);

    private static readonly Lazy<Type> _resPathListType = new(() => typeof(List<>).MakeGenericType(ResPathType));
    private static readonly Lazy<MethodInfo> _resPathListAdd = new(() => ResPathListType.GetMethod("Add")!);

    public static Type ResPathType => _resPathType.Value;
    private static ConstructorInfo ResPathConstructor => _resPathConstructor.Value;
    private static MethodInfo ResPathToRootedPath => _resPathToRootedPath.Value;
    private static Type ResPathListType => _resPathListType.Value;
    private static MethodInfo ResPathListAdd => _resPathListAdd.Value;

    /// <summary>
    ///     IEnumerable&lt;ResPath&gt;, for building patch return values that mirror
    ///         RT methods returning that type (e.g. WritableDirProvider.Find).
    /// </summary>
    public static Type ResPathEnumerableType => typeof(IEnumerable<>).MakeGenericType(ResPathType);

    public static object Construct(string path)
        => ResPathConstructor.Invoke([path]);

    public static object ToRootedPath(object resPath)
        => ResPathToRootedPath.Invoke(resPath, null)!;

    /// <summary>
    ///     Builds a real List&lt;ResPath&gt; (boxed as object) out of canonical path strings,
    ///         rooting each one to match what RT itself hands back.
    /// </summary>
    public static object BuildRootedResPathList(IEnumerable<string> canonPaths)
    {
        var list = Activator.CreateInstance(ResPathListType)!;
        foreach (var canonPath in canonPaths)
            ResPathListAdd.Invoke(list, [ToRootedPath(Construct(canonPath))]);

        return list;
    }
}
