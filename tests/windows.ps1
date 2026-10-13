# windows.ps1 : lists the visible top-level windows of one process (the test scripts use it to find the notice window).
param([Parameter(Mandatory)][int]$ProcessId, [string]$SaveDir = '')
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class WinList {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static List<string> ForPid(uint pid) {
        var res = new List<string>();
        int z = 0;   // EnumWindows goes from the top of the stack down: z is the position in the stacking order
        EnumWindows((h, l) => {
            z++;
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            RECT r; GetWindowRect(h, out r);
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
            int ex = GetWindowLong(h, -20);
            res.Add(string.Format("{0} z={1} vis={2} ex=0x{3:X} rect=({4},{5})-({6},{7}) class={8} title='{9}'",
                h, z, IsWindowVisible(h), ex, r.L, r.T, r.R, r.B, cls, txt));
            return true;
        }, IntPtr.Zero);
        return res;
    }
    // the top of the stacking order, whatever the process (what covers what)
    public static List<string> Top(int max) {
        var res = new List<string>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            uint p; GetWindowThreadProcessId(h, out p);
            var txt = new StringBuilder(200); GetWindowText(h, txt, 200);
            int ex = GetWindowLong(h, -20);
            if (txt.Length == 0 && ex == 0) return true;
            RECT r; GetWindowRect(h, out r);
            res.Add(string.Format("pid={0} topmost={1} rect=({2},{3})-({4},{5}) title='{6}'", p, (ex & 8) != 0, r.L, r.T, r.R, r.B, txt));
            return res.Count < max;
        }, IntPtr.Zero);
        return res;
    }
}
"@
[WinList]::ForPid([uint32]$ProcessId) | ForEach-Object { $_ }
Write-Output "-- top of the stacking order (topmost first):"
[WinList]::Top(10) | ForEach-Object { $_ }

# -SaveDir: each visible window of the process is also drawn into a PNG (PrintWindow), so its content can be seen even when covered
if ($SaveDir) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class WinPrint {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@
    $i = 0
    foreach ($line in [WinList]::ForPid([uint32]$ProcessId)) {
        if ($line -notmatch ' vis=True ') { continue }
        $h = [IntPtr][long]($line.Split(' ')[0])
        $rect = [WinList+RECT]::new()
        [void][WinList]::GetWindowRect($h, [ref]$rect)
        $w = [Math]::Max(1, $rect.R - $rect.L); $hh = [Math]::Max(1, $rect.B - $rect.T)
        $bmp = New-Object System.Drawing.Bitmap $w, $hh
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [void][WinPrint]::PrintWindow($h, $hdc, 2)
        $g.ReleaseHdc($hdc); $g.Dispose()
        $i++
        $bmp.Save((Join-Path $SaveDir ("window-$i.png")), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }
    Write-Output "saved $i window image(s) to $SaveDir"
}
