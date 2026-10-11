using System.Reflection;
using System.Runtime.InteropServices;

namespace Sanabi.Framework.Misc.Imports;

/// <summary>
///     This bs is done so that we can use SDL independent of the version its on.
///         Obviously dont touch any signatures here unless you REALLY KNOW WHAT YOU'RE DOING!
/// </summary>
internal static partial class SDL
{
    /// <summary>
    ///     Should be called when you think (are guessing) that SDL3 is loaded in the process.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when SDL3 isn't present.</exception>
    internal static void BindSdl()
    {
        var handle = GetSdlHandle();

        SDL_GetDisplayBounds =
            Marshal.GetDelegateForFunctionPointer<SDL_GetDisplayBoundsDelegate>(
                NativeLibrary.GetExport(handle, "SDL_GetDisplayBounds"));

        SDL_GetDisplayForWindow =
            Marshal.GetDelegateForFunctionPointer<SDL_GetDisplayForWindowDelegate>(
                NativeLibrary.GetExport(handle, "SDL_GetDisplayForWindow"));

        SDL_GetPrimaryDisplay =
            Marshal.GetDelegateForFunctionPointer<SDL_GetPrimaryDisplayDelegate>(
                NativeLibrary.GetExport(handle, "SDL_GetPrimaryDisplay"));

        SDL_SetWindowBordered =
            Marshal.GetDelegateForFunctionPointer<SDL_SetWindowBorderedDelegate>(
                NativeLibrary.GetExport(handle, "SDL_SetWindowBordered"));
    }

    internal static nint GetSdlHandle()
    {
        string[] names;
        if (OperatingSystem.IsWindows())
            names = ["SDL3.dll"];
        else if (OperatingSystem.IsLinux())
            names = ["libSDL3.so.0", "libSDL3.so"];
        else if (OperatingSystem.IsMacOS())
            names = ["libSDL3.0.dylib", "libSDL3.dylib"];
        else
            throw new PlatformNotSupportedException("Your platform is too inferior for me to consider even trying to make it work here");

        // Returns the handle of the already loaded library if it's loaded
        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
                return handle;
        }

        throw new InvalidOperationException($"Tried to get handle for SDL3 as a loaded module, but could not do so! Tried: {string.Join(", ", names)}");
    }

    // bool SDL_GetDisplayBounds(SDL_DisplayID displayID, SDL_Rect *rect)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate SDLBool SDL_GetDisplayBoundsDelegate(
        uint displayId,
        out SDL_Rect rect);
    public static SDL_GetDisplayBoundsDelegate SDL_GetDisplayBounds = null!;

    // SDL_DisplayID SDL_GetDisplayForWindow(SDL_Window *window). 0 on failure
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint SDL_GetDisplayForWindowDelegate(
        nint window);
    public static SDL_GetDisplayForWindowDelegate SDL_GetDisplayForWindow = null!;

    // SDL_DisplayID SDL_GetPrimaryDisplay(void). 0 on failure
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint SDL_GetPrimaryDisplayDelegate();
    public static SDL_GetPrimaryDisplayDelegate SDL_GetPrimaryDisplay = null!;

    // bool SDL_SetWindowBordered(SDL_Window *window, bool bordered)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate SDLBool SDL_SetWindowBorderedDelegate(
        nint window,
        SDLBool bordered);
    public static SDL_SetWindowBorderedDelegate SDL_SetWindowBordered = null!;

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_Rect
    {
        public int x;
        public int y;
        public int w;
        public int h;
    }

    public readonly record struct SDLBool
    {
        private readonly byte value;

        internal const byte FALSE_VALUE = 0;
        internal const byte TRUE_VALUE = 1;

        internal SDLBool(byte value)
        {
            this.value = value;
        }

        public static implicit operator bool(SDLBool b)
        {
            return b.value != FALSE_VALUE;
        }

        public static implicit operator SDLBool(bool b)
        {
            return new SDLBool(b ? TRUE_VALUE : FALSE_VALUE);
        }

        public bool Equals(SDLBool other)
        {
            return other.value == value;
        }

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }
    }
}

/// <summary>
///     Used to
/// </summary>
internal static class SdlModuleTracker
{
    private static IntPtr _sdlHandle;
    private static readonly object _lock = new();

    public static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(SdlModuleTracker).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (IsSdl(libraryName))
        {
            lock (_lock)
            {
                if (_sdlHandle == IntPtr.Zero)
                {
                    // This loads OR attaches to existing load depending on platform
                    NativeLibrary.TryLoad(libraryName, assembly, searchPath, out _sdlHandle);
                }
            }

            return _sdlHandle;
        }

        return IntPtr.Zero;
    }

    private static bool IsSdl(string name) =>
        name.Contains("SDL3", StringComparison.OrdinalIgnoreCase);
}
