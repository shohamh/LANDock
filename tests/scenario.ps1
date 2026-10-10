# scenario.ps1 -M1 '\\.\DISPLAYn' -M2 ... -TV ... -Out <dir>
# Runs on the host desktop (DPI aware). Needs an unlocked, awake session with no elevated window in front.
param([string]$M1, [string]$M2, [string]$TV, [string]$Out)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class S {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct IU { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public IU u; }
  public delegate bool CB(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int sz);
  [DllImport("user32.dll")] static extern bool EnumWindows(CB cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public static IntPtr FindByTitlePrefix(string cls, string prefix) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var c = new StringBuilder(128); GetClassName(h, c, 128);
      var t = new StringBuilder(256); GetWindowText(h, t, 256);
      if (c.ToString() == cls && t.ToString().StartsWith(prefix)) { found = h; return false; }
      return true; }, IntPtr.Zero);
    return found; }
  public static List<IntPtr> ListByClass(string cls) {
    var r = new List<IntPtr>();
    EnumWindows((h, l) => { if (!IsWindowVisible(h)) return true; var c = new StringBuilder(128); GetClassName(h, c, 128); if (c.ToString() == cls) r.Add(h); return true; }, IntPtr.Zero);
    return r; }
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static void CtrlShiftN() {
    var i = new INPUT[6];
    i[0].type = 1; i[0].u.ki.vk = 0x11; i[1].type = 1; i[1].u.ki.vk = 0x10; i[2].type = 1; i[2].u.ki.vk = 0x4E;
    i[3].type = 1; i[3].u.ki.vk = 0x4E; i[3].u.ki.flags = 2; i[4].type = 1; i[4].u.ki.vk = 0x10; i[4].u.ki.flags = 2; i[5].type = 1; i[5].u.ki.vk = 0x11; i[5].u.ki.flags = 2;
    SendInput(6, i, Marshal.SizeOf(typeof(INPUT))); }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public static void CharHold(char ch, int holdMs) {
    var d = new INPUT[1]; d[0].type = 1; d[0].u.ki.scan = ch; d[0].u.ki.flags = 0x0004;
    var u = new INPUT[1]; u[0].type = 1; u[0].u.ki.scan = ch; u[0].u.ki.flags = 0x0004 | 0x0002;
    SendInput(1, d, Marshal.SizeOf(typeof(INPUT))); System.Threading.Thread.Sleep(holdMs); SendInput(1, u, Marshal.SizeOf(typeof(INPUT))); }
  public static void KeyHold(ushort vk, int holdMs) {
    var d = new INPUT[1]; d[0].type = 1; d[0].u.ki.vk = vk;
    var u = new INPUT[1]; u[0].type = 1; u[0].u.ki.vk = vk; u[0].u.ki.flags = 2;
    SendInput(1, d, Marshal.SizeOf(typeof(INPUT))); System.Threading.Thread.Sleep(holdMs); SendInput(1, u, Marshal.SizeOf(typeof(INPUT))); }
  public static void CtrlA() {
    var i = new INPUT[4]; i[0].type = 1; i[0].u.ki.vk = 0x11; i[1].type = 1; i[1].u.ki.vk = 0x41; i[2].type = 1; i[2].u.ki.vk = 0x41; i[2].u.ki.flags = 2; i[3].type = 1; i[3].u.ki.vk = 0x11; i[3].u.ki.flags = 2;
    SendInput(4, i, Marshal.SizeOf(typeof(INPUT))); }
  public static void Click() {
    var i = new INPUT[2]; i[0].type = 0; i[0].u.mi.flags = 0x0002; i[1].type = 0; i[1].u.mi.flags = 0x0004;
    SendInput(2, i, Marshal.SizeOf(typeof(INPUT))); }
  public static void TypeChar(char ch) {
    var i = new INPUT[2]; i[0].type = 1; i[0].u.ki.scan = ch; i[0].u.ki.flags = 0x0004;
    i[1].type = 1; i[1].u.ki.scan = ch; i[1].u.ki.flags = 0x0004 | 0x0002;
    SendInput(2, i, Marshal.SizeOf(typeof(INPUT))); }
  public static void Key(ushort vk) {
    var i = new INPUT[2]; i[0].type = 1; i[0].u.ki.vk = vk; i[1].type = 1; i[1].u.ki.vk = vk; i[1].u.ki.flags = 0x0002;
    SendInput(2, i, Marshal.SizeOf(typeof(INPUT))); }
}
'@
[void][S]::SetProcessDpiAwarenessContext([IntPtr](-4))
$script:log = @()
function L($m) { $script:log += $m; $m }
function Scr($dev) { ([System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -eq $dev }).Bounds }
function Cur { $p = New-Object S+PT; [void][S]::GetCursorPos([ref]$p); $p }
function ScreenOf($x, $y) { $s = [System.Windows.Forms.Screen]::FromPoint((New-Object System.Drawing.Point $x, $y)); $s.DeviceName }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function NewNotepad() {
  $before = [S]::ListByClass('Notepad')
  if ($before.Count -eq 0) { return [IntPtr]::Zero }
  $base = $before[0]
  $r = New-Object S+RECT; [void][S]::GetWindowRect($base, [ref]$r)
  [void][S]::SetCursorPos([int](($r.L + $r.R) / 2), [int]($r.T + ($r.B - $r.T) * 0.65)); Start-Sleep -Milliseconds 250
  [S]::Click(); Start-Sleep -Milliseconds 300
  [S]::CtrlShiftN()
  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 300
    $now = [S]::ListByClass('Notepad')
    $new = $now | Where-Object { $before -notcontains $_ } | Select-Object -First 1
    if ($new) { return $new }
  }
  return [IntPtr]::Zero
}
function CloseDiscard($h) {
  [void][S]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero); Start-Sleep -Milliseconds 800
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $b = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Current.Name -match "^Don.?t save$" } | Select-Object -First 1
    if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500; return 'closed without saving' }
  } catch {}
  return 'closed'
}
function ReadNotepadText($h) {
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $AE = [System.Windows.Automation.AutomationElement]; $CT = [System.Windows.Automation.ControlType]
    $doc = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Document)))
    if (-not $doc) { $doc = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit))) }
    if (-not $doc) { return $null }
    return $doc.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
  } catch { return $null }
}
function Norm($t) { if ($null -eq $t) { return $null }; ($t -replace "\r\n|\r|\n", "|").TrimEnd(" ", "|") }
function TypeVerified($h, $text, $label) {
  $attempts = @(@(15, 35), @(30, 70), @(50, 120))
  $n = 0
  foreach ($a in $attempts) {
    $n++
    if ($n -gt 1) { [S]::CtrlA(); Start-Sleep -Milliseconds 200; [S]::KeyHold(0x2E, 40); Start-Sleep -Milliseconds 300 }
    foreach ($ch in $text.ToCharArray()) {
      if ($ch -eq [char]13) { [S]::KeyHold(0x0D, $a[0]) } else { [S]::CharHold($ch, $a[0]) }
      Start-Sleep -Milliseconds $a[1]
    }
    Start-Sleep -Milliseconds 900
    $got = Norm (ReadNotepadText $h); $want = Norm $text
    if ($null -eq $got) { L "Notepad ${label}: typed $($text.Length) characters (attempt $n, hold $($a[0]) ms) but could not read the text back"; return }
    if ($got -eq $want) { L "Notepad ${label}: typed $($text.Length) characters, read back and matched exactly (attempt $n, key hold $($a[0]) ms, gap $($a[1]) ms)"; return }
    L "Notepad ${label}: attempt $n mismatch (got $($got.Length) chars, expected $($want.Length)); retrying slower"
  }
  L "Notepad ${label}: STILL MISMATCHED after $n attempts. Got: '$got'"
}
$script:npWindows = @()
if (Get-Process LogonUI -ErrorAction SilentlyContinue) { L "SESSION LOCKED: cannot run"; exit 3 }
if (-not [S]::SetCursorPos(500, 500)) { L "CURSOR INJECTION BLOCKED (elevated foreground window or secure desktop)"; exit 4 }

