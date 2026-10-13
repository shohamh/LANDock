# package-components.ps1 : turns the build outputs into the release files.
#
#   dist\LANDock-app-<version>.zip      the program (LANDock.exe, self-contained)           } used by the in-app updater
#   dist\LANDock-host-<version>.zip     Apollo + vddtool + apps.json + sources notice        } (only the parts that changed
#   dist\LANDock-client-<version>.zip   Moonlight (portable build) + sources notice          }  are downloaded)
#   dist\manifest.json                  version + SHA256 of each of the three, and the release notes
#   build\stage\{app,host,client}\      the same content unpacked: the installer is built from it (build-installer.ps1)
#
# Inputs:  -AppExe       the published LANDock.exe (dotnet publish -c Release)
#          -HostBin      host\bin as produced by scripts\build-host.sh      (default: host\bin)
#          -MoonlightZip a zip built by scripts\build-moonlight.ps1; without it the official release is downloaded and
#                        SHA256-checked against scripts\versions.env
#          -NotesFile    release notes to embed in manifest.json
# A part's version is an id that changes when its CONTENT changes (the pinned source commit), not with every release,
# so an update that touches only the program does not make anybody download Apollo again.
param(
  [Parameter(Mandatory)][string]$AppExe,
  [Parameter(Mandatory)][string]$GgWaveDll,
  [string]$Version = '',
  [string]$HostBin = '',
  [string]$MoonlightZip = '',
  [string]$NotesFile = '',
  [string]$OutDir = '',
  [string]$Stage = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
if (-not $HostBin) { $HostBin = Join-Path $root 'host\bin' }
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
if (-not $Stage) { $Stage = Join-Path $root 'build\stage' }
if (-not (Test-Path $AppExe)) { throw "LANDock.exe not found: $AppExe" }
if (-not (Test-Path $GgWaveDll)) { throw "ggwave.dll not found: $GgWaveDll (scripts\build-ggwave.sh builds it)" }
foreach ($f in 'sunshine.exe', 'vddtool.exe', 'assets') { if (-not (Test-Path (Join-Path $HostBin $f))) { throw "$HostBin\$f is missing: run scripts\build-host.sh first" } }
foreach ($f in 'SudoVDA.inf', 'SudoVDA.dll', 'sudovda.cat', 'sudovda.cer') { if (-not (Test-Path (Join-Path $root "driver\sudovda\$f"))) { throw "driver\sudovda\$f is missing (the virtual display driver)" } }

function Read-Env($path) {
  $h = @{}
  Get-Content $path | Where-Object { $_ -match '^\s*[A-Z_0-9]+=' } | ForEach-Object { $k, $v = $_ -split '=', 2; $h[$k.Trim()] = $v.Trim() }
  return $h
}
$v = Read-Env (Join-Path $PSScriptRoot 'versions.env')
function Short($s) { if ($s.Length -gt 7) { $s.Substring(0, 7) } else { $s } }
function Sha256-Of($path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLower() }
function Sha256-OfText($text) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))) -replace '-', '').ToLower()
}
function Zip-Folder($folder, $zip) {
  if (Test-Path $zip) { Remove-Item $zip }
  Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $zip -CompressionLevel Optimal
}

# ---- fresh stage ------------------------------------------------------------------------------------------------------
if (Test-Path $Stage) { Remove-Item -Recurse -Force $Stage }
$app = Join-Path $Stage 'app'; $hostDir = Join-Path $Stage 'host'; $client = Join-Path $Stage 'client'
New-Item -ItemType Directory -Force -Path $app, $hostDir, $client | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---- app --------------------------------------------------------------------------------------------------------------
Copy-Item $AppExe (Join-Path $app 'LANDock.exe')
Copy-Item $GgWaveDll (Join-Path $app 'ggwave.dll')

