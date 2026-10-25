using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Sanabi.Framework.Data;
using Sanabi.Framework.Game.Patches;
using Sanabi.Framework.Game.Utility;
using Sanabi.Framework.Misc;
using Sanabi.Framework.Patching;
using SS14.Launcher;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Handles loading external mods from the mods directory,
///         into the game.
/// </summary>
public static partial class AssemblyLoadingManager
{
    public static int TotalExternalModCount = 0;
    private static readonly Queue<ILoadedModData> _dataPendingAssemblyLoad = new(); // Important to be Queue rather than Stack to preserve order of assemblies
    private static readonly Queue<object> _loadersPendingMount = new(); // Important to be Queue rather than Stack to preserve order of assemblies
    private static MethodInfo _iResourceManagerAddRootsMethod = default!; // has to be interface or else it cant get called on dynamic or whatever IDFK
    private static ConstructorInfo _dirLoaderConstructorData = default!;
    private static Type _gameSharedType = default!;

    // Detached mod-loading: we replicate the one load-bearing step of the engine's BaseModLoader.InitMod
    // (IReflectionManager.LoadAssemblies, for type discovery) and drive the GameShared lifecycle ourselves,
    // WITHOUT adding a ModInfo to the engine's Mods list. That way our mods never appear in
    // BaseModLoader.LoadedModules in the first place — no after-the-fact scrubbing needed.
    private static MethodInfo _loadAssembliesMethod = default!;      // IReflectionManager.LoadAssemblies(IEnumerable<Assembly>)
    private static FieldInfo _modLoaderReflectionManagerField = default!; // BaseModLoader.ReflectionManager
    private static FieldInfo _modLoaderDependenciesField = default!;      // BaseModLoader._dependencies
    private static FieldInfo _modLoaderTestingCallbacksField = default!;  // BaseModLoader._testingCallbacks

    // GameShared members, resolved once (the engine type isn't referenceable at compile time).
    private static MethodInfo _gsPreInit = default!;
    private static MethodInfo _gsInit = default!;
    private static MethodInfo _gsPostInit = default!;
    private static MethodInfo _gsUpdate = default!;        // Update(ModUpdateLevel, FrameEventArgs)
    private static MethodInfo _gsShutdown = default!;
    private static MethodInfo _gsDispose = default!;
    private static MethodInfo _gsSetTestingCallbacks = default!;
    private static PropertyInfo _gsDependenciesProperty = default!;

    /// <summary>
    ///     This is also used to identify SanabiLauncher IContentRoots.
    /// </summary>
    private static object _universalLoaderSawmill = default!;
    private static object _resPathRootValue = default!;

    public const string ResourcesFolderName = "Resources";

    [PatchEntry(PatchRunLevel.Engine)]
    private static void Start()
    {
        if (!SanabiConfig.ProcessConfig.LoadExternalMods)
            return;

        _gameSharedType = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.GameShared", except: true);
        var internalModLoader = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.ModLoader", except: true);
        var baseModLoader = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.BaseModLoader", except: true);

        // Engine bits we read off the live ModLoader instance to replicate InitMod's load-bearing effects.
        _loadAssembliesMethod = AccessTools.Method(
            "Robust.Shared.Reflection.IReflectionManager:LoadAssemblies",
            [typeof(IEnumerable<Assembly>)]);
        _modLoaderReflectionManagerField = AccessTools.Field(baseModLoader, "ReflectionManager");
        _modLoaderDependenciesField = AccessTools.Field(baseModLoader, "_dependencies");
        _modLoaderTestingCallbacksField = AccessTools.Field(baseModLoader, "_testingCallbacks");

        // GameShared lifecycle members (PreInit/Init/PostInit/Update/Shutdown/Dispose + Dependencies/callbacks).
        _gsPreInit = PatchHelpers.GetMethod(_gameSharedType, "PreInit", except: true)!;
        _gsInit = PatchHelpers.GetMethod(_gameSharedType, "Init", except: true)!;
        _gsPostInit = PatchHelpers.GetMethod(_gameSharedType, "PostInit", except: true)!;
        _gsUpdate = PatchHelpers.GetMethod(_gameSharedType, "Update", except: true)!;
        _gsShutdown = PatchHelpers.GetMethod(_gameSharedType, "Shutdown", except: true)!;
        _gsDispose = PatchHelpers.GetMethod(_gameSharedType, "Dispose", [], except: true)!;
        _gsSetTestingCallbacks = PatchHelpers.GetMethod(_gameSharedType, "SetTestingCallbacks", except: true)!;
        _gsDependenciesProperty = _gameSharedType.GetProperty("Dependencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Couldn't resolve GameShared.Dependencies!");

        _iResourceManagerAddRootsMethod = AccessTools.Method("Robust.Shared.ContentPack.IResourceManager:AddRoot");

        var sawmillImplType = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.Log.LogManager+Sawmill", except: true);

        // DEMENTED
        // `public Sawmill(Sawmill? parent, string name)`, although sawmill impl type is nullable, its notnullable here
        _universalLoaderSawmill = PatchHelpers.GetConstructorAndMakeInstance(sawmillImplType, [sawmillImplType, typeof(string)], [null, new Guid().ToString()]);

        _resPathRootValue = ResPathFactory.Construct("/");

        var sawmillInterfaceType = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.Log.ISawmill", except: true)!;

        // (DirectoryInfo directory, ISawmill sawmill, bool checkCasing)
        _dirLoaderConstructorData = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.ResourceManager+DirLoader", except: true)
            .GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, [typeof(DirectoryInfo), sawmillInterfaceType, typeof(bool)])
                ?? throw new InvalidOperationException("Couldn't resolve DirLoader constructor!");