function Shot($name, $rect) {
  $bmp = New-Object System.Drawing.Bitmap $rect.Width, $rect.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.X, $rect.Y, 0, 0, $bmp.Size); $g.Dispose()
  $path = Join-Path $Out $name
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  L "screenshot: $name ($($rect.Width)x$($rect.Height))"
}
$all = [System.Windows.Forms.Screen]::AllScreens
$minX = ($all | ForEach-Object { $_.Bounds.X } | Measure-Object -Minimum).Minimum
$minY = ($all | ForEach-Object { $_.Bounds.Y } | Measure-Object -Minimum).Minimum
$maxX = ($all | ForEach-Object { $_.Bounds.Right } | Measure-Object -Maximum).Maximum
$maxY = ($all | ForEach-Object { $_.Bounds.Bottom } | Measure-Object -Maximum).Maximum
$desk = New-Object System.Drawing.Rectangle $minX, $minY, ($maxX - $minX), ($maxY - $minY)
$lap = ($all | Where-Object { $_.Primary }).Bounds
L "monitors:"
$all | ForEach-Object { L ("  {0} {1}x{2} at ({3},{4}){5}" -f $_.DeviceName, $_.Bounds.Width, $_.Bounds.Height, $_.Bounds.X, $_.Bounds.Y, $(if ($_.Primary) { ' primary' } else { '' })) }

