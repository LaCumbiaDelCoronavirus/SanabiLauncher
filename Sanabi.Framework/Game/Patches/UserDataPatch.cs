using Sanabi.Framework.Game.Managers;
using Sanabi.Framework.Data;
using Sanabi.Framework.Patching;
using HarmonyLib;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Sanabi.Framework.Misc;

namespace Sanabi.Framework.Game.Patches;

public static class UserDataPatch
{
    /// <summary>
    ///     Dir providers which should use the proxy vfs instead of original code.
    /// </summary>
    private static readonly Dictionary<object, ProxyVfs> StealthedDirProviders = [];

    public static bool Enabled => SanabiConfig.ProcessConfig.RunUserDataPatch;

    private static readonly Type WdpType = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.WritableDirProvider", except: true);
    private static readonly MethodInfo WdpGetFullPath = PatchHelpers.GetMethod(WdpType, "GetFullPath")!;

    private static PropertyInfo ResManUserData = null!;

    [PatchEntry(PatchRunLevel.Engine)]
    public static void Patch()
    {
        if (!Enabled)
            return;

        var resMan = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.ResourceManager", except: true);
        ResManUserData = resMan.GetProperty("UserData")!;

        var resourceManagerInitialize = PatchHelpers.ResolveMethod(
            resMan, "Initialize");

        PatchHelpers.PatchMethod(
            resourceManagerInitialize,
            ResManInitializePostfix,
            HarmonyPatchType.Postfix
        );

        // Wdp

        PatchHelpers.PatchMethod(
            WdpType,
            "CreateDir",
            WdpPrefixCreateDir,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "Delete",
            WdpPrefixDelete,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "Exists",
            WdpPrefixExists,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "Find",
            WdpPrefixFind,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "DirectoryEntries",
            WdpPrefixDirectoryEntries,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "IsDir",
            WdpPrefixIsDir,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "Open",
            WdpPrefixOpen,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "OpenSubdirectory",
            WdpPrefixOpenSubdirectory,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WdpType,
            "Rename",
            WdpPrefixRename,
            HarmonyPatchType.Prefix
        );
    }

    private static void ResManInitializePostfix(ref dynamic __instance, ref string? userData /* param */)
    {
        // if userdata is already null for irrelevant reasons, let it do its own thing
        if (userData == null)
            return;

        // Don do nuffin
        var userDataDirectory = userData;
        if (!Directory.Exists(userDataDirectory))
            return;

        userData = null;

        // WritableDirProvider(DirectoryInfo rootDir, bool hideRootDir)
        var wdp = Activator.CreateInstance(WdpType, [new DirectoryInfo(userDataDirectory), true])!;
        var proxyVfs = new ProxyVfs(userDataDirectory);
        proxyVfs.PopulateFromRoot();
        StealthedDirProviders[wdp] = proxyVfs;

        ResManUserData.SetValue(__instance, wdp);
    }

    private static string GetFullPath(dynamic wdp, object resPath)
        => WdpGetFullPath.Invoke(wdp, (object[])[resPath]);

