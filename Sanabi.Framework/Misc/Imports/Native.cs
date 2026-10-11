using System.Runtime.InteropServices;

namespace Sanabi.Framework.Misc.Imports;

public static partial class NativeWin
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string lpModuleName);

    public static nint? GetModuleHandleOrNullIfZero(string lpModuleName)
    {
        var handle = GetModuleHandle(lpModuleName);
        if (handle == nint.Zero)
            return null;

        return handle;
    }
}