# 1. Moonlight windows on the laptop screen, small, in a row
$ml = @(@{p=49989; n='M1'; x=10}, @{p=50989; n='M2'; x=650}, @{p=51989; n='TV'; x=1290})
foreach ($m in $ml) {
  $proc = Get-CimInstance Win32_Process -Filter "Name='Moonlight.exe'" | Where-Object { $_.CommandLine -match "127.0.0.1:$($m.p)" } | Select-Object -First 1
  if (-not $proc) { L "no Moonlight process for $($m.n)"; continue }
  $h = (Get-Process -Id $proc.ProcessId).MainWindowHandle
  [void][S]::MoveWindow($h, $m.x, 90, 630, 400, $true)
  L "Moonlight window $($m.n) placed on the laptop screen"
}
Start-Sleep -Seconds 1
# keep the user's own Notepad window out of every screenshot: minimize it now, restore it at the end
$baseNp = [S]::ListByClass('Notepad') | Select-Object -First 1
if ($baseNp) { [void][S]::ShowWindow($baseNp, 6); L "minimized the user's own Notepad window for the screenshots (restored at the end)"; Start-Sleep -Seconds 1 }
Shot '01-laptop-three-streams-idle.png' $lap
Shot '01-whole-desktop-idle.png' $desk

# 2. one Notepad per virtual display: move it around, then type
$targets = @(
  @{f='m1'; disp=$M1; label='M1 (1920x1080 monitor)'},
  @{f='m2'; disp=$M2; label='M2 (1920x1080 monitor)'},
  @{f='tv'; disp=$TV; label='TV (3840x2160)'})
