# layout-variations.ps1 : the laptop's screen placement against several desktop screen setups, on the real laptop (this PC).
# A fake desktop pairs with the laptop and reports the screens of each setup; the laptop creates a virtual display for each screen and
# lays them out. For each setup the five placements are set (per desktop, in its record), and the positions Windows shows are compared
# with the positions the rules give (computed here, independently of the program).
# Needs the program installed. The real tray is stopped during the run; the fake desktop is removed at the end.
param([string]$Only = '')
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$app = "$env:LOCALAPPDATA\Programs\LANDock\LANDock.exe"
$vdd = "$env:LOCALAPPDATA\Programs\LANDock\host\bin\vddtool.exe"
$moon = "$env:LOCALAPPDATA\LANDock\moonlight\Moonlight.exe"
$data = "$env:LOCALAPPDATA\LANDock"
$out = Join-Path $root 'build\layout-variations'
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out 'run.log'
$pin = $null
$results = New-Object System.Collections.ArrayList
$fakeName = 'fake-desk'
$fakeUltra = 'FAKE0000000000AB'

function Log([string]$m) { $line = "[$(Get-Date -Format HH:mm:ss)] $m"; Add-Content -Path $log -Value $line; Write-Output $line }

# the instance folder of each Moonlight port, and how many client connections its log has shown so far
$keyOf = @{ 49989 = 'c'; 50989 = 'd'; 51989 = 'e'; 52989 = 'f'; 53989 = 'g'; 54989 = 'h' }
function Count-Connected([int]$port) {
    $f = "$data\host\$($keyOf[$port])\apollo.log"
    if (-not (Test-Path $f)) { return 0 }
    return @(Select-String -Path $f -Pattern 'CLIENT CONNECTED').Count
}

function Start-Stream($a) {
    return Start-Process -FilePath $moon -ArgumentList 'stream', "127.0.0.1:$($a.port)", 'VD', '--display-mode', 'windowed', '--absolute-mouse', '--resolution', "$($a.width)x$($a.height)", '--fps', '60', '--no-audio-on-host' -WorkingDirectory (Split-Path $moon) -WindowStyle Hidden -PassThru
}

# Moonlight's pair keeps running after the pairing succeeded, so it is watched (as the program does): once its output reaches the last
# step ("phrase=pairchallenge"), the pairing is done and Moonlight is ended. Ending it earlier loses the pairing.
function Pair-Display([string]$addr, [string]$name) {
    $pOut = Join-Path $out "pair-$name.out"
    $p = Start-Process -FilePath $moon -ArgumentList 'pair', $addr, '--pin', $pin -WorkingDirectory (Split-Path $moon) -WindowStyle Hidden -PassThru -RedirectStandardOutput $pOut -RedirectStandardError "$pOut.err"
    for ($t = 0; $t -lt 45 -and -not $p.HasExited; $t++) {
        Start-Sleep -Seconds 1
        $text = Get-Content $pOut, "$pOut.err" -Raw -ErrorAction SilentlyContinue | Out-String
        if ($text -match 'phrase=pairchallenge') { break }
    }
    Start-Sleep -Seconds 2
    try { if (-not $p.HasExited) { $p.Kill() } } catch {}
}

function Get-Screens {
    $res = @{}
    foreach ($l in (& $vdd screens)) {
        if ($l -match '^\\\\\.\\(DISPLAY\d+) pos=(-?\d+),(-?\d+) size=(\d+)x(\d+).*?(primary)?\s*$') {
            $res[$matches[1]] = [pscustomobject]@{ Name = $matches[1]; X = [int]$matches[2]; Y = [int]$matches[3]; W = [int]$matches[4]; H = [int]$matches[5]; Primary = [bool]$matches[6] }
        }
    }
    return $res
}

function Post-Json([string]$path, $body, [string]$token = '') {
    $file = Join-Path $out 'body.json'
    [System.IO.File]::WriteAllText($file, ($body | ConvertTo-Json -Depth 10 -Compress), (New-Object System.Text.UTF8Encoding($false)))
    $curl = @('-sk', '-X', 'POST', "https://127.0.0.1:49900$path", '-H', 'Content-Type: application/json', '--data-binary', "@$file")
    if ($token -ne '') { $curl += @('-H', "Authorization: Bearer $token") }
    $text = & curl.exe @curl
    return ($text | ConvertFrom-Json)
}

