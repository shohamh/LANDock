# assemble-install.ps1 : puts the staged parts (scripts\package-components.ps1 -> build\stage\{app,host,client}) together the
# way the installer lays them out, for running and testing LANDock without installing it.
#   scripts\assemble-install.ps1 [-Dest build\run]
param([string]$Dest = '', [string]$Stage = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dest) { $Dest = Join-Path $root 'build\run' }
if (-not $Stage) { $Stage = Join-Path $root 'build\stage' }
foreach ($p in 'app', 'host', 'client') { if (-not (Test-Path (Join-Path $Stage $p))) { throw "$Stage\$p is missing: run scripts\package-components.ps1 first" } }
if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
Copy-Item (Join-Path $Stage 'app\*') $Dest -Recurse
Copy-Item (Join-Path $Stage 'host') (Join-Path $Dest 'host') -Recurse
Copy-Item (Join-Path $Stage 'client') (Join-Path $Dest 'client') -Recurse
Write-Host "assembled $Dest"
