# pointer-probe.ps1 -PortA 49989 -DispA '\.\DISPLAY7' -PortB 50989 -DispB '\.\DISPLAY8'
# DPI-aware probe: positions the two Moonlight windows, moves the pointer into each, reads where the host cursor lands.
param([int]$PortA, [string]$DispA, [int]$PortB, [string]$DispB)
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
}
'@
[void][W]::SetProcessDpiAwarenessContext([IntPtr](-4))
if (Get-Process LogonUI -ErrorAction SilentlyContinue) { 'SESSION LOCKED (LogonUI running): pointer test not possible'; exit 3 }
function Win($port) {
  $p = Get-CimInstance Win32_Process -Filter "Name='Moonlight.exe'" | Where-Object { $_.CommandLine -match "127.0.0.1:$port" } | Select-Object -First 1
  if (-not $p) { return $null }
  (Get-Process -Id $p.ProcessId).MainWindowHandle
}
function Bounds($dev) { ([System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -eq $dev }).Bounds }
function Cur { $pt = New-Object W+PT; [void][W]::GetCursorPos([ref]$pt); $pt }
$cases = @(@{port=$PortA; disp=$DispA; x=10}, @{port=$PortB; disp=$DispB; x=960})
$fr = @(@(0.25,0.25), @(0.75,0.50), @(0.50,0.90), @(0.05,0.05), @(0.95,0.95))

$fgsig = Add-Type -PassThru -Name FGX -Namespace PX -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);'
$sb = New-Object System.Text.StringBuilder 256; [void]$fgsig::GetWindowText($fgsig::GetForegroundWindow(), $sb, 256)
if (-not [W]::SetCursorPos(500, 500)) { "CURSOR INJECTION BLOCKED: SetCursorPos fails. Foreground window: '$sb'. An elevated foreground window (UIPI) or a locked/secure desktop blocks it; close or minimize it and rerun."; exit 4 }
foreach ($c in $cases) {
  $h = Win $c.port
  if (-not $h -or $h -eq [IntPtr]::Zero) { "port $($c.port): no Moonlight window found"; continue }
  [void][W]::MoveWindow($h, $c.x, 60, 940, 600, $true); Start-Sleep -Milliseconds 600
  $r = New-Object W+RECT; [void][W]::GetClientRect($h, [ref]$r)
  $o = New-Object W+PT; [void][W]::ClientToScreen($h, [ref]$o)
  $cw = $r.R - $r.L; $ch = $r.B - $r.T
  $vw = [Math]::Min($cw, $ch * 16.0 / 9.0); $vh = $vw * 9.0 / 16.0
  $ox = $o.X + ($cw - $vw) / 2.0; $oy = $o.Y + ($ch - $vh) / 2.0
  $w = $vw; $hh = $vh; $o = [pscustomobject]@{ X = $ox; Y = $oy }
  $b = Bounds $c.disp
  "port $($c.port) -> $($c.disp): video rect at ($([int]$o.X),$([int]$o.Y)) $([int]$w)x$([int]$hh) (client ${cw}x${ch}); display bounds $($b.X),$($b.Y) $($b.Width)x$($b.Height)"
  [void][W]::SetForegroundWindow($h); Start-Sleep -Milliseconds 300
  # click once inside the window to give it input focus (also forwarded to the host)
  [void][W]::SetCursorPos($o.X + [int]($w/2), $o.Y + [int]($hh/2)); Start-Sleep -Milliseconds 200
  [W]::mouse_event(0x2,0,0,0,[IntPtr]::Zero); [W]::mouse_event(0x4,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 400
  foreach ($f in $fr) {
    $px = $o.X + [int]($f[0]*$w); $py = $o.Y + [int]($f[1]*$hh)
    [void][W]::SetCursorPos($px - 6, $py - 6); Start-Sleep -Milliseconds 120
    [void][W]::SetCursorPos($px, $py); $imm = Cur; Start-Sleep -Milliseconds 120
    [W]::mouse_event(0x1, 2, 2, 0, [IntPtr]::Zero); [W]::mouse_event(0x1, -2, -2, 0, [IntPtr]::Zero)
    $sw = [Diagnostics.Stopwatch]::StartNew(); $trace = @(); $last = ""
    while ($sw.ElapsedMilliseconds -lt 700) { $q = Cur; $k = "($($q.X),$($q.Y))"; if ($k -ne $last) { $trace += "$k@$($sw.ElapsedMilliseconds)ms"; $last = $k } }
    $cur = Cur
    $ex = $b.X + $f[0]*$b.Width; $ey = $b.Y + $f[1]*$b.Height
    "  f=($($f[0]),$($f[1])) window pt=($px,$py) immediate=($($imm.X),$($imm.Y)) -> host cursor=($($cur.X),$($cur.Y)) expected=($([int]$ex),$([int]$ey)) trace=$($trace -join ">") delta=($([int]($cur.X-$ex)),$([int]($cur.Y-$ey)))"
  }
}
