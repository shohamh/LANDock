# Closes only Notepad tabs whose names start with the given prefixes (never the user's own tabs).
# Safety: before every Ctrl+W it re-reads which tab is selected and refuses unless that tab matches a prefix.
param([string]$PrefixList)
$Prefixes = $PrefixList -split ','
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class K {
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct IU { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public IU u; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int sz);
  public static void Click() { var i = new INPUT[2]; i[0].type = 0; i[0].u.mi.flags = 0x0002; i[1].type = 0; i[1].u.mi.flags = 0x0004; SendInput(2, i, Marshal.SizeOf(typeof(INPUT))); }
  public static void CtrlW() { var i = new INPUT[4];
    i[0].type = 1; i[0].u.ki.vk = 0x11; i[1].type = 1; i[1].u.ki.vk = 0x57; i[2].type = 1; i[2].u.ki.vk = 0x57; i[2].u.ki.flags = 2; i[3].type = 1; i[3].u.ki.vk = 0x11; i[3].u.ki.flags = 2;
    SendInput(4, i, Marshal.SizeOf(typeof(INPUT))); }
}
'@
[void][K]::SetProcessDpiAwarenessContext([IntPtr](-4))
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$root = $AE::RootElement
function Win { $root.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Notepad'))) }
function Tabs($w) { $w.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::TabItem))) }
function Matches($name) { foreach ($p in $Prefixes) { if ($name -like "$p*") { return $true } }; return $false }
$btnCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
for ($guard = 0; $guard -lt 12; $guard++) {
  $w = Win; if (-not $w) { 'no notepad window'; break }
  $mine = Tabs $w | Where-Object { Matches $_.Current.Name } | Select-Object -First 1
  if (-not $mine) { break }
  $name = $mine.Current.Name
  $mine.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 400
  # click into the editor (lower middle of the window) so Notepad has keyboard focus
  $r = $w.Current.BoundingRectangle
  [void][K]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height * 0.65)); Start-Sleep -Milliseconds 250; [K]::Click(); Start-Sleep -Milliseconds 300
  # re-verify the selected tab is one of mine before sending the shortcut
  $w = Win
  $sel = Tabs $w | Where-Object { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } | Select-Object -First 1
  if (-not $sel -or -not (Matches $sel.Current.Name)) { "refusing: selected tab is '$($sel.Current.Name)', not one of mine"; break }
  [K]::CtrlW(); Start-Sleep -Milliseconds 800
  $w = Win
  $dont = $w.FindAll($TS::Descendants, $btnCond) | Where-Object { $_.Current.Name -match "^Don.?t save$" } | Select-Object -First 1
  if ($dont) { $dont.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 600; "closed without saving: $name" } else { "closed: $name" }
}
"remaining tabs:"
$w = Win
foreach ($t in (Tabs $w)) { "   $($t.Current.Name)" }
