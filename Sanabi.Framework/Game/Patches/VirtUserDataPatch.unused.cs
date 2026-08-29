// using Sanabi.Framework.Game.Managers;
// using Sanabi.Framework.Data;
// using Sanabi.Framework.Patching;
// using HarmonyLib;
// using System.Reflection;
// using Sanabi.Framework.Game.Utility;

// namespace Sanabi.Framework.Game.Patches;

// /// <summary>
// ///     Basically replaces the UserData with a VirtualWritableDirProvider, that gets injected with
// ///         the current contents of the real user data directory that would've been read.
// /// </summary>
// public static class UserDataPatch
// {
//     private static readonly HashSet<MemoryStream> MemoryStreamsWeInstantiated = [];
//     public static bool Enabled => SanabiConfig.ProcessConfig.RunUserDataPatch;

//     private static readonly Type VwdpStreamType = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.VirtualWritableDirProvider+VirtualFileStream", except: true);
//     private static readonly FieldInfo VwdpStreamSource = VwdpStreamType.GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!;

//     private static readonly Type VwdpType = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.VirtualWritableDirProvider", except: true);

//     /// <summary>public void CreateDir(ResPath path)</summary>
//     private static readonly MethodInfo VwdpCreateDir = PatchHelpers.GetMethod(VwdpType, "CreateDir", except: true)!;
//     /// <summary>public Stream Open(ResPath path, FileMode fileMode, FileAccess access, FileShare share)</summary>
//     private static readonly MethodInfo VwdpOpen = PatchHelpers.GetMethod(VwdpType, "Open", except: true)!;

//     private static PropertyInfo ResManUserData = null!;

//     [PatchEntry(PatchRunLevel.Engine)]
//     public static void Patch()
//     {
//         if (!Enabled)
//             return;

//         var resMan = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.ResourceManager", except: true);
//         ResManUserData = resMan.GetProperty("UserData")!;

//         var resourceManagerInitialize = PatchHelpers.ResolveMethod(
//             resMan, "Initialize");

//         PatchHelpers.PatchMethod(
//             resourceManagerInitialize,
//             ResManInitializePostfix,
//             HarmonyPatchType.Postfix
//         );

//         // VWDP Stream fixing because its position is totally wrong o algo

//         PatchHelpers.PatchMethod(
//             AccessTools.PropertyGetter(VwdpStreamType, "Length"),
//             VwdpStreamLengthPrefix,
//             HarmonyPatchType.Prefix
//         );

//         // Todo fix everything below breaking kinda

//         PatchHelpers.PatchMethod(
//             AccessTools.PropertyGetter(VwdpStreamType, "Position"),
//             VwdpStreamPositionPrefix,
//             HarmonyPatchType.Prefix
//         );

//         // public override int Read(byte[] buffer, int offset, int count)
//         PatchHelpers.PatchMethod(
//             VwdpStreamType,
//             "Read",
//             VwdpStreamAnyOperationPostfix,
//             HarmonyPatchType.Postfix,
//             [typeof(byte[]), typeof(int), typeof(int)]
//         );

//         // public override void Write(byte[] buffer, int offset, int count)
//         PatchHelpers.PatchMethod(
//             VwdpStreamType,
//             "Write",
//             VwdpStreamAnyOperationPostfix,
//             HarmonyPatchType.Postfix,
//             [typeof(byte[]), typeof(int), typeof(int)]
//         );
//     }

//     // Originally: `public override long Length => _source.Position;`
//     // New: `public override long Length => _source.Length;`
//     /*
//         BECAUSE ITS.. TRULY GENIUS
//                 ITS TRULY PEAK
//     */
//     private static bool VwdpStreamLengthPrefix(ref object __instance, ref long __result)
//     {
//         var _source = (MemoryStream)VwdpStreamSource.GetValue(__instance)!;
//         // Keep original behaviour if this isn't our virtual filestream (came from elsewhere), to avoid detection
//         if (!MemoryStreamsWeInstantiated.Contains(_source))
//             return true;

//         __result = _source.Length;
//         return false;
//     }

//     private static bool VwdpStreamPositionPrefix(ref object __instance, ref long __result)
//     {
//         var _source = (MemoryStream)VwdpStreamSource.GetValue(__instance)!;
//         // Keep original behaviour if this isn't our virtual filestream (came from elsewhere), to avoid detection
//         if (!MemoryStreamsWeInstantiated.Contains(_source))
//             return true;

//         __result = _source.Position;
//         return false;
//     }

//     private static void VwdpStreamAnyOperationPostfix(ref object __instance)
//     {
//         var _source = (MemoryStream)VwdpStreamSource.GetValue(__instance)!;
//         // Keep original behaviour if this isn't our virtual filestream (came from elsewhere), to avoid detection
//         if (!MemoryStreamsWeInstantiated.Contains(_source))
//             return;

//         // AAAAAGHHH
//         _source.Position = 0;
//     }

//     private static void ResManInitializePostfix(ref dynamic __instance, ref string? userData /* param */)
//     {
//         // if userdata is already null for irrelevant reasons, let it do its own thing
//         if (userData == null)
//             return;

//         // Don do nuffin
//         var userDataDirectory = userData;
//         if (!Directory.Exists(userDataDirectory))
//             return;

//         userData = null;

//         var vwdp = Activator.CreateInstance(VwdpType)!;
//         LoadUserDataInto(vwdp, userDataDirectory);

//         ResManUserData.SetValue(__instance, vwdp);
//     }

//     private static void LoadUserDataInto(dynamic vwdp, string loadPath)
//     {
//         foreach (var realPath in Directory.EnumerateFileSystemEntries(loadPath))
//         {
//             // Root it
//             // Also just use '/', as respath only accepts that
//             var virtualPathString = Path.Combine("/", Path.GetRelativePath(loadPath, realPath));
//             virtualPathString = virtualPathString.Replace(Path.DirectorySeparatorChar, '/');

//             if (Directory.Exists(realPath))
//                 VwdpCreateDir.Invoke(vwdp, (object[])[ResPathFactory.Construct(virtualPathString)]);
//             else if (File.Exists(realPath))
//             {
//                 /*
//                     So VirtualFileStream is actually broken
//                         Specifically, VirtualFileStream.Write fucks up the Position of the backing
//                         FileNode's MemoryStream. Yay!
//                 */

//                 using var realStream = File.OpenRead(realPath);
//                 var virtualStream = VwdpProxy.CreateNewUnsafe(ResPathFactory.Construct(virtualPathString), vwdp);
//                 MemoryStreamsWeInstantiated.Add(virtualStream);

//                 // ACTUALLY PRESERVE the position
//                 realStream.CopyTo(virtualStream);
//                 virtualStream.Position = 0; // who cares about old position
//             }
//         }
//     }
// }
