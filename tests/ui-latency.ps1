# ui-latency.ps1 : how long the settings window of a process takes to answer while a button is pressed (WM_NULL with a timeout).
# Presses the button with the given text, and polls the window every 20 ms for -Seconds; prints the slowest answer in ms.
param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Text, [int]$Seconds = 6)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Lat {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    public static IntPtr Top(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); var t = new StringBuilder(128); GetWindowText(h, t, 128);
            if (p == pid && IsWindowVisible(h) && t.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    public static IntPtr Button(IntPtr top, string text) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(top, (h, l) => { var c = new StringBuilder(64); GetClassName(h, c, 64); var t = new StringBuilder(128); GetWindowText(h, t, 128);
            if (c.ToString().ToUpperInvariant().Contains("BUTTON") && t.ToString() == text) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
}
"@
$top = [Lat]::Top([uint32]$ProcessId, 'LANDock')
if ($top -eq [IntPtr]::Zero) { Write-Output "no LANDock window"; exit 1 }
$b = [Lat]::Button($top, $Text)
if ($b -eq [IntPtr]::Zero) { Write-Output "no button '$Text'"; exit 1 }
[void][Lat]::PostMessage($b, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)   # BM_CLICK, queued: this script is not blocked by the window
$sw = [Diagnostics.Stopwatch]::StartNew(); $max = 0; $n = 0; $hung = 0
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $t0 = $sw.ElapsedMilliseconds; $r = [IntPtr]::Zero
    $ok = [Lat]::SendMessageTimeout($top, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 5000, [ref]$r)
    $d = $sw.ElapsedMilliseconds - $t0
    if ($ok -eq [IntPtr]::Zero) { $hung++ }
    if ($d -gt $max) { $max = $d }
    $n++; Start-Sleep -Milliseconds 20
}
Write-Output "polls=$n slowest answer=${max} ms timeouts=$hung"
