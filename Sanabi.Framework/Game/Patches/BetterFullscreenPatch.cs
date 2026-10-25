using System.Diagnostics;
using Sanabi.Framework.Game.Managers;
using HarmonyLib;
using Sanabi.Framework.Patching;
using Sanabi.Framework.Data;
using System.Reflection;
using Sanabi.Framework.Misc;
using Sanabi.Framework.Misc.Imports;

namespace Sanabi.Framework.Game.Patches;

/// <summary>
///     Gives you borderless windowed on SDL3
/// </summary>
// Be careful...

// There is a small issue with this that i THINK it causes screen to flicker black when starting. But eh who cares
public static class BetterFullscreenPatch
{
    public static bool Enabled => SanabiConfig.ProcessConfig.RunBetterFullscreenPatch;
    private static dynamic _clyde = null!;
    private static dynamic _windowingImpl = null!;

    // Resolved lazily in Resolve(), so that engines without Sdl3WindowingImpl don't blow up the type initializer
    private static Type WindowingImplType = null!;
    private static MethodInfo SendCmdMethod = null!;
    private static Type _clydeType = null!;
    private static Type CmdWinSetWindowed = null!;

    private static FieldInfo _clyde_mainWindow = null!;
    private static FieldInfo _clyde_windowMode = null!;
    private static FieldInfo sdl3WindowRegSdl3Window = null!;
    private static FieldInfo windowRegWindowSize = null!;
    private static FieldInfo windowRegWindowPos = null!;
    private static FieldInfo windowRegPrevWindowSize = null!;
    private static FieldInfo windowRegPrevWindowPos = null!;

    private static FieldInfo _cmdWinWindow = null!;
    private static FieldInfo _cmdWinWidth = null!;
    private static FieldInfo _cmdWinHeight = null!;
    private static FieldInfo _cmdWinPosX = null!;
    private static FieldInfo _cmdWinPosY = null!;

    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>
    ///     Resolves all engine types and members we need. Returns false (and warns) if any is missing.
    /// </summary>
    [StackTraceHidden]
    private static bool Resolve()
    {
        try
        {
            if (!ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.Graphics.Clyde.Clyde+Sdl3WindowingImpl", out var windowingImplType) ||
                !ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.Graphics.Clyde.Clyde", out var clydeType) ||
                !ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.Graphics.Clyde.Clyde+Sdl3WindowingImpl+CmdWinSetWindowed", out var cmdWinSetWindowed) ||
                !ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.Graphics.Clyde.Clyde+Sdl3WindowingImpl+Sdl3WindowReg", out var sdl3WindowReg) ||
                !ReflectionManager.TryGetTypeByQualifiedName("Robust.Client.Graphics.Clyde.Clyde+WindowReg", out var windowRegType))
            {
                SanabiLogger.LogWarn("BetterFullscreenPatch: SDL3 windowing types not found in this engine, skipping patch.");
                return false;
            }

            var sendCmd = PatchHelpers.GetMethod(windowingImplType, "SendCmd");
            var mainWindow = clydeType.GetField("_mainWindow", BindingFlags.Instance | BindingFlags.NonPublic);
            var windowMode = clydeType.GetField("_windowMode", BindingFlags.Instance | BindingFlags.NonPublic);
            var sdl3Window = sdl3WindowReg.GetField("Sdl3Window", AnyInstance);
            var windowSize = windowRegType.GetField("WindowSize", AnyInstance);
            var windowPos = windowRegType.GetField("WindowPos", AnyInstance);
            var prevWindowSize = windowRegType.GetField("PrevWindowSize", AnyInstance);
            var prevWindowPos = windowRegType.GetField("PrevWindowPos", AnyInstance);
            var cmdWindow = cmdWinSetWindowed.GetField("Window", AnyInstance);
            var cmdWidth = cmdWinSetWindowed.GetField("Width", AnyInstance);
            var cmdHeight = cmdWinSetWindowed.GetField("Height", AnyInstance);
            var cmdPosX = cmdWinSetWindowed.GetField("PosX", AnyInstance);
            var cmdPosY = cmdWinSetWindowed.GetField("PosY", AnyInstance);

            if (sendCmd == null || mainWindow == null || windowMode == null || sdl3Window == null ||
                windowSize == null || windowPos == null || prevWindowSize == null || prevWindowPos == null ||
                cmdWindow == null || cmdWidth == null || cmdHeight == null || cmdPosX == null || cmdPosY == null)
            {
                SanabiLogger.LogWarn("BetterFullscreenPatch: A required engine method or field is missing, skipping patch.");
                return false;
            }

            WindowingImplType = windowingImplType;
            SendCmdMethod = sendCmd;
            _clydeType = clydeType;
            CmdWinSetWindowed = cmdWinSetWindowed;
            _clyde_mainWindow = mainWindow;
            _clyde_windowMode = windowMode;
            sdl3WindowRegSdl3Window = sdl3Window;
            windowRegWindowSize = windowSize;
            windowRegWindowPos = windowPos;
            windowRegPrevWindowSize = prevWindowSize;
            windowRegPrevWindowPos = prevWindowPos;
            _cmdWinWindow = cmdWindow;
            _cmdWinWidth = cmdWidth;
            _cmdWinHeight = cmdHeight;
            _cmdWinPosX = cmdPosX;
            _cmdWinPosY = cmdPosY;
            return true;
        }
        catch (Exception e)
        {
            SanabiLogger.LogWarn($"BetterFullscreenPatch: Failed resolving engine members, skipping patch: {e}");
            return false;
        }
    }

