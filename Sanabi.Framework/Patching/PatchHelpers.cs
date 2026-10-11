using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sanabi.Framework.Game.Managers;

namespace Sanabi.Framework.Patching;

/// <summary>
///     Helpers for patching.
///         Don't assume that these helpers will work on async
///         or generic methods.
/// </summary>
/// <remarks>
///     Relies on <see cref="HarmonyManager.Harmony"/>.
/// </remarks>
public static partial class PatchHelpers
{
    /// <summary>
    ///     Patches a <see cref="MethodBase"/> with a false-returning prefix;
    ///         i.e. stops a method from executing any code.
    /// </summary>
    /// <param name="method">Method to patch.</param>
    public static void PatchPrefixFalse(MethodBase method)
        => HarmonyManager.Harmony.Patch(method, prefix: new HarmonyMethod(FalsePrefix));

    internal static bool FalsePrefix()
    {
        return false;
    }

    /// <summary>
    ///     Takes constructor parameter-types and parameters and invokes them
    ///         to create an instance of the thing being constructed.
    ///
    ///      Assumes the constructor exists, otherwise throws.
    ///         This variant tries to get the type from it's fully qualified name.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static dynamic GetConstructorAndMakeInstance(string fullyQualifiedTypeName, Type[] parameterTypes, object?[]? parameters)
    {
        var type = ReflectionManager.GetTypeByQualifiedName(fullyQualifiedTypeName, except: true);
        return GetConstructorAndMakeInstance(type, parameterTypes, parameters);
    }

