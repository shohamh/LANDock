# build-installer.ps1 : builds dist\LANDock-Setup-<version>.exe with Inno Setup from the staged parts.
#   Run scripts\package-components.ps1 first (it fills build\stage\{app,host,client}).
#   Inno Setup is found on PATH, in its usual folders, or (on CI) installed with Chocolatey.
param(
  [string]$Version = '',
  [string]$Stage = '',
  [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
if (-not $Stage) { $Stage = Join-Path $root 'build\stage' }
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
foreach ($p in 'app', 'host', 'client') { if (-not (Test-Path (Join-Path $Stage $p))) { throw "$Stage\$p is missing: run scripts\package-components.ps1 first" } }

function Find-Iscc {
  $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  foreach ($base in $env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs')) {
    if (-not $base) { continue }
    $p = Join-Path $base 'Inno Setup 6\ISCC.exe'
    if (Test-Path $p) { return $p }
  }
  return $null
}
$iscc = Find-Iscc
if (-not $iscc -and (Get-Command choco -ErrorAction SilentlyContinue)) {
  Write-Host 'installing Inno Setup with Chocolatey ...'
  choco install innosetup -y --no-progress | Out-Host
  $iscc = Find-Iscc
}
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup).' }
Write-Host "using $iscc"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$numeric = if ($Version -match '^(\d+\.\d+\.\d+)') { $Matches[1] } else { '0.0.0' }   # file version info must be numeric; 1.2.3-rc1 -> 1.2.3
& $iscc "/DAppVersion=$Version" "/DNumericVersion=$numeric" "/DStageDir=$Stage" "/DRepoRoot=$root" "/O$OutDir" (Join-Path $root 'installer\LANDock.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
$setup = Join-Path $OutDir "LANDock-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "expected $setup was not produced" }
Write-Host ("built {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
