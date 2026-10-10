# build-moonlight.ps1 : build Moonlight (our fork of moonlight-qt) from source and make a portable x64 zip.
#
# Needs Windows with Visual Studio 2022 (C++ workload) and Qt for MSVC 2022 x64 (the version in versions.env,
# MOONLIGHT_QT_VERSION). Pass the Qt bin folder, e.g.  -QtBin C:\Qt\6.12.0\msvc2022_64\bin
# GitHub Actions runs the same script (.github\workflows\build.yml); the steps are Moonlight's own CI steps for Windows.
#
# Source: the fork and commit pinned in scripts\versions.env. To build something else (for example after pulling
# upstream changes) pass -Repo / -Ref / -Commit, or set -Commit '' to build the tip of -Ref.
param(
  [Parameter(Mandatory = $true)][string]$QtBin,
  [string]$OutDir = '',
  [string]$Repo = '',
  [string]$Ref = '',
  [string]$Commit = '<pinned>'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
$work = Join-Path $root 'build'
$src = Join-Path $work 'moonlight'

function Read-Env($path) {
  $h = @{}
  Get-Content $path | Where-Object { $_ -match '^\s*[A-Z_0-9]+=' } | ForEach-Object { $k, $v = $_ -split '=', 2; $h[$k.Trim()] = $v.Trim() }
  return $h
}
$v = Read-Env (Join-Path $PSScriptRoot 'versions.env')
if (-not $Repo) { $Repo = $v.MOONLIGHT_REPO }
if (-not $Ref) { $Ref = $v.MOONLIGHT_REF }
if ($Commit -eq '<pinned>') { $Commit = $v.MOONLIGHT_COMMIT }
$want = if ($Commit) { $Commit } else { $Ref }

function Step($m) { Write-Host "[build-moonlight] $m" }
function Run($exe, [string[]]$argv) { & $exe @argv; if ($LASTEXITCODE -ne 0) { throw "$exe $($argv -join ' ') failed ($LASTEXITCODE)" } }

Step "1/4 Moonlight from $Repo @ $want"
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
New-Item -ItemType Directory -Force -Path $src | Out-Null
Run git @('-C', $src, 'init', '-q')
Run git @('-C', $src, 'remote', 'add', 'origin', $Repo)
Run git @('-C', $src, 'fetch', '-q', '--depth', '1', 'origin', $want)
Run git @('-C', $src, 'checkout', '-q', 'FETCH_HEAD')
$head = (git -C $src rev-parse HEAD).Trim()
if ($Commit -and $head -ne $Commit) { throw "fetched $head, expected $Commit" }
Step "  at $head"
Run git @('-C', $src, 'submodule', 'update', '--init', '--recursive', '--depth', '1', '-q')

Push-Location $src
try {
  Step "2/4 prebuilt dependencies (setup-deps.ps1)"
  Run powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '.\setup-deps.ps1')

  Step "3/4 build x64 (scripts\build-arch.bat Release x64)"
  $env:PATH = "$QtBin;$env:PATH"
  & cmd /c "scripts\build-arch.bat Release x64"
  if ($LASTEXITCODE -ne 0) { throw "build-arch.bat failed ($LASTEXITCODE)" }
} finally { Pop-Location }

Step "4/4 portable zip"
$deploy = Join-Path $src 'build\deploy-x64-release'
if (-not (Test-Path (Join-Path $deploy 'Moonlight.exe'))) { throw "no Moonlight.exe in $deploy" }
$short = $head.Substring(0, 7)
$name = "MoonlightPortable-x64-$($v.MOONLIGHT_UPSTREAM_BASE.TrimStart('v'))-landock.$short"
$stage = Join-Path ([IO.Path]::GetTempPath()) ("landock-ml-" + [guid]::NewGuid().ToString('N'))
Copy-Item $deploy $stage -Recurse
New-Item -ItemType File -Force -Path (Join-Path $stage 'portable.dat') | Out-Null    # portable mode: settings next to the exe
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -Recurse -Force $stage
Set-Content (Join-Path $OutDir "$name.source.txt") "Moonlight built from $Repo`ncommit $head`n(upstream base $($v.MOONLIGHT_UPSTREAM_BASE), $($v.MOONLIGHT_UPSTREAM_REPO))"
Step "done: $zip  sha256 $((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())"