    /// <summary>
    ///      Assumes the constructor exists, otherwise throws.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ConstructorInfo GetConstructor(Type type, Type[] parameterTypes)
        => type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly, parameterTypes)!;

    /// <summary>
    ///     Takes constructor parameter-types and parameters and invokes them
    ///         to create an instance of the thing being constructed.
    ///
    ///      Assumes the constructor exists, otherwise throws.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static dynamic GetConstructorAndMakeInstance(Type type, Type[] parameterTypes, object?[]? parameters)
        => GetConstructor(type, parameterTypes).Invoke(parameters);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static MethodInfo? GetMethod(Type? type, string methodName, Type[]? parameters = null, bool except = false)
    {
        var returnedType = AccessTools.Method(type, methodName, parameters);
        if (except &&
            returnedType is not { })
            throw new InvalidOperationException($"Couldn't resolve method {methodName} on type {type?.ToString() ?? "N/A"}!");

        return returnedType;
    }

    /// <summary>
    ///     Tries to retrieve the constant value of a static field from the given type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? GetConstantFieldValue(Type? type, string fieldName)
    {
        return type?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetRawConstantValue();
    }

    /// <summary>
    ///     Helper for setting a field on something. Does nothing
    ///         if field doesn't exist.
    /// </summary>
    /// <param name="thing">Leave as null if the field is static.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? ForceSetField(object? thing, Type thingType, string fieldName, object? newValue)
    {
        var fieldInfo = thingType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (fieldInfo == null)
            return newValue;

        fieldInfo.SetValue(thing, newValue);
        return newValue;
    }

    /// <summary>
    ///     Tries to patch a method by the names of the necessary
    ///         classes and methods.
    /// </summary>
    /// <param name="targetQualifiedTypeName">Fully qualified type-name of the target class.</param>
    /// <param name="targetMethodName">Name of the target method.</param>
    /// <param name="patchQualifiedTypeName">Fully qualified type-name of the patch class.</param>
    /// <param name="patchMethodName">Name of the patch method.</param>
    /// <param name="patchType">How the patch will be applied.</param>
    /// <param name="targetMethodParameters">Parameters taken by the target method.</param>
    /// <param name="patchMethodParameters">Parameters taken by the patch method.</param>
    public static void PatchMethod(
        string targetQualifiedTypeName,
        string targetMethodName,
        string patchQualifiedTypeName,
        string patchMethodName,
        HarmonyPatchType patchType,
        Type[]? targetMethodParameters = null,
        Type[]? patchMethodParameters = null)
    {
        if (!ReflectionManager.TryGetTypeByQualifiedName(targetQualifiedTypeName, out var targetClass) ||
            !ReflectionManager.TryGetTypeByQualifiedName(patchQualifiedTypeName, out var patchClass))
            return;

        PatchMethod(
            targetClass,
            targetMethodName,
            patchClass,
            patchMethodName,
            patchType,
            targetMethodParameters: targetMethodParameters,
            patchMethodParameters: patchMethodParameters
        );
    }

    // Inheritdoc is quite buggy here
    /// <summary>
    ///     Tries to patch a method by the classes and names of the
    ///         required methods.
    /// </summary>
    /// <param name="targetClass">Class where the target method is defined in.</param>
    /// <param name="targetMethodName">Name of the target method.</param>
    /// <param name="patchClass">Class where the patch method is defined in.</param>
    /// <param name="patchMethodName">Name of the patch method.</param>
    /// <param name="patchType">How the patch will be applied.</param>
    /// <param name="targetMethodParameters">Parameters taken by the target method.</param>
    /// <param name="patchMethodParameters">Parameters taken by the patch method.</param>
    public static void PatchMethod(
        Type? targetClass,
        string targetMethodName,
        Type? patchClass,
        string patchMethodName,
        HarmonyPatchType patchType,
        Type[]? targetMethodParameters = null,
        Type[]? patchMethodParameters = null)
    {
        if (targetClass == null ||
            patchClass == null)
            return;

        var targetMethod = ResolveMethod(targetClass, targetMethodName, targetMethodParameters);
        var patchMethod = ResolveMethod(patchClass, patchMethodName, patchMethodParameters);

        TryPatchMethod(targetMethod, patchMethod, patchType);
    }

    /// <summary>
    ///     Tries to get a method on a type, by it's name
    ///         and parameters. Will throw if method isnt found.
    /// </summary>
    /// <returns>Null if no method was found.</returns>
    // TODO: Logs
    public static MethodInfo? ResolveMethod(Type? type, string methodName, Type[]? methodParameters = null)
        => GetMethod(type, methodName, methodParameters, except: true);

    /// <summary>
    ///     Tries to patch a method by the names of the necessary
    ///         class and method. However, the patch method
    ///         and target class are already defined.
    ///
    ///     The delegate must not be a lambda expression, as if so
    ///         then an <see cref="InvalidProgramException"/> will be thrown.
    /// </summary>
    /// <param name="targetQualifiedTypeName">Fully qualified type-name of the target class.</param>
    /// <param name="targetMethodName">Name of the target method.</param>
    /// <param name="patchDelegate">The patch method.</param>
    /// <param name="patchType">How the patch will be applied.</param>
    /// <param name="targetMethodParameters">Parameters taken by the target method.</param>
    public static void PatchMethod(
        Type targetClass,
        string targetMethodName,
        Delegate patchDelegate,
        HarmonyPatchType patchType,
        Type[]? targetMethodParameters = null)
    {
        var targetMethod = ResolveMethod(targetClass, targetMethodName, targetMethodParameters);
        TryPatchMethod(targetMethod, patchDelegate.Method, patchType);
    }

    /// <summary>
    ///     Tries to patch a method by the names of the necessary
    ///         class and method. However, the patch method
    ///         and target class are already defined.
    ///
    ///     The delegate must not be a lambda expression, as if so
    ///         then an <see cref="InvalidProgramException"/> will be thrown.
    /// </summary>
    /// <param name="targetQualifiedTypeName">Fully qualified type-name of the target class.</param>
    /// <param name="targetMethodName">Name of the target method.</param>
    /// <param name="patchDelegate">The patch method.</param>
    /// <param name="patchType">How the patch will be applied.</param>
    /// <param name="targetMethodParameters">Parameters taken by the target method.</param>
    public static void PatchConstructor(
        Type targetClass,
        Delegate patchDelegate,
        HarmonyPatchType patchType,
        Type[]? targetMethodParameters = null)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var targetMethod = targetClass.GetConstructor(flags, targetMethodParameters ?? []);

        // No explicit parameters and no parameterless constructor; fall back to the only constructor
        if (targetMethod == null &&
            targetMethodParameters == null)
        {
            var constructors = targetClass.GetConstructors(flags);
            if (constructors.Length == 1)
                targetMethod = constructors[0];
        }

        if (targetMethod == null)
            throw new ArgumentException($"No constructor for type {targetClass} matches the given arguments!");

        TryPatchMethod(targetMethod, patchDelegate.Method, patchType);
    }

    /// <summary>
    ///     Tries to patch a method by the names of the necessary
    ///         class and method. However, the patch method
    ///         is already defined.
    /// </summary>
    /// <param name="targetQualifiedTypeName">Fully qualified type-name of the target class.</param>
    /// <param name="targetMethodName">Name of the target method.</param>
    /// <param name="patchDelegate">The patch method.</param>
    /// <param name="patchType">How the patch will be applied.</param>
    /// <param name="targetMethodParameters">Parameters taken by the target method.</param>
    public static void PatchMethod(
        string targetQualifiedTypeName,
        string targetMethodName,
        Delegate patchDelegate,
        HarmonyPatchType patchType,
        Type[]? targetMethodParameters = null)
    {
        PatchMethod(
            ReflectionManager.GetTypeByQualifiedName(targetQualifiedTypeName, except: true),
            targetMethodName,
            patchDelegate,
            patchType,
            targetMethodParameters: targetMethodParameters
        );
    }

    public static void PatchMethod(
        MethodInfo? targetMethod,
        MethodInfo? patchMethod,
        HarmonyPatchType patchType)
    {
        TryPatchMethod(targetMethod, patchMethod, patchType);
    }

    public static void PatchMethod(
        MethodInfo? targetMethod,
        Delegate patchDelegate,
        HarmonyPatchType patchType)
    {
        TryPatchMethod(targetMethod, patchDelegate.Method, patchType);
    }

    /// <summary>
    ///     Returns the <see cref="MethodInfo"/> of the given
    ///         <see cref="Delegate"/>.
    /// </summary>
    public static MethodInfo GetMethod(Delegate @delegate)
        => @delegate.Method;
}