        PatchHelpers.PatchMethod(
            ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ProgramShared", except: true),
            "DoMounts",
            DoMountsPrefix,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            PatchHelpers.GetMethod(internalModLoader, "TryLoadModules"),
            ModLoaderPostfix,
            HarmonyPatchType.Postfix
        );

        // Drive our detached GameShared entry points through the engine's own run-level broadcasts, so they get
        // engine-identical PreInit/Init/PostInit/Update/Shutdown timing without ever being in the engine Mods list.
        // (These are declared + concrete on BaseModLoader.)
        PatchHelpers.PatchMethod(baseModLoader, "BroadcastRunLevel", BroadcastRunLevelPostfix, HarmonyPatchType.Postfix);
        PatchHelpers.PatchMethod(baseModLoader, "BroadcastUpdate", BroadcastUpdatePostfix, HarmonyPatchType.Postfix);
        PatchHelpers.PatchMethod(baseModLoader, "Shutdown", ShutdownPostfix, HarmonyPatchType.Postfix);

        PatchHelpers.PatchMethod(
            ReflectionManager.GetTypeByQualifiedName("Robust.Shared.GameObjects.EntitySystemManager", except: true),
            "Initialize",
            EntSysManInitPostfix,
            HarmonyPatchType.Postfix
        );

        var ensureGetAllTypesCacheMethod = PatchHelpers.GetMethod(
            ReflectionManager.GetTypeByQualifiedName("Robust.Shared.Reflection.ReflectionManager", except: true),
            "EnsureGetAllTypesCache",
            except: true
        )!;
        // How about we NOT kill our fucking dude
        AssemblyHidingManager.EngineOverridenCallsites.Add(ensureGetAllTypesCacheMethod);

        if (!TryGetExternalMods(out var modules))
            return;

        TotalExternalModCount = modules.Count;

        var index = 0;
        foreach (var module in modules)
        {
            SanabiLogger.LogInfo($"ASMLOAD: Considering to load mod `{module.Name}`");
            if (!GetIsModEnabled(SanabiConfig.ProcessConfig.LoadedExternalModsFlags, index++))
                continue;

            SanabiLogger.LogInfo($"ASMLOAD: Loading mod `{module.Name}`");
            module.Initialise();

            _dataPendingAssemblyLoad.Enqueue(module);

            if (module is ILoadedModAndResourceData modAndResourceDataData)
            {
                var resourcesPath = modAndResourceDataData.GetResourcesPath();
                if (!Directory.Exists(resourcesPath))
                    throw new FileNotFoundException($"Couldn't find resources folder at `{resourcesPath}`! Mod name: `{module.Name}`");

                var dirLoader = _dirLoaderConstructorData.Invoke([new DirectoryInfo(resourcesPath), _universalLoaderSawmill, false]);
                _loadersPendingMount.Enqueue(dirLoader);

                SanabiLogger.LogInfo($"Loaded and mounted mod `{module.Name}`, resources at `{resourcesPath}`");
            }
        }
    }

    private static void DoMountsPrefix(ref object res /* resource manager */) // cant use `dynamic`; have to call IResourceManagerInternal's (yes the interface) AddRoot method
    {
        while (_loadersPendingMount.TryDequeue(out var loader))
        {
            _iResourceManagerAddRootsMethod.Invoke(res, [_resPathRootValue, loader]);
        }
    }

    public static bool TryGetExternalMods([MaybeNullWhen(false)] out List<ILoadedModData> externalMods)
    {
        var modPaths = Directory.GetFileSystemEntries(LauncherPaths.SanabiModsPath, "*", SearchOption.TopDirectoryOnly);

        if (modPaths.Length == 0)
        {
            externalMods = null;
            return false;
        }

        // setup array
        externalMods = new(modPaths.Length);

        // wtf why would you ever need more than 64 mods
        if (modPaths.Length > 64)
        {
            Array.Resize(ref modPaths, 64);
            SanabiLogger.LogError("Only the first 64 mods will be loaded!");
        }

        foreach (var modPath in modPaths)
        {
            if (Directory.Exists(modPath)) // Path points to dir
            {
                string? dllPath = null;
                string? resourcesPath = null;

                foreach (var modSubPath in Directory.GetFileSystemEntries(modPath, "*", SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetExtension(modSubPath) == ".dll")
                        dllPath = modSubPath;
                    else if (Directory.Exists(modSubPath) && new DirectoryInfo(modSubPath).Name == ResourcesFolderName)
                        resourcesPath = modSubPath;

                    if (dllPath != null && resourcesPath != null)
                        break;
                }

                // only resourcesPath is necessary
                if (resourcesPath == null)
                {
                    SanabiLogger.LogError($"Couldn't resolve resourcesPath `{resourcesPath ?? "N/A"}` on a folder! Path: {modPath}");
                    continue;
                }

                externalMods.Add(new LoadedFolderData(new DirectoryInfo(modPath).Name, modPath));
                continue;
            }
            else if (File.Exists(modPath)) // Path points to file
            {
                var modPathExtension = Path.GetExtension(modPath);
                if (modPathExtension == ".dll")
                    externalMods.Add(new LoadedDllData(new DirectoryInfo(modPath).Name, modPath));
                else if (modPathExtension == ".zip")
                    externalMods.Add(new LoadedPackData(new DirectoryInfo(modPath).Name, modPath));
                else
                    SanabiLogger.LogError($"Tried to load mod file, but wasn't a `.dll`! Path: {modPath}");

                continue;
            }

            SanabiLogger.LogError($"Mod path was not recognised as anything meaningful! Path: {modPath}");
        }

        return true;
    }
}

