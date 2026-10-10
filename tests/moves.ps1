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
  public static void MouseDown() { var i = new INPUT[1]; i[0].type = 0; i[0].u.mi.flags = 0x0002; SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); }
  public static void MouseUp() { var i = new INPUT[1]; i[0].type = 0; i[0].u.mi.flags = 0x0004; SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); }
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


# ---- Moonlight windows: small, in a row on the laptop screen
$ml = @(@{p=49989; n='M1'; x=10}, @{p=50989; n='M2'; x=650}, @{p=51989; n='TV'; x=1290})
$mlh = @{}
foreach ($m in $ml) {
  $proc = Get-CimInstance Win32_Process -Filter "Name='Moonlight.exe'" | Where-Object { $_.CommandLine -match "127.0.0.1:$($m.p)" } | Select-Object -First 1
  if (-not $proc) { L "no Moonlight process for $($m.n)"; exit 5 }
  $mlh[$m.n] = (Get-Process -Id $proc.ProcessId).MainWindowHandle
  [void][S]::MoveWindow($mlh[$m.n], $m.x, 90, 630, 400, $true)
}
L "Moonlight windows M1, M2, TV placed in a row on the laptop screen"
Start-Sleep -Seconds 2
$baseNp = [S]::ListByClass('Notepad') | Select-Object -First 1
if ($baseNp) { [void][S]::ShowWindow($baseNp, 6); L "minimized the user's own Notepad window (restored at the end)"; Start-Sleep -Seconds 1 }

# ---- helpers: where is a window, what do the streams show
$lapName = ([System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.Primary }).DeviceName
$bounds = @{ M1 = (Scr $M1); M2 = (Scr $M2); TV = (Scr $TV); LAP = (New-Object System.Drawing.Rectangle 0, 500, 1920, 580) }
function KeyOf($x, $y) {
  $n = ScreenOf ([int]$x) ([int]$y)
  if ($n -eq $M1) { 'M1' } elseif ($n -eq $M2) { 'M2' } elseif ($n -eq $TV) { 'TV' } else { 'LAP' }
}
function WinKey($h) { $r = New-Object S+RECT; [void][S]::GetWindowRect($h, [ref]$r); KeyOf (($r.L + $r.R) / 2) (($r.T + $r.B) / 2) }
function MlFrame($name) {
  $h = $mlh[$name]
  $cl = New-Object S+RECT; [void][S]::GetClientRect($h, [ref]$cl)
  $o = New-Object S+PT; [void][S]::ClientToScreen($h, [ref]$o)
  $bmp = New-Object System.Drawing.Bitmap $cl.R, $cl.B
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($o.X, $o.Y, 0, 0, $bmp.Size); $g.Dispose()
  $small = New-Object System.Drawing.Bitmap 96, 54
  $g2 = [System.Drawing.Graphics]::FromImage($small); $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
  $g2.DrawImage($bmp, 0, 0, 96, 54); $g2.Dispose(); $bmp.Dispose()
  return $small
}
function FrameDiff($a, $b) {
  $sum = 0.0
  for ($x = 0; $x -lt 96; $x += 2) { for ($y = 0; $y -lt 54; $y += 2) {
    $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
    $sum += ([Math]::Abs($p.R - $q.R) + [Math]::Abs($p.G - $q.G) + [Math]::Abs($p.B - $q.B)) / 3.0
  } }
  return $sum / (48 * 27)
}
function Frames() { @{ M1 = (MlFrame 'M1'); M2 = (MlFrame 'M2'); TV = (MlFrame 'TV') } }
function FocusEditor($h) {
  $cl = New-Object S+RECT; [void][S]::GetClientRect($h, [ref]$cl)
  $o = New-Object S+PT; [void][S]::ClientToScreen($h, [ref]$o)
  [void][S]::SetCursorPos($o.X + [int]($cl.R / 2), $o.Y + [int]($cl.B * 0.6)); Start-Sleep -Milliseconds 300
  [S]::Click(); Start-Sleep -Milliseconds 300
}
# real mouse drag of the window by its title bar, ending with the window centred on the destination
function DragTo($h, $destKey, $ox, $oy) {
  $r = New-Object S+RECT; [void][S]::GetWindowRect($h, [ref]$r)
  $w = $r.R - $r.L; $hh = $r.B - $r.T
  $gx = $r.L + [int]($w * 0.62); $gy = $r.T + 16
  $d = $bounds[$destKey]
  $tx = $d.X + [int]($d.Width / 2) - [int]($w / 2) + ($gx - $r.L) + $ox
  $ty = $d.Y + [int]($d.Height / 2) - [int]($hh / 2) + ($gy - $r.T) + $oy
  [void][S]::SetCursorPos($gx, $gy); Start-Sleep -Milliseconds 250
  [S]::MouseDown(); Start-Sleep -Milliseconds 150
  $seq = @(); $last = ''
  $steps = 50
  for ($k = 1; $k -le $steps; $k++) {
    $t = $k / $steps; $e = $t * $t * (3 - 2 * $t)
    [void][S]::SetCursorPos([int]($gx + ($tx - $gx) * $e), [int]($gy + ($ty - $gy) * $e)); Start-Sleep -Milliseconds 14
    $key = WinKey $h
    if ($key -ne $last) { $seq += $key; $last = $key }
  }
  Start-Sleep -Milliseconds 150; [S]::MouseUp(); Start-Sleep -Milliseconds 700
  return $seq
}

