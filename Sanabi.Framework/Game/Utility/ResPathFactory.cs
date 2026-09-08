using System.Reflection;
using Sanabi.Framework.Game.Managers;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Game.Utility;

public static class ResPathFactory
{
    public static readonly Type ResPathType = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.Utility.ResPath", except: true);
    private static readonly ConstructorInfo ResPathConstructor = PatchHelpers.GetConstructor(ResPathType, [typeof(string)]);
    private static readonly MethodInfo ResPathToRootedPath = PatchHelpers.GetMethod(ResPathType, "ToRootedPath")!;

    private static readonly Type ResPathListType = typeof(List<>).MakeGenericType(ResPathType);
    private static readonly MethodInfo ResPathListAdd = ResPathListType.GetMethod("Add")!;

    /// <summary>
    ///     IEnumerable&lt;ResPath&gt;, for building patch return values that mirror
    ///         RT methods returning that type (e.g. WritableDirProvider.Find).
    /// </summary>
    public static readonly Type ResPathEnumerableType = typeof(IEnumerable<>).MakeGenericType(ResPathType);

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