# open the three new Notepad windows first (needs the base window visible), then minimize the base again
if ($baseNp) { [void][S]::ShowWindow($baseNp, 9); Start-Sleep -Milliseconds 800 }
foreach ($t in $targets) {
  $h = NewNotepad
  if ($h -eq [IntPtr]::Zero) { L "could not open a new Notepad window for $($t.f) (is a Notepad window open to start from?)"; $t.h = [IntPtr]::Zero; continue }
  $t.h = $h; $script:npWindows += $h; Start-Sleep -Milliseconds 500
}
if ($baseNp) { [void][S]::ShowWindow($baseNp, 6); Start-Sleep -Milliseconds 800 }
foreach ($t in $targets) {
  $np = $t.h
  if (-not $np -or $np -eq [IntPtr]::Zero) { continue }
  $b = Scr $t.disp
  if (-not $b) { L "display $($t.disp) not found"; continue }
  $w = [int]([Math]::Min(900, $b.Width * 0.5)); $hh = [int]([Math]::Min(500, $b.Height * 0.45))
  $r = New-Object S+RECT; [void][S]::GetWindowRect($np, [ref]$r)
  $cx = $r.L; $cy = $r.T
  $mid = @(($b.X + $b.Width/2 - $w/2), ($b.Y + $b.Height/2 - $hh/2))
  $wps = @($mid, @(($b.X + 40), ($b.Y + 40)), @(($b.Right - $w - 40), ($b.Y + 60)), @(($b.Right - $w - 40), ($b.Bottom - $hh - 60)), @(($b.X + 60), ($b.Bottom - $hh - 60)), $mid)
  $moves = 0
  foreach ($wp in $wps) {
    $steps = 14; $sx = $cx; $sy = $cy
    for ($k = 1; $k -le $steps; $k++) {
      $nx = [int]($sx + ($wp[0] - $sx) * $k / $steps); $ny = [int]($sy + ($wp[1] - $sy) * $k / $steps)
      [void][S]::MoveWindow($np, $nx, $ny, $w, $hh, $true); Start-Sleep -Milliseconds 25; $moves++
    }
    $cx = [int]$wp[0]; $cy = [int]$wp[1]
  }
  $r2 = New-Object S+RECT; [void][S]::GetWindowRect($np, [ref]$r2)
  $on = ScreenOf (($r2.L + $r2.R) / 2) (($r2.T + $r2.B) / 2)
  L "Notepad $($t.f): moved in $moves steps; final window at ($($r2.L),$($r2.T)) on $on (target $($t.disp))"
  $cl = New-Object S+RECT; [void][S]::GetClientRect($np, [ref]$cl)
  $o = New-Object S+PT; [void][S]::ClientToScreen($np, [ref]$o)
  [void][S]::SetCursorPos($o.X + [int]($cl.R/2), $o.Y + [int]($cl.B/2)); Start-Sleep -Milliseconds 300
  [S]::Click(); Start-Sleep -Milliseconds 300
  $text = "Hello from $($t.label)." + [char]13 + "Display: $($t.disp) at $($b.X),$($b.Y) size $($b.Width)x$($b.Height)." + [char]13 + "Typed on the host while the laptop shows this display in a small window."
  TypeVerified $np $text $t.f
  Shot "02-after-typing-$($t.f)-laptop.png" $lap
}
Start-Sleep -Seconds 2
Shot '03-laptop-three-streams-with-notepads.png' $lap
Shot '03-whole-desktop-with-notepads.png' $desk

# 3. mouse sweep: laptop -> M1 -> M2 -> TV and back, 25 px steps
$script:stuck = 0; $script:trans = @(); $script:last = ''; $script:steps = 0
function Sweep($x0, $x1, $y) {
  $dir = if ($x1 -ge $x0) { 25 } else { -25 }
  for ($x = $x0; (($dir -gt 0) -and ($x -le $x1)) -or (($dir -lt 0) -and ($x -ge $x1)); $x += $dir) {
    [void][S]::SetCursorPos($x, $y); $c = Cur; $script:steps++
    if (([Math]::Abs($c.X - $x) -gt 1) -or ([Math]::Abs($c.Y - $y) -gt 1)) { $script:stuck++ }
    $d = ScreenOf $c.X $c.Y
    if ($d -ne $script:last) { $script:trans += "$d@x=$($c.X)"; $script:last = $d }
    Start-Sleep -Milliseconds 4
  }
}
Sweep 100 ($maxX - 20) 500
Sweep ($maxX - 20) 100 700
L "mouse sweep: $($script:steps) positions, $($script:stuck) mismatches (cursor not where it was put); display sequence: $($script:trans -join ' -> ')"
for ($k = 0; $k -le 60; $k++) {
  $x = 100 + [int](($maxX - 120) * $k / 60); $y = 300 + [int](1700 * $k / 60)
  [void][S]::SetCursorPos($x, $y)
}
$c = Cur
L "diagonal sweep ended at ($($c.X),$($c.Y)) on $(ScreenOf $c.X $c.Y)"
foreach ($h in $script:npWindows) { L "Notepad window $h : $(CloseDiscard $h)" }
if ($baseNp) { [void][S]::ShowWindow($baseNp, 9); L "restored the user's own Notepad window" }
$script:log | Out-File (Join-Path $Out 'scenario.log') -Encoding utf8
