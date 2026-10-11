#!/bin/bash
# run-arrangement.sh : the laptop lays its displays out by the desktop's screens, screens above and below, landscape and portrait
# (plan-monitor-layout.md, steps 1 to 3). One machine: the laptop runs here; the desktop's report is sent by hand (see
# arrangement-report.ps1), so its screens are not screens of this PC: no feedback between the laptop's displays and the report.
#
# Report:  A landscape 1920x1080 at (0,0) primary -> M1; B portrait 1080x1920 at (1920,0) -> M2; C landscape at (0,-1080) -> TV.

# Expected on the laptop (its own screen stays at (0,0)-(1920,1080)):
#          M1 at (1920,1080);  TV (above M1) at (1920,0);  M2 (portrait, right of M1) at (3840,1080) 1080x1920.
set -u
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
INST=$ROOT/build/run
W=$ROOT/build/arrangement-run
APP="$INST/LANDock.exe"
VDD="$INST/host/bin/vddtool.exe"
REPORT="$ROOT/tests/arrangement-report.ps1"
log() { echo "[$(date +%T)] $*"; }

cleanup() {
  log "== stopping"
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$REPORT")" -Mode Stop -DataDir "$(cygpath -w "$W/data")" > /dev/null 2>&1
  "$APP" host down > "$W/host-down.log" 2>&1
  for i in $(seq 1 30); do tasklist //FI "IMAGENAME eq LANDock.exe" 2>/dev/null | grep -q LANDock.exe || break; sleep 1; done
  log "== screens after cleanup"; "$VDD" screens
}
trap cleanup EXIT

rm -rf "$W"; mkdir -p "$W/data"
WW=$(cygpath -m "$W")
export LANDOCK_DATA_DIR="$WW/data" LANDOCK_INSTALL_DIR=$(cygpath -m "$INST")

log "== host up"
("$APP" host up --pin-file "$WW/pin.txt" > "$W/host-up.log" 2>&1 < /dev/null &)
for i in $(seq 1 150); do grep -q "Host is ready\|ERROR\|already running" "$W/host-up.log" 2>/dev/null && break; sleep 2; done
grep -q "Host is ready" "$W/host-up.log" || { log "host did not come up:"; tail -5 "$W/host-up.log"; exit 2; }
PIN=$(tr -d '[:space:]' < "$W/pin.txt")
CPORT=$(grep -o "Control port (desktop PCs connect here): [0-9]*" "$W/host-up.log" | grep -o "[0-9]*$")
log "control port $CPORT"

log "== desktop: add the laptop (pairs all six displays; a few minutes)"
"$APP" client add-host 127.0.0.1 --port "$CPORT" --pin "$PIN" --trust --name loopback > "$W/pair.log" 2>&1 || { log "pairing failed:"; tail -8 "$W/pair.log"; exit 3; }
grep -c "paired" "$W/pair.log" | sed 's/^/  lines saying paired: /'

log "== the report (ABOVE, RIGHT, BELOW) and the three displays it asks for"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$REPORT")" -Mode Start -DataDir "$(cygpath -w "$W/data")" -ControlPort "$CPORT" | tr -d '\r' | sed 's/^/  /'

log "== waiting for the displays and the layout"
sleep 60
log "== screens of this PC"
"$VDD" screens | sed 's/^/  /'

PASS=1
check() {   # check NAME X Y W H
  if "$VDD" screens | tr -d '\r' | grep -v primary | grep -q "pos=$2,$3 size=$4x$5"; then
    log "PASS  $1 at ($2,$3) $4x$5"
  else
    log "FAIL  $1 is not at ($2,$3) $4x$5"; PASS=0
  fi
}
check "M1 (landscape, the primary)"  1920 1080 1920 1080
check "TV (landscape, above M1)"  1920 0    1920 1080
check "M2 (portrait, right of M1)"   3840 1080 1080 1920

log "== laptop log (layout and agreement)"
grep -E "layout|agreed|reported" "$W/data/logs/landock.log" | cut -c1-200 | tail -12 | sed 's/^/  /'
if [ $PASS = 1 ]; then log "ARRANGEMENT: every display is where the desktop's arrangement puts it"; else log "ARRANGEMENT: not as expected"; fi
