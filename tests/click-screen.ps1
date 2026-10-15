# click-screen.ps1 : clicks the middle of the notice's screen diagram (a mouse down and up sent to the window, so the mouse does not move
# and the window is not activated). The notice is the process's visible top-most window without a title.
param([Parameter(Mandatory)][int]$ProcessId)
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ClickAt {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static List<IntPtr> Windows(uint pid) {
        var res = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) res.Add(h); return true; }, IntPtr.Zero);
        return res;
    }
    public static string Click(IntPtr h) {
        RECT r; GetClientRect(h, out r);
        int x = r.R / 2, y = (int)(r.B * 92.0 / 214.0);   // the diagram area is the middle of the notice (12,40 300x214 base size)
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        SendMessage(h, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN
        SendMessage(h, 0x0202, IntPtr.Zero, lp); // WM_LBUTTONUP
        return x + "," + y + " of " + r.R + "x" + r.B;
    }
}
"@
$ws = [ClickAt]::Windows([uint32]$ProcessId)
if ($ws.Count -eq 0) { Write-Output "no visible window"; exit 1 }
Write-Output ("clicked " + [ClickAt]::Click($ws[0]))