/// <summary>
///     Represents a loaded assembly, optionally with extra loaded resources.
///         The assembly `.dll` will share the name of the parent, if applicable.
/// </summary>
public interface ILoadedModData
{
    /// <summary>
    ///     Name of this folder/file, has file-extension if applicable.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    ///     Path to this folder/dll/pack. Uses system-format paths. Has file-extension if applicable.
    /// </summary>
    public string DataPath { get; set; }

    /// <summary>
    ///     Mod assembly.
    /// </summary>
    public Assembly? Assembly { get; set; }

    /// <summary>
    ///     Method called on the data once, ever, before it is used.
    /// </summary>
    public void Initialise();
}

/// <summary>
///     For standalone .DLLs being loaded.
///         <see cref="ModPath"/> would be path to the `.dll`.
/// </summary>
public sealed class LoadedDllData(string name, string dataPath) : ILoadedModData
{
    public string Name { get; set; } = name;

    public string DataPath { get; set; } = dataPath;

    /// <summary>
    ///     Mod assembly. For purely DLLmods, will never be null.
    /// </summary>
    public Assembly? Assembly { get; set; } = null;

    public void Initialise()
    {
        Assembly = Assembly.LoadFrom(DataPath);
    }
}

/// <summary>
///     Represents a loaded assembly, with mounted resources.
///         The assembly `.dll` will share the name of the parent, if applicable.
/// </summary>
public interface ILoadedModAndResourceData : ILoadedModData
{
    /// <returns>Path to resources folder that will be loaded.</returns>
    public string GetResourcesPath();
}

/// <summary>
///     For mods that are folders with:
///     - loaded resources
///     - OPTIONALLY, a loaded `.dll`.
///
///     <see cref="ModPath"/> would be path to the folder containing
///         resources folder and the `.dll` (if it's there).
/// </summary>
public class LoadedFolderData(string name, string dataPath) : ILoadedModAndResourceData
{
    public string Name { get; set; } = name;

    public string DataPath { get; set; } = dataPath;

    /// <summary>
    ///     Mod assembly. For resourcemods, may be null.
    /// </summary>
    public Assembly? Assembly { get; set; } = null;

    public virtual string GetResourcesPath() => Path.Join(DataPath, AssemblyLoadingManager.ResourcesFolderName);

    public virtual void Initialise()
    {
        var dllPath = Path.ChangeExtension(Path.Join(DataPath, Name), "dll");
        if (!File.Exists(dllPath))
            return;

        Assembly = Assembly.LoadFrom(dllPath);
    }
}

/// <summary>
///     Basically <see cref="LoadedFolderData"/> but contained in a zip file.
///         These are uniquely handled by the game thanks to Sanabi.
///
///     <see cref="ModPath"/> would be path to the zip file containing
///         resources folder and the `.dll` (if it's there).
/// </summary>
public class LoadedPackData(string name, string dataPath) : ILoadedModAndResourceData
{
    /// <summary>
    ///     SHA256 of the `.zip` file being extracted.
    /// </summary>
    public const string ZipFolderChecksumFile = ".sanabi_checksum";