# ---- host -------------------------------------------------------------------------------------------------------------
$apolloCommit = if (Test-Path (Join-Path $HostBin 'APOLLO-COMMIT.txt')) { (Get-Content (Join-Path $HostBin 'APOLLO-COMMIT.txt') -Raw).Trim() } else { $v.APOLLO_COMMIT }
Copy-Item $HostBin (Join-Path $hostDir 'bin') -Recurse
Remove-Item (Join-Path $hostDir 'bin\APOLLO-COMMIT.txt') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'host\apps.json') $hostDir
Copy-Item (Join-Path $root 'patches') (Join-Path $hostDir 'patches') -Recurse
Copy-Item (Join-Path $root 'LICENSE') $hostDir
Copy-Item (Join-Path $root 'NOTICE.md') $hostDir
# the virtual display driver (SudoVDA, MIT/CC0): the installer trusts its certificate and installs it (one administrator prompt)
Copy-Item (Join-Path $root 'driver\sudovda') (Join-Path $hostDir 'driver') -Recurse
$contentHash = Sha256-OfText ((Get-Content (Join-Path $root 'tools\vddtool.cpp') -Raw) + (Get-Content (Join-Path $root 'host\apps.json') -Raw) + (Sha256-Of (Join-Path $root 'driver\sudovda\SudoVDA.dll')) + (Sha256-Of (Join-Path $root 'driver\sudovda\SudoVDA.inf')))
$hostId = (Short $apolloCommit) + '.' + (Short $contentHash)
Set-Content (Join-Path $hostDir 'VERSION.txt') $hostId -NoNewline
$q = [char]96
Set-Content (Join-Path $hostDir 'SOURCE.md') @"
# Source of the binaries in this folder

${q}bin\sunshine.exe${q} and ${q}bin\assets\${q} are **Apollo** (GPL-3.0) built from source from our fork.

| | |
|---|---|
| Built from | $($v.APOLLO_REPO -replace '\.git$','') at commit ${q}$apolloCommit${q} |
| Upstream project | https://github.com/$($v.APOLLO_UPSTREAM_REPO), based on release ${q}$($v.APOLLO_UPSTREAM_BASE)${q} |
| Our changes | the commits of the ${q}$($v.APOLLO_REF)${q} branch on top of that tag; exported as ${q}patches\*.patch${q} here |
| Build | ${q}scripts/build-host.sh${q} in https://github.com/shohamh/LANDock (MSYS2 UCRT64, CMake, Ninja; Boost $($v.BOOST_VERSION) SHA256 ${q}$($v.BOOST_SHA256)${q}) |

${q}bin\vddtool.exe${q} is built from ${q}tools/vddtool.cpp${q} in the same repository, using headers from the Apollo source above.

