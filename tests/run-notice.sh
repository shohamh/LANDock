#!/bin/bash
# run-notice.sh : the laptop's notice for a recognized desktop, on ONE machine (host and desktop on this PC, loopback).
# Uses the installed program with its own test data folder (build/notice-test), so the user's settings are not touched.
# Steps: pair this PC with a test host (host up, add-host, host down), then start the tray for the test data with the host set to
# start with it (the way the real app runs: the notice lives in the process that runs the host). The desktop shows its presence
# on the LAN, the laptop recognizes it and the notice comes up. A screenshot is taken. Sound stays off: no ultrasound here.
# Needs the program installed. Stops everything at the end.
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
W="$ROOT/build/notice-test"
D="$W/data"
APP="${LANDOCK_APP:-$LOCALAPPDATA/Programs/LANDock/LANDock.exe}"
SHOT="$W/notice.png"
export LANDOCK_DATA_DIR="$(cygpath -m "$D")"
log() { echo "[$(date +%T)] $*"; }

cleanup() {
  log "== stopping"
  pid=$(cat "$W/tray.pid" 2>/dev/null)
  [ -n "$pid" ] && taskkill //PID "$pid" //F > /dev/null 2>&1
  "$APP" host down > /dev/null 2>&1
  sleep 3
  "$APP" client remove-host notice-test > /dev/null 2>&1
  log "== done"
}
trap cleanup EXIT

# leftovers of an earlier run
pid=$(cat "$W/tray.pid" 2>/dev/null); [ -n "$pid" ] && taskkill //PID "$pid" //F > /dev/null 2>&1
rm -rf "$W"; mkdir -p "$W"

log "== host up (test data) and pairing with this PC as a desktop"
(nohup "$APP" host up --pin-file "$(cygpath -m "$W")/pin.txt" > "$W/host-up.log" 2>&1 < /dev/null &)
for i in $(seq 1 90); do grep -q "Host is ready" "$W/host-up.log" 2>/dev/null && break; sleep 2; done
grep -q "Host is ready" "$W/host-up.log" || { log "host did not come up"; tail -5 "$W/host-up.log"; exit 2; }
PIN=$(tr -d '[:space:]' < "$W/pin.txt")
CPORT=$(grep -o "Control port (desktop PCs connect here): [0-9]*" "$W/host-up.log" | grep -o "[0-9]*$")
"$APP" client add-host 127.0.0.1 --port "$CPORT" --pin "$PIN" --trust --name notice-test > "$W/pair.log" 2>&1; echo "  add-host exit $?"
"$APP" host down > /dev/null 2>&1
for i in $(seq 1 60); do netstat -ano | grep LISTENING | grep -q ":49900 " || break; sleep 2; done
sleep 3

log "== tray for the test data, host set to start with it"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "\$f='$(cygpath -w "$D")\\settings.json'; \$s = Get-Content \$f -Raw | ConvertFrom-Json; \$s.Host.AutoStart = \$true; \$s | ConvertTo-Json -Depth 30 | Set-Content -Path \$f -Encoding utf8"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "\$env:LANDOCK_DATA_DIR='$(cygpath -w "$D")'; \$p = Start-Process -FilePath '$(cygpath -w "$APP")' -ArgumentList '--tray' -WindowStyle Hidden -PassThru; \$p.Id | Set-Content -Path '$(cygpath -w "$W")\\tray.pid'"
for i in $(seq 1 60); do grep -q "is here" "$D/logs/landock.log" 2>/dev/null && break; sleep 2; done
log "== laptop log"
grep -E "host ready|showing this desktop|is here|browsing|ERROR|notice" "$D/logs/landock.log" | cut -c1-200 | tail -10

sleep 3
log "== windows of the test tray (the notice is one of them)"
TRAYPID=$(cat "$W/tray.pid" 2>/dev/null)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/tests/windows.ps1")" -ProcessId "$TRAYPID" -SaveDir "$(cygpath -w "$W")" 2>&1 | tr -d '\r' | cut -c1-240 | tee "$W/windows.txt"

log "== click the screen in the notice (turns it off for the laptop)"
PSHOT="$W/notice-before.png"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/tests/screenshot.ps1")" -Path "$(cygpath -w "$PSHOT")" > /dev/null 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/tests/click-screen.ps1")" -ProcessId "$TRAYPID" 2>&1 | tr -d ''
sleep 2
echo "  saved choices:"; grep -A6 '"Displays"' "$D/host/paired-desktops.json" 2>/dev/null || find "$D" -name paired-desktops.json -exec grep -A6 Displays {} \;
log "== screenshot"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/tests/screenshot.ps1")" -Path "$(cygpath -w "$SHOT")" > /dev/null 2>&1
log "screenshot: $SHOT"
