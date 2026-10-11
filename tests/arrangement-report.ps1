# arrangement-report.ps1 : the desktop's side of tests/run-arrangement.sh, for the parts that need PowerShell (DPAPI and HTTPS).
#   -Mode Start : sends the report of the screens below to the laptop and starts a Moonlight stream for each display it agrees to.
#   -Mode Stop  : quits those streams.
# The report (what a desktop with these screens would send; every screen is used and touches another, so the arrangement is one piece):
#   A  landscape 1920x1080 at (0,0),      primary  -> M1
#   B  portrait  1080x1920 at (1920,0),   right of A, taller -> M2
#   C  landscape 1920x1080 at (0,-1080),  above A  -> TV
param(
  [Parameter(Mandatory)][ValidateSet('Start', 'Stop')][string]$Mode,
  [Parameter(Mandatory)][string]$DataDir,
  [int]$ControlPort = 49900
)
$ErrorActionPreference = 'Stop'

if ($Mode -eq 'Stop') {
  Get-Process Moonlight -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*arrangement-run*" } | Stop-Process -Force
  return
}

Add-Type -AssemblyName System.Security
$settings = Get-Content (Join-Path $DataDir 'settings.json') -Raw | ConvertFrom-Json
$h = $settings.Client.Hosts[0]
$token = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($h.TokenProtected), $null, 'CurrentUser'))

[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$body = @{
  streams = @(
    @{ name = 'M1'; width = 1920; height = 1080; fps = 60; screenId = 'FAKE_A' },
    @{ name = 'M2'; width = 1080; height = 1920; fps = 60; screenId = 'FAKE_B' },
    @{ name = 'TV'; width = 1920; height = 1080; fps = 60; screenId = 'FAKE_C' }
  )
  monitors = @(
    @{ id = 'FAKE_A'; x = 0;    y = 0;     width = 1920; height = 1080; rotation = 0;  primary = $true },
    @{ id = 'FAKE_B'; x = 1920; y = 0;     width = 1080; height = 1920; rotation = 90; primary = $false },
    @{ id = 'FAKE_C'; x = 0;    y = -1080; width = 1920; height = 1080; rotation = 0;  primary = $false }
  )
  ultraId = ''
} | ConvertTo-Json -Depth 6
$agreed = Invoke-RestMethod -Uri "https://127.0.0.1:$ControlPort/api/session" -Method Post -Body $body -ContentType 'application/json' -Headers @{ Authorization = "Bearer $token" }
$agreed | ForEach-Object { Write-Host ("agreed {0}: port {1} {2}x{3}" -f $_.name, $_.port, $_.width, $_.height) }

$dir = Join-Path $DataDir "moonlight"
foreach ($a in $agreed) {
  $folder = $dir
  Start-Process -FilePath (Join-Path $folder 'Moonlight.exe') -WorkingDirectory $folder -WindowStyle Hidden -ArgumentList "stream 127.0.0.1:$($a.port) VD --display-mode windowed --absolute-mouse --resolution $($a.width)x$($a.height) --fps 60 --no-audio-on-host" | Out-Null
  Write-Host "started Moonlight for $($a.name)"
}
