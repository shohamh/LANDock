# click-button.ps1 : presses a button of one process's window by its text (BM_CLICK). The mouse does not move and the window is not
# activated: the test presses the button of the notice the way a click would.
param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Text)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Press {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static List<IntPtr> Windows(uint pid) {
        var res = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) res.Add(h); return true; }, IntPtr.Zero);
        return res;
    }
    public static IntPtr FindButton(IntPtr top, string text) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(top, (h, l) => {
            var cls = new StringBuilder(64); GetClassName(h, cls, 64);
            var t = new StringBuilder(128); GetWindowText(h, t, 128);
            // WinForms names its button windows "WindowsForms10.BUTTON...": match on the text, and on the class containing BUTTON
            if (cls.ToString().ToUpperInvariant().Contains("BUTTON") && t.ToString() == text) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static bool Click(IntPtr button) { SendMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero); return true; }   // BM_CLICK
}
"@
$done = $false
foreach ($w in [Press]::Windows([uint32]$ProcessId)) {
    $b = [Press]::FindButton($w, $Text)
    if ($b -ne [IntPtr]::Zero) { [void][Press]::Click($b); Write-Output "pressed '$Text' in window $w"; $done = $true; break }
}
if (-not $done) { Write-Output "no visible button '$Text' in process $ProcessId"; exit 1 }