# ---- calibrate how much a static stream changes by itself
$f1 = Frames; Start-Sleep -Milliseconds 1200; $f2 = Frames
$noise = @{}; $thr = @{}
foreach ($k in 'M1', 'M2', 'TV') { $noise[$k] = FrameDiff $f1[$k] $f2[$k]; $thr[$k] = [Math]::Max(3.0 * $noise[$k], 1.2); $f1[$k].Dispose(); $f2[$k].Dispose() }
L ("idle stream noise: M1={0:N2} M2={1:N2} TV={2:N2}   a stream counts as 'changed' above: M1={3:N2} M2={4:N2} TV={5:N2}" -f $noise.M1, $noise.M2, $noise.TV, $thr.M1, $thr.M2, $thr.TV)
Shot 'moves-00-start-laptop.png' $lap

# ---- each Notepad takes the full trip by mouse drag: laptop -> M1 -> M2 -> TV -> M2 -> M1 -> laptop -> its home display
$stations = @('M1', 'M2', 'TV', 'M2', 'M1', 'LAP')
$notes = @(@{f = 'A'; home = 'M1'; ox = -500; oy = -120}, @{f = 'B'; home = 'M2'; ox = 0; oy = 150}, @{f = 'C'; home = 'TV'; ox = 500; oy = -120})
$hops = 0; $fails = 0
foreach ($n in $notes) {
  if ($baseNp) { [void][S]::ShowWindow($baseNp, 9); Start-Sleep -Milliseconds 800 }
  $np = NewNotepad
  if ($baseNp) { [void][S]::ShowWindow($baseNp, 6); Start-Sleep -Milliseconds 600 }
  if ($np -eq [IntPtr]::Zero) { L "Notepad $($n.f): could not open a new window"; $fails++; continue }
  $script:npWindows += $np
  [void][S]::MoveWindow($np, 100 + 40 * $notes.IndexOf($n), 570, 800, 440, $true); Start-Sleep -Milliseconds 600
  $text = "Notepad $($n.f): I am being dragged across the laptop, M1, M2 and the TV." + [char]13 + "My text must survive every move."
  FocusEditor $np
  TypeVerified $np $text "$($n.f) (before moving)"
  $cur = 'LAP'; $i = 0
  foreach ($dst in ($stations + @($n.home))) {
    $i++; $hops++
    $before = Frames
    $seq = DragTo $np $dst $n.ox $n.oy
    Start-Sleep -Milliseconds 900
    $after = Frames
    $diffs = @{}; $changed = @()
    foreach ($k in 'M1', 'M2', 'TV') { $diffs[$k] = FrameDiff $before[$k] $after[$k]; if ($diffs[$k] -gt $thr[$k]) { $changed += $k }; $before[$k].Dispose(); $after[$k].Dispose() }
    $expected = @(); if ($cur -ne 'LAP') { $expected += $cur }; if ($dst -ne 'LAP') { $expected += $dst }
    $okStream = ((@($changed) | Sort-Object) -join ',') -eq ((@($expected) | Sort-Object) -join ',')
    $where = WinKey $np
    $okWin = ($where -eq $dst)
    $textNow = Norm (ReadNotepadText $np)
    $okText = ($textNow -eq (Norm $text))
    $ok = $okWin -and $okStream -and $okText
    if (-not $ok) { $fails++ }
    L ("Notepad {0} hop {1}: {2} -> {3} | window now on {4} {5} | passed through {6} | text {7} | streams changed [{8}] expected [{9}] {10} (diff M1={11:N1} M2={12:N1} TV={13:N1}) => {14}" -f `
      $n.f, $i, $cur, $dst, $where, $(if ($okWin) { 'ok' } else { 'WRONG' }), ($seq -join '>'), $(if ($okText) { 'intact' } else { 'CHANGED' }), ($changed -join ','), ($expected -join ','), $(if ($okStream) { 'ok' } else { 'MISMATCH' }), $diffs.M1, $diffs.M2, $diffs.TV, $(if ($ok) { 'PASS' } else { 'FAIL' }))
    if ($n.f -eq 'A') { Shot ("moves-A-hop{0}-to-{1}.png" -f $i, $dst) $lap }
    $cur = $dst
  }
}
Start-Sleep -Seconds 2
Shot 'moves-99-final-laptop.png' $lap
Shot 'moves-99-final-whole-desktop.png' $desk
L "SUMMARY: $hops hops, $fails failures"
foreach ($h in $script:npWindows) { L "Notepad window $h : $(CloseDiscard $h)" }
if ($baseNp) { [void][S]::ShowWindow($baseNp, 9); L "restored the user's own Notepad window" }
$script:log | Out-File (Join-Path $Out 'scenario.log') -Encoding utf8