    [PatchEntry(PatchRunLevel.Engine)]
    [StackTraceHidden]
    public static void Patch()
    {
        if (!Enabled)
            return;

        if (!Resolve())
            return;

        PatchHelpers.PatchMethod(
            WindowingImplType,
            "WindowCreate",
            WindowCreatePrefix,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            WindowingImplType,
            "UpdateMainWindowMode",
            PrefixUpdateMainWindowMode,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchConstructor(
            _clydeType,
            ClydeConstructorPrefix,
            HarmonyPatchType.Prefix
        );

        PatchHelpers.PatchMethod(
            _clydeType,
            "TryInitMainWindow",
            TryInitMainWindowPostfix,
            HarmonyPatchType.Postfix
        );
    }

    [StackTraceHidden]
    private static void WindowCreatePrefix(ref object __instance) => _windowingImpl = __instance;
    [StackTraceHidden]
    private static bool PrefixUpdateMainWindowMode(ref object __instance)
    {
        // If we fail, let the original engine code run
        return !DoMainWindowUpdate(__instance);
    }

    [StackTraceHidden]
    private static void ClydeConstructorPrefix(ref object __instance) => _clyde = __instance;
    [StackTraceHidden]
    private static void TryInitMainWindowPostfix() => DoMainWindowUpdate(_windowingImpl, saveSize: false);

    [StackTraceHidden]
    private static dynamic? GetMainWindow() => _clyde_mainWindow.GetValue(_clyde);

    [StackTraceHidden]
    private static TruncatedClydeWindowMode? GetWindowMode() => (TruncatedClydeWindowMode)_clyde_windowMode.GetValue(_clyde); // Directly casted

    [StackTraceHidden]
    private static object ConstructCmdWinSetWindowed(nint window, int w, int h, int x, int y)
    {
        var cmd = Activator.CreateInstance(CmdWinSetWindowed)!;

        _cmdWinWindow.SetValue(cmd, window);
        _cmdWinWidth.SetValue(cmd, w);
        _cmdWinHeight.SetValue(cmd, h);
        _cmdWinPosX.SetValue(cmd, x);
        _cmdWinPosY.SetValue(cmd, y);

        return cmd;
    }

    // Fields
    [StackTraceHidden]
    private static nint GetSdl3Window(object sdl3WindowReg) => (nint)sdl3WindowRegSdl3Window.GetValue(sdl3WindowReg)!;

    [StackTraceHidden]
    private static dynamic GetWindowSize(object windowReg) => windowRegWindowSize.GetValue(windowReg)!;
    [StackTraceHidden]
    private static dynamic GetWindowPos(object windowReg) => windowRegWindowPos.GetValue(windowReg)!;

    [StackTraceHidden]
    private static dynamic GetPrevWindowSize(object windowReg) => windowRegPrevWindowSize.GetValue(windowReg)!;
    [StackTraceHidden]
    private static void SetPrevWindowSize(object windowReg, dynamic val) => windowRegPrevWindowSize.SetValue(windowReg, val);
    [StackTraceHidden]
    private static dynamic GetPrevWindowPos(object windowReg) => windowRegPrevWindowPos.GetValue(windowReg)!;
    [StackTraceHidden]
    private static void SetPrevWindowPos(object windowReg, dynamic val) => windowRegPrevWindowPos.SetValue(windowReg, val);

    private static bool WasSdlBound = false;

    /// <summary>
    ///     Returns true if the window was handled, false if nothing was done or it failed.
    /// </summary>
    [StackTraceHidden]
    private static bool DoMainWindowUpdate(object? windowingImpl, bool saveSize = true)
    {
        try
        {
            if (_clyde == null || windowingImpl == null)
                return false;

            var mainWindow = GetMainWindow();
            if (mainWindow == null)
                return false;

            if (!WasSdlBound)
            {
                // Patching this onto Sdl3WindowingImpl.InitSdl3 didn't work for some reason i dgaf to understand so here it goes
                SDL.BindSdl();
                WasSdlBound = true;
            }

            nint sdlWindow = GetSdl3Window(mainWindow);

            if (GetWindowMode() == TruncatedClydeWindowMode.Fullscreen)
            {
                if (saveSize)
                {
                    SetPrevWindowSize(mainWindow, GetWindowSize(mainWindow));
                    SetPrevWindowPos(mainWindow, GetWindowPos(mainWindow));
                }

                var displayId = GetDisplayForWindow(sdlWindow);
                if (!SDL.SDL_GetDisplayBounds(displayId, out var bounds))
                {
                    SanabiLogger.LogWarn($"BetterFullscreenPatch: SDL_GetDisplayBounds failed for display {displayId}.");
                    return false;
                }

                var cmd = ConstructCmdWinSetWindowed(
                    sdlWindow,
                    bounds.w,
                    bounds.h,
                    bounds.x,
                    bounds.y
                );
                SendCmdMethod.Invoke(windowingImpl, (object[])[cmd]);
            }
            else
            {
                var prevWindowSize = GetPrevWindowSize(mainWindow);
                var prevWindowPos = GetPrevWindowPos(mainWindow);

                var cmd = ConstructCmdWinSetWindowed(
                    sdlWindow,
                    prevWindowSize.X,
                    prevWindowSize.Y,
                    prevWindowPos.X,
                    prevWindowPos.Y
                );
                SendCmdMethod.Invoke(windowingImpl, (object[])[cmd]);
            }

            SDL.SDL_SetWindowBordered(sdlWindow, false);
            return true;
        }
        catch (Exception e)
        {
            SanabiLogger.LogError($"BetterFullscreenPatch: DoMainWindowUpdate failed: {e}");
            return false;
        }
    }

    /// <summary>
    ///     Gets the display that the given SDL window is on, falling back to the primary display.
    /// </summary>
    [StackTraceHidden]
    private static uint GetDisplayForWindow(nint sdlWindow)
    {
        var displayId = SDL.SDL_GetDisplayForWindow(sdlWindow);
        return displayId != 0 ? displayId : SDL.SDL_GetPrimaryDisplay();
    }
}

public enum TruncatedClydeWindowMode : byte { Windowed = 0, Fullscreen = 1 }