${q}driver\${q} is **SudoVDA** (MIT and CC0 / public domain, https://github.com/SudoMaker/SudoVDA): ${q}SudoVDA.dll${q} is the signed build of SudoMaker (its signature is by sudovda@su.mk), with the ${q}SudoVDA.inf${q}, ${q}sudovda.cat${q} and ${q}sudovda.cer${q} that Apollo ships.

Component id: $hostId   (LANDock release $Version)
"@

# ---- client -----------------------------------------------------------------------------------------------------------
$fromSource = [bool]$MoonlightZip
if ($MoonlightZip) {
  if (-not (Test-Path $MoonlightZip)) { throw "Moonlight zip not found: $MoonlightZip" }
  $sourceFile = [IO.Path]::ChangeExtension($MoonlightZip, '.source.txt')
  $built = if (Test-Path $sourceFile) { (Get-Content $sourceFile -Raw).Trim() } else { 'built from source (details unavailable)' }
  $mlSha = Sha256-Of $MoonlightZip
  $sourceText = @"
Moonlight (moonlight-qt, GPL-3.0) built from source by the LANDock release workflow.
$built
SHA256 of the Moonlight zip : $mlSha
Fork     : $($v.MOONLIGHT_REPO -replace '\.git$','')
Upstream : https://github.com/$($v.MOONLIGHT_UPSTREAM_REPO) (base $($v.MOONLIGHT_UPSTREAM_BASE))
"@
  $clientId = "$($v.MOONLIGHT_UPSTREAM_BASE.TrimStart('v'))-landock.$(Short $v.MOONLIGHT_COMMIT)"
} else {
  $cache = Join-Path $root 'build\downloads'
  New-Item -ItemType Directory -Force -Path $cache | Out-Null
  $MoonlightZip = Join-Path $cache ([IO.Path]::GetFileName($v.MOONLIGHT_URL))
  if (-not (Test-Path $MoonlightZip)) { Write-Host "downloading Moonlight $($v.MOONLIGHT_VERSION) ..."; Invoke-WebRequest -Uri $v.MOONLIGHT_URL -OutFile $MoonlightZip -UseBasicParsing }
  $mlSha = Sha256-Of $MoonlightZip
  if ($mlSha -ne $v.MOONLIGHT_SHA256.ToLower()) { throw "Moonlight zip SHA256 is $mlSha, expected $($v.MOONLIGHT_SHA256)" }
  $sourceText = @"
Moonlight (moonlight-qt $($v.MOONLIGHT_VERSION), GPL-3.0): the unmodified official release.
Download : $($v.MOONLIGHT_URL)
SHA256   : $($v.MOONLIGHT_SHA256)
Source   : https://github.com/moonlight-stream/moonlight-qt (tag v$($v.MOONLIGHT_VERSION))
"@
  $clientId = "$($v.MOONLIGHT_VERSION)-official"
}
Write-Host "Moonlight zip: $(Split-Path -Leaf $MoonlightZip)  sha256 $mlSha  ($(if ($fromSource) { 'built from our fork' } else { 'official release' }))"
$mlDir = Join-Path $client 'moonlight'
New-Item -ItemType Directory -Force -Path $mlDir | Out-Null
Expand-Archive -Path $MoonlightZip -DestinationPath $mlDir -Force
# the zip may hold everything inside one top-level folder: lift it up so Moonlight.exe sits directly in moonlight\
$top = Get-ChildItem $mlDir
if ($top.Count -eq 1 -and $top[0].PSIsContainer -and -not (Test-Path (Join-Path $mlDir 'Moonlight.exe'))) {
  Get-ChildItem $top[0].FullName | Move-Item -Destination $mlDir
  Remove-Item $top[0].FullName -Recurse
}
if (-not (Test-Path (Join-Path $mlDir 'Moonlight.exe'))) { throw 'Moonlight.exe not found in the Moonlight zip' }
Copy-Item (Join-Path $root 'LICENSE') $client
Copy-Item (Join-Path $root 'NOTICE.md') $client
Set-Content (Join-Path $client 'VERSION.txt') $clientId -NoNewline
Set-Content (Join-Path $client 'MOONLIGHT-SOURCE.txt') $sourceText

# ---- zips + manifest --------------------------------------------------------------------------------------------------
$files = @{
  app    = @{ zip = "LANDock-app-$Version.zip";    dir = $app;     version = $Version }
  host   = @{ zip = "LANDock-host-$Version.zip";   dir = $hostDir; version = $hostId }
  client = @{ zip = "LANDock-client-$Version.zip"; dir = $client;  version = $clientId }
}
$components = [ordered]@{}
foreach ($name in 'app', 'host', 'client') {
  $f = $files[$name]
  $zip = Join-Path $OutDir $f.zip
  Zip-Folder $f.dir $zip
  $item = Get-Item $zip
  $components[$name] = [ordered]@{ version = $f.version; file = $f.zip; sha256 = (Sha256-Of $zip); size = $item.Length }
  Write-Host ("{0,-7} {1,-34} {2,7:N1} MB  id {3}" -f $name, $f.zip, ($item.Length / 1MB), $f.version)
}
$notes = if ($NotesFile -and (Test-Path $NotesFile)) { (Get-Content $NotesFile -Raw) } else { '' }
if ($notes.Length -gt 6000) { $notes = $notes.Substring(0, 6000) + "`n..." }
$manifest = [ordered]@{ schema = 1; version = $Version; tag = "v$Version"; notes = $notes; components = $components }
$manifestPath = Join-Path $OutDir 'manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Host "wrote $manifestPath"