    /// <summary>
    ///     Path to folder of the extracted zip.
    ///         Nothing may exist here when initially made,
    ///         but something will be here upon initialisation.
    /// </summary>
    public string ExtractedZipPath = Path.Combine(LauncherPaths.SanabiExtractedModZipsPath, name);

    public string Name { get; set; } = name;

    public string DataPath { get; set; } = dataPath;

    /// <summary>
    ///     Mod assembly. For resourcemods, may be null.
    /// </summary>
    public Assembly? Assembly { get; set; } = null;

    public string GetResourcesPath() => Path.Combine(ExtractedZipPath, AssemblyLoadingManager.ResourcesFolderName);

    private void UpdateExtractTo()
    {
        // Delete if something already exists there
        if (Directory.Exists(ExtractedZipPath))
        {
            SanabiLogger.LogInfo($"[ZIPLOADING] Deleting already-existing cache at `{ExtractedZipPath}`");
            Directory.Delete(ExtractedZipPath, true);
        }

        // This takes even more of a while
        SanabiLogger.LogInfo($"[ZIPLOADING] Extracting zip of name `{Name}` to `{ExtractedZipPath}`");
        var sw = Stopwatch.StartNew();

        var safeExtractor = new SafeZipExtractor();
        _ = safeExtractor.ExtractSafelyAsync(DataPath, ExtractedZipPath);

        SanabiLogger.LogInfo($"[ZIPLOADING] Extracted zip of name `{Name}` to `{ExtractedZipPath}`. Elapsed time: {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    ///     Checksum provided should be in invariant lowercase.
    ///     This adds the checksum file, or updates it if it already exists,
    ///         in the specified directory.
    /// </summary>
    private void WriteChecksumIn(string checksum)
    {
        File.WriteAllText(Path.Combine(ExtractedZipPath, ZipFolderChecksumFile), checksum);
    }

    /// <summary>
    ///     Basically we uniquely identify a .zip with SHA256 and get a checksum;
    ///     Then we extract+cache this if its not already cached or overwrite existing,
    ///         if checksum doesnt match (indicating we have a different version).
    ///
    ///     If checksums are the same nothing happens and everything is read the same.
    /// </summary>
    public void HandleModZip(string currentChecksum)
    {
        // Check if already-extracted zip path exists
        if (Directory.Exists(ExtractedZipPath))
        {
            // Zip cache exists

            // Find version of the current extracted zip
            var originalChecksum = File.ReadAllText(Path.Combine(ExtractedZipPath, ZipFolderChecksumFile)).Trim();
            SanabiLogger.LogInfo($"[ZIPLOADING] Already-existing cache found; original checksum: {originalChecksum}, current checksum: {currentChecksum}; matching: {originalChecksum == currentChecksum}");

            // Version doesn't match the zip we're trying to load
            if (currentChecksum != originalChecksum)
            {
                // Delete existing if necessary, and add checksum
                UpdateExtractTo();
                WriteChecksumIn(currentChecksum);

                return;
            }
        }
        else // Cache doesn't exist
        {
            UpdateExtractTo();
            WriteChecksumIn(currentChecksum);

            SanabiLogger.LogInfo($"[ZIPLOADING] Added new extracted folder at `{ExtractedZipPath}`, checksum `{currentChecksum}`");

            return;
        }

        // At this point: we are confirmed to have something extracted at `cachedExtractedModZipPath`, with a checksum stored inside matching `currentChecksum`
        SanabiLogger.LogInfo($"[ZIPLOADING] Read already-existing extracted folder at `{ExtractedZipPath}`, checksum `{currentChecksum}`");
    }

    public void Initialise()
    {
        // Checksum of our `.zip`
        var currentChecksum = "";

        using (var stream = File.OpenRead(DataPath))
        using (var sha256 = SHA256.Create())
        {
            // This takes a while
            var currentHash = sha256.ComputeHash(stream);
            currentChecksum = Convert.ToHexString(currentHash).ToLowerInvariant();
        }

        // Handle zip
        HandleModZip(currentChecksum);

        // Handle assembly
        var dllPath = Path.ChangeExtension(Path.Join(ExtractedZipPath, Name), "dll");
        if (File.Exists(dllPath))
            Assembly = Assembly.LoadFrom(dllPath);
        else // For zips the dll can be named anything ever
        {
            var possibleFiles = Directory.EnumerateFiles(ExtractedZipPath, "*.dll");
            foreach (var possibleDllPath in possibleFiles)
            {
                Assembly = Assembly.LoadFrom(possibleDllPath);
                break;
            }
        }
    }
}
