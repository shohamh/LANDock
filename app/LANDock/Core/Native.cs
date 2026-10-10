using System.Runtime.InteropServices;

namespace LANDock.Core;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; public int Width => R - L; public int Height => B - T; }

    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;

    /// <summary>Brings a window to the front (a tap on Alt first, so Windows lets us take the foreground).</summary>
    public static void Activate(IntPtr h)
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        ShowWindow(h, 9); SetForegroundWindow(h);
    }

    /// <summary>Moonlight's own hotkey, Ctrl+Alt+Shift+X: toggles fullscreen / windowed.</summary>
    public static void ToggleFullscreen()
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x10, 0, 0, UIntPtr.Zero);
        keybd_event(0x58, 0, 0, UIntPtr.Zero); Thread.Sleep(80); keybd_event(0x58, 0, 2, UIntPtr.Zero);
        keybd_event(0x10, 0, 2, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }
}