function Set-FakePlacement([string]$placement) {
    $p = "$data\host\paired-desktops.json"
    # Windows PowerShell 5.1: ConvertFrom-Json returns a JSON array as one object, and ConvertTo-Json needs the array cast
    $list = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($p).Trim([char]0xFEFF))
    foreach ($d in $list) { if ($d.Name -eq $fakeName) { $d | Add-Member -NotePropertyName Placement -NotePropertyValue $placement -Force } }
    [System.IO.File]::WriteAllText($p, (ConvertTo-Json -InputObject ([object[]]$list) -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
}

# The rules, written out here independently of the program. desk: the desktop screens (X, Y, W, H); lap: the laptop's screen.
function Expected-Positions($desk, $lap, [string]$placement) {
    $minX = ($desk | Measure-Object -Property X -Minimum).Minimum
    $minY = ($desk | Measure-Object -Property Y -Minimum).Minimum
    $maxR = ($desk | ForEach-Object { $_.X + $_.W } | Measure-Object -Maximum).Maximum
    $maxB = ($desk | ForEach-Object { $_.Y + $_.H } | Measure-Object -Maximum).Maximum
    $gw = $maxR - $minX; $gh = $maxB - $minY
    switch ($placement) {
        'left'           { $ox = $lap.W;                                   $oy = 0 }
        'right'          { $ox = -$gw;                                     $oy = 0 }
        'below-centered' { $ox = [math]::Truncate(($lap.W - $gw) / 2);     $oy = -$gh }
        'below-right'    { $ox = $lap.W - $gw;                             $oy = -$gh }
        'below-left'     { $ox = 0;                                        $oy = -$gh }
    }
    # below the desktop: its lowest screen over the laptop's width touches the laptop's top edge (see LaptopLayout.Compute)
    if ($placement -like 'below-*') {
        $over = @($desk | Where-Object { ($ox + ($_.X - $minX)) -lt $lap.W -and ($ox + ($_.X - $minX) + $_.W) -gt 0 })
        if ($over.Count -gt 0) { $oy = -(($over | ForEach-Object { $_.Y + $_.H - $minY }) | Measure-Object -Maximum).Maximum }
    }
    $res = @{}
    foreach ($s in $desk) { $res[$s.Id] = @(($ox + ($s.X - $minX)), ($oy + ($s.Y - $minY))) }
    return $res
}

$setups = @(
    @{ Name = '1 screen';                     Screens = @(
        @{ Id = 'S1'; X = 0; Y = 0; W = 1920; H = 1080; P = $true }) },
    @{ Name = '2 landscape side by side';     Screens = @(
        @{ Id = 'S1'; X = 0; Y = 0; W = 1920; H = 1080; P = $true },
        @{ Id = 'S2'; X = 1920; Y = 0; W = 1920; H = 1080; P = $false }) },
    @{ Name = 'landscape + portrait right';   Screens = @(
        @{ Id = 'S1'; X = 0; Y = 0; W = 1920; H = 1080; P = $true },
        @{ Id = 'S2'; X = 1920; Y = 0; W = 1080; H = 1920; P = $false }) },
    @{ Name = 'landscape above and below';    Screens = @(
        @{ Id = 'S1'; X = 0; Y = 0; W = 1920; H = 1080; P = $true },
        @{ Id = 'S2'; X = 0; Y = -1080; W = 1920; H = 1080; P = $false }) },
    @{ Name = 'portrait primary, landscape left'; Screens = @(
        @{ Id = 'S1'; X = 0; Y = 0; W = 1080; H = 1920; P = $true },
        @{ Id = 'S2'; X = -1920; Y = 0; W = 1920; H = 1080; P = $false }) },
    @{ Name = 'three screens, offsets';       Screens = @(
        @{ Id = 'S1'; X = 0; Y = 0; W = 1920; H = 1080; P = $true },
        @{ Id = 'S2'; X = 1920; Y = -300; W = 1280; H = 1024; P = $false },
        @{ Id = 'S3'; X = 0; Y = 1080; W = 1920; H = 1080; P = $false }) }
)
$placements = @('left', 'right', 'below-centered', 'below-right', 'below-left')

try {
    Log 'stopping the real tray (its notice and auto connect would interfere)'
    Get-Process LANDock -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*Programs\LANDock*' } | Stop-Process -Force
    Start-Sleep -Seconds 3

    Log 'host up (command line)'
    & $app host down | Out-Null
    Start-Sleep -Seconds 5
    $pinFile = Join-Path $out 'pin.txt'
    $upLog = Join-Path $out 'host-up.log'
    $hostProc = Start-Process -FilePath $app -ArgumentList 'host', 'up', '--pin-file', $pinFile -RedirectStandardOutput $upLog -RedirectStandardError (Join-Path $out 'host-up.err') -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 90; $i++) { if ((Test-Path $upLog) -and (Select-String -Path $upLog -Pattern 'Host is ready' -Quiet)) { break }; Start-Sleep -Seconds 2 }
    if (-not (Select-String -Path $upLog -Pattern 'Host is ready' -Quiet)) { throw 'the host did not come up' }
    $pin = (Get-Content $pinFile -Raw).Trim()
    Log "host is ready (PIN $pin)"

    Log 'pairing the fake desktop over the control port'
    $pair = Post-Json '/api/pair' @{ pin = $pin; client = $fakeName; ultraId = $fakeUltra }
    $token = $pair.token
    if (-not $token) { throw "pairing failed: $($pair | ConvertTo-Json -Compress)" }
    Log 'paired'

    $lap = (Get-Screens).Values | Where-Object { $_.Primary } | Select-Object -First 1
    Log "laptop screen: $($lap.Name) $($lap.W)x$($lap.H) at $($lap.X),$($lap.Y)"

    foreach ($setup in $setups) {
        if ($Only -ne '' -and $setup.Name -notlike "*$Only*") { continue }
        Log "=== setup: $($setup.Name)"
        $desk = @($setup.Screens | ForEach-Object { [pscustomobject]@{ Id = $_.Id; X = $_.X; Y = $_.Y; W = $_.W; H = $_.H; P = $_.P } })
        $streams = @()
        $names = @('M1', 'M2', 'TV', 'M4', 'M5', 'M6')
        for ($i = 0; $i -lt $desk.Count; $i++) {
            $streams += @{ name = $names[$i]; width = $desk[$i].W; height = $desk[$i].H; fps = 60; screenId = $desk[$i].Id }
        }
        $monitors = @($desk | ForEach-Object { @{ id = $_.Id; x = $_.X; y = $_.Y; width = $_.W; height = $_.H; rotation = 0; primary = $_.P } })
        $agreed = Post-Json '/api/session' @{ streams = $streams; ultraId = $fakeUltra; monitors = $monitors } $token
        if (-not $agreed -or -not ($agreed | Select-Object -First 1).port) { Log ("session gave no streams: " + ($agreed | ConvertTo-Json -Compress)); continue }
        Log ("session agreed: " +(($agreed | ForEach-Object { "$($_.name)=port $($_.port) $($_.width)x$($_.height)" }) -join ', '))

        $connected = @{}
        foreach ($a in $agreed) { $connected[$a.name] = Count-Connected ([int]$a.port) }
        # The streams start at once. A stream that does not connect is not paired yet: its display is paired, then its stream starts
        # again. (Asking Moonlight "list" first, as this used to, said "not paired" for paired displays too, and each pairing then waited
        # for its full timeout in every setup.)
        $mlProcs = @{}
        foreach ($a in $agreed) { $mlProcs[$a.name] = Start-Stream $a }
        $pending = @($agreed)
        for ($round = 1; $round -le 2 -and $pending.Count -gt 0; $round++) {
            for ($i = 0; $i -lt $(if ($round -eq 1) { 20 } else { 60 }); $i++) {
                $pending = @($pending | Where-Object { (Count-Connected ([int]$_.port)) -eq $connected[$_.name] })
                if ($pending.Count -eq 0) { break }
                Start-Sleep -Seconds 1
            }
            if ($pending.Count -eq 0 -or $round -eq 2) { break }
            foreach ($a in $pending) {
                Log "  $($a.name) did not connect: pairing its display, then its stream starts again"
                Pair-Display "127.0.0.1:$($a.port)" $a.name
                try { $old = $mlProcs[$a.name]; if (-not $old.HasExited) { Stop-Process -Id $old.Id -Force } } catch {}
                $mlProcs[$a.name] = Start-Stream $a
            }
        }
        if ($pending.Count -gt 0) { Log ("  not connected: " + (($pending | ForEach-Object { $_.name }) -join ', ')) }

        # the streams are up when each instance logs a new client connection (counted from before they started); the displays they
        # show are then on the screen. Displays left from the setup before are not counted.
        $ready = $false
        for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
            Start-Sleep -Seconds 1
            $ready = $true
            foreach ($a in $agreed) { if ((Count-Connected ([int]$a.port)) -eq $connected[$a.name]) { $ready = $false } }
        }
        # which virtual display shows which stream: the instance's log names the display it created
        $display = @{}
        for ($i = 0; $i -lt 30; $i++) {
            $display = @{}
            foreach ($a in $agreed) {
                $logFile = "$data\host\$($keyOf[[int]$a.port])\apollo.log"
                $created = Select-String -Path $logFile -Pattern 'Virtual Display created at \\\\\.\\(DISPLAY\d+)' | Select-Object -Last 1
                if ($created) { $display[$a.name] = $created.Matches[0].Groups[1].Value }
            }
            $scr = Get-Screens
            if ($display.Count -eq $agreed.Count -and @($display.Values | Where-Object { -not $scr.ContainsKey($_) }).Count -eq 0) { break }
            Start-Sleep -Seconds 1
        }
        Log ("displays: " + (($display.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

        foreach ($pl in $placements) {
            Set-FakePlacement $pl
            $exp = Expected-Positions $desk $lap $pl
            # Windows settles a move a moment after it is applied: read again for up to 20 s until every display is where it should be
            $waited = 0
            do {
                Start-Sleep -Seconds 1; $waited += 1
                $scr = Get-Screens
                $ok = $true
                for ($i = 0; $i -lt $desk.Count; $i++) {
                    $dn = $display[$names[$i]]
                    if (-not $dn -or -not $scr.ContainsKey($dn)) { $ok = $false; continue }
                    $want = $exp[$desk[$i].Id]
                    if ($scr[$dn].X -ne $want[0] -or $scr[$dn].Y -ne $want[1]) { $ok = $false }
                }
            } while (-not $ok -and $waited -lt 20)
            $detail = @()
            for ($i = 0; $i -lt $desk.Count; $i++) {
                $sid = $desk[$i].Id
                $sname = $names[$i]
                $dn = $display[$sname]
                if (-not $dn -or -not $scr.ContainsKey($dn)) { $detail += "$sname not on screen"; continue }
                $actual = $scr[$dn]
                $want = $exp[$sid]
                $detail += "$sname($sid) $dn at $($actual.X),$($actual.Y) size $($actual.W)x$($actual.H) want $($want[0]),$($want[1])"
            }
            $detail += "settled after $waited s"
            $verdict = if ($ok) { 'PASS' } else { 'FAIL' }
            Log ("[$verdict] $($setup.Name) / $pl : " + ($detail -join '; '))
            [void]$results.Add([pscustomobject]@{ Setup = $setup.Name; Placement = $pl; Verdict = $verdict; Detail = ($detail -join '; ') })
        }

        Log 'stopping the streams of this setup'
        foreach ($m in $mlProcs.Values) { try { if (-not $m.HasExited) { Stop-Process -Id $m.Id -Force } } catch {} }
        # the displays stay until the program takes them down (after about a minute without a desktop); the next setup does not wait for that
        Start-Sleep -Seconds 2
    }
}
finally {
    Log 'cleanup: streams, fake desktop, host'
    Get-Process Moonlight -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*AppData\Local\LANDock*' } | Stop-Process -Force -ErrorAction SilentlyContinue
    try { [System.IO.File]::WriteAllText("$data\host\paired-desktops.json", '[]', (New-Object System.Text.UTF8Encoding($false))) } catch {}
    & $app host down | Out-Null
    Start-Sleep -Seconds 5
    $results | Export-Csv -Path (Join-Path $out 'results.csv') -NoTypeInformation
    $pass = @($results | Where-Object { $_.Verdict -eq 'PASS' }).Count
    Log "summary: $pass of $($results.Count) checks passed"
}