    // public void Delete(ResPath path)
    private static bool WdpPrefixCreateDir(ref object __instance, ref object path)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        var diskPath = GetFullPath(__instance, path);
        proxyVfs.SetDir(diskPath, new ProxyVfs.DirectoryNode([]));
        return false;
    }

    //
    private static bool WdpPrefixDelete(ref object __instance, ref object path)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        var diskPath = GetFullPath(__instance, path);
        proxyVfs.DeleteAt(diskPath);
        return false;
    }

    // public bool Exists(ResPath path)
    private static bool WdpPrefixExists(ref object __instance, ref bool __result, ref object path)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        var diskPath = GetFullPath(__instance, path);
        __result = proxyVfs.Exists(diskPath);
        return false;
    }

    // public (IEnumerable<ResPath> files, IEnumerable<ResPath> directories) Find(string pattern, bool recursive = true)
    private static bool WdpPrefixFind(ref object __instance, ref string pattern, ref bool recursive)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        if (pattern.Contains(".."))
            throw new InvalidOperationException($"Pattern may not contain '..'. Pattern: {pattern}."); // [sic]

        throw new NotImplementedException("Find is not implemented");
    }

    private static bool WdpPrefixDirectoryEntries(ref object __instance, ref IEnumerable<string> __result, ref object path)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        // TODO LCDC SC_VULN: original code does `yield break`, `yield return` - this method deviates in behaviour
        var diskPath = GetFullPath(__instance, path);
        if (!proxyVfs.Exists(diskPath))
        {
            __result = [];
            return false;
        }

        __result = proxyVfs.EnumerateAllEntries(diskPath);
        return false;
    }

    // public bool IsDir(ResPath path)
    private static bool WdpPrefixIsDir(ref object __instance, ref bool __result, ref object path)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        var diskPath = GetFullPath(__instance, path);
        __result = proxyVfs.TryFindNodeAt(diskPath, out var node) && node is ProxyVfs.DirectoryNode;
        return false;
    }

    // public Stream Open(ResPath path, FileMode fileMode, FileAccess access, FileShare share)
    private static bool WdpPrefixOpen(ref object __instance, ref Stream __result, ref object path, ref FileMode fileMode, ref FileAccess access, ref FileShare share)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        var diskPath = GetFullPath(__instance, path);
        if (!proxyVfs.TryFindNodeAtDisk(diskPath, out var node) ||
            node is not ProxyVfs.FileNode fileNode)
        {
            // We are making a new file
            var parentDirectory = proxyVfs.FindParentOf(diskPath);
            var memoryFileNode = new ProxyVfs.MemoryFileNode(new MemoryStream());
            parentDirectory.Entries[Path.GetFileName(diskPath)] = memoryFileNode;

            var tempStream = new ProxyVfs.ProxyMemoryFileStream(memoryFileNode.Stream, true, access.HasFlag(FileAccess.Read));
            __result = tempStream;

            return false;
        }

        // We are editing an existing file, so throw
        if (fileMode == FileMode.CreateNew)
            throw new IOException(); // TODO LCDC SC_VULN: needs original exception message

        switch (fileNode)
        {
            case ProxyVfs.DiskFileNode:
                if (access.HasFlag(FileAccess.Write))
                {
                    // As there is a potential write operation here,
                    // the disk node must now be collapsed into a memory node.

                    SanabiLogger.LogInfo($"PROXYVFS: Collapsed this node into a memory node {diskPath}");

                    using var fileStream = File.Open(diskPath, fileMode, access, share);
                    var memoryStream = new MemoryStream();
                    fileStream.CopyTo(memoryStream);

                    ref var nodeRef = ref proxyVfs.GetNodeRefOrAddDefault(diskPath);
                    nodeRef = new ProxyVfs.MemoryFileNode(memoryStream);

                    var tempStream = new ProxyVfs.ProxyMemoryFileStream(((ProxyVfs.MemoryFileNode)nodeRef).Stream, true, access.HasFlag(FileAccess.Read));
                    __result = tempStream;
                }
                else
                    __result = File.Open(diskPath, fileMode, access, share);

                break;
            case ProxyVfs.MemoryFileNode memoryFileNode:
                var tempMemStream = new ProxyVfs.ProxyMemoryFileStream(memoryFileNode.Stream, access.HasFlag(FileAccess.Write), access.HasFlag(FileAccess.Read));
                __result = tempMemStream;
                break;
            default:
                break;
        }

        return false;
    }

    // public IWritableDirProvider OpenSubdirectory(ResPath path)
    private static bool WdpPrefixOpenSubdirectory(ref object __instance, ref object __result, ref object path)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        var diskPath = GetFullPath(__instance, path);
        if (!proxyVfs.TryFindNodeAt(diskPath, out var node) ||
            node is not ProxyVfs.DirectoryNode directoryNode)
            throw new IOException();

        // Ah shit, here we go again.

        // This should just point to a subset of the same filesystem, instead of naively making a new VFS and populating it with totally new nodes
        var enumeratedNodes = new List<ProxyVfs.INode>();
        ProxyVfs.AddAllEnumeratedNodes(directoryNode, enumeratedNodes);
        var innerProxyVfs = new ProxyVfs(diskPath)
        {
            RootNode = directoryNode
        };

        var wdp = Activator.CreateInstance(WdpType, [new DirectoryInfo(diskPath), true])!;
        StealthedDirProviders[wdp] = innerProxyVfs;

        __result = wdp;
        return false;
    }

    private static bool WdpPrefixRename(ref object __instance, ref object oldPath, ref object newPath)
    {
        if (!StealthedDirProviders.TryGetValue(__instance, out var proxyVfs))
            return true;

        proxyVfs.Rename(GetFullPath(__instance, oldPath), GetFullPath(__instance, newPath));
        return false;
    }

    /// <summary>
    ///     Simple VFS that tries to increase performance by reading 'stored' files from disk,
    ///         and where mutated or newly added files get stored in memory.
    /// </summary>
    /// <param name="rootPath">On-disk path that is the root of the VFS.</param>
    private class ProxyVfs(string rootPath)
    {
        public bool TryFindNodeAtDisk(string path, [MaybeNullWhen(false)] out INode? node, DirectoryNode? searchedNode = null)
        {
            path = Path.GetRelativePath(RootPath, path);
            return TryFindNodeAt(path, out node, searchedNode: searchedNode);
        }

        /// <summary>
        ///     Takes path relative to root
        /// </summary>
        public bool TryFindNodeAt(string path, [MaybeNullWhen(false)] out INode? node, DirectoryNode? searchedNode = null)
        {
            if (path.StartsWith(".."))
                throw new AccessViolationException();

            searchedNode ??= RootNode;
            if (path == ".")
                goto returnSearched;

            var segments = path.Split(Path.DirectorySeparatorChar);
            if (segments.Length == 0)
                goto returnSearched;

            var firstSegment = segments[0];
            if (segments.Length > 1)
            {
                foreach (var (entryName, entryNode) in searchedNode.Entries)
                {
                    if (entryName != firstSegment)
                        continue;

                    // Remove the first segment and the separator
                    var subPath = path[firstSegment.Length..];
                    return TryFindNodeAt(subPath, out node, searchedNode: searchedNode);
                }
            }
            else // We are at the last segment; the filename.
            {
                foreach (var (entryName, entryNode) in searchedNode.Entries)
                {
                    if (entryName != firstSegment)
                        continue;

                    node = entryNode;
                    return true;
                }
            }

            node = null;
            return false;

        returnSearched:
            node = searchedNode;
            return true;
        }

        public List<string> EnumerateAllEntries(string diskPath)
        {
            if (!TryFindNodeAt(diskPath, out var node) ||
                node is not DirectoryNode directoryNode)
                throw new IOException(); // TODO LCDC SC_VULN: needs original exception message

            var list = new List<string>();
            AddAllEnumeratedEntries(diskPath, directoryNode, list);
            return list;
        }

        public static void AddAllEnumeratedEntries(string currentDiskPath, DirectoryNode currentNode, List<string> list)
        {
            foreach (var (entryName, entryNode) in currentNode.Entries)
            {
                var entryDiskPath = currentDiskPath + entryName;
                list.Add(entryDiskPath);

                if (entryNode is DirectoryNode entryDirectoryNode)
                    AddAllEnumeratedEntries(entryDiskPath, entryDirectoryNode, list);
            }
        }

        public static void AddAllEnumeratedNodes(DirectoryNode currentNode, List<INode> list)
        {
            foreach (var (_, entryNode) in currentNode.Entries)
            {
                if (entryNode is DirectoryNode entryDirectoryNode)
                    AddAllEnumeratedNodes(entryDirectoryNode, list);
            }
        }

        public DirectoryNode FindParentOf(string diskPath)
        {
            var parentDir = Directory.GetParent(diskPath)!.FullName;
            var relativeParentDir = Path.GetRelativePath(RootPath, parentDir);

            if (!TryFindNodeAt(relativeParentDir, out var parentNode) ||
                parentNode is not DirectoryNode parentDirNode)
                throw new IOException("Couldnt find parent of " + diskPath); // TODO LCDC SC_VULN: needs original exception message

            return parentDirNode;
        }

        public void PopulateFromRoot()
        {
            foreach (var entryDiskPath in Directory.EnumerateFileSystemEntries(RootPath))
            {
                if (Directory.Exists(entryDiskPath))
                    SetDir(entryDiskPath, new([]));
                else if (File.Exists(entryDiskPath))
                {
                    var diskFileNode = new DiskFileNode(entryDiskPath);
                    SetFile(entryDiskPath, diskFileNode);
                }
            }
        }

        public void SetDir(string diskPath, DirectoryNode directoryNode)
        {
            var parentDirNode = FindParentOf(diskPath);
            parentDirNode.Entries[Path.GetFileName(diskPath)] = directoryNode;
        }

        public void SetFile(string diskPath, FileNode fileNode)
        {
            var parentDirNode = FindParentOf(diskPath);
            parentDirNode.Entries[Path.GetFileName(diskPath)] = fileNode;
        }

        /// <returns>The removed node.</returns>
        public INode DeleteAt(string diskPath)
        {
            var parentDirNode = FindParentOf(diskPath);
            if (!parentDirNode.Entries.Remove(Path.GetFileName(diskPath), out var removedNode))
                throw new IOException(); // TODO LCDC SC_VULN: needs original exception message

            return removedNode;
        }

        public bool Exists(string diskPath)
        {
            var parentDirNode = FindParentOf(diskPath);
            return parentDirNode.Entries.ContainsKey(Path.GetFileName(diskPath));
        }

        public void Rename(string oldDiskPath, string newDiskPath)
        {
            // Just detach it
            var detachedNode = DeleteAt(oldDiskPath);

            var parentNewDirNode = FindParentOf(newDiskPath);
            parentNewDirNode.Entries[Path.GetFileName(newDiskPath)] = detachedNode;
        }

        public ref INode GetNodeRefOrAddDefault(string diskPath)
        {
            var parentDirNode = FindParentOf(diskPath);
            return ref CollectionsMarshal.GetValueRefOrAddDefault(parentDirNode.Entries, Path.GetFileName(diskPath), out _)!;
        }

        /// <summary>
        ///     Disk path that this originates from
        /// </summary>
        public string RootPath = rootPath;
        public DirectoryNode RootNode = new([]);

        public interface INode;

        public record class DirectoryNode(Dictionary<string, INode> Entries) : INode;

        public abstract record class FileNode : INode;

        /// <summary>
        ///     Now-mutated (in the VFS) files that have been transferred from disk to memory.
        ///         When reading these, they are read from memory.
        /// </summary>
        public record class MemoryFileNode(MemoryStream Stream) : FileNode;

        /// <summary>
        ///     Unmutated (in the VFS) files that are stored on disk, not in memory.
        ///         When reading these, they are read directly from disk.
        /// </summary>
        public record class DiskFileNode(string Path) : FileNode;

        public class ProxyMemoryFileStream(MemoryStream backer, bool canWrite, bool canRead) : Stream
        {
            private readonly MemoryStream _backer = backer;
            private readonly bool _canWrite = canWrite;
            private readonly bool _canRead = canRead;

            public override bool CanWrite => _canWrite;
            public override bool CanRead => _canRead;
            public override bool CanSeek => _canRead;
            public override long Length => _backer.Length;

            /// <summary>
            ///     Number of bytes added to current position
            ///         when it is accessed externally.
            /// </summary>
            private long _offset = 0;

            public override long Position
            {
                get => _backer.Position;
                set
                {
                    _backer.Position = value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!CanRead)
                    throw new IOException(); // TODO LCDC SC_VULN: needs original exception message

                var oldPosition = _backer.Position;

                _backer.Position += _offset;
                var bytes = _backer.Read(buffer, offset, count);

                _backer.Position = oldPosition;

                return bytes;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!CanWrite)
                    throw new IOException(); // TODO LCDC SC_VULN: needs original exception message

                var oldPosition = _backer.Position;

                _backer.Position += _offset;
                _backer.Write(buffer, offset, count);

                _backer.Position = oldPosition;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                if (!CanSeek)
                    throw new IOException(); // TODO LCDC SC_VULN: needs original exception message

                switch (origin)
                {
                    case SeekOrigin.Begin:
                        _offset = offset - _backer.Position;
                        break;
                    case SeekOrigin.Current:
                        _offset = offset;
                        break;
                    case SeekOrigin.End:
                        _offset = _backer.Length - offset;
                        break;
                }

                return _backer.Position + _offset;
            }

            public override void Flush() { }
            public override void SetLength(long value) => _backer.SetLength(value);
        }
    }
}
