#!/bin/bash
# run-display-tests.sh [scenario|moves|all] : end-to-end test on ONE machine (host and client both on this PC, loopback).
#
# It starts the host with the LANDock app, adds it to the client over the control port (handshake, pairing), starts the three displays, then drives
# the desktop:
#   scenario : opens a Notepad on each virtual display, moves it around, types into it (read back and compared),
#              sweeps the mouse across laptop + all three displays
#   moves    : drags three Notepad windows with the real mouse laptop -> M1 -> M2 -> TV and back, checking window
#              position, text and which streams changed after every hop
# Needs: an UNLOCKED, awake, non-elevated desktop session; Windows 10/11; an assembled install folder
# (scripts/package-components.ps1, then scripts/assemble-install.ps1; INSTALL_DIR overrides the default build/run).
# It takes control of the mouse and keyboard for a few minutes: do not touch them while it runs.
set -u
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
RW=$(cygpath -m "$ROOT")
INSTALL=$(cygpath -u "${INSTALL_DIR:-$ROOT/build/run}")
WORK=$ROOT/build/test-data
MODE=${1:-all}
log() { echo "[$(date +%T)] $*"; }
for f in LANDock.exe host/bin/sunshine.exe host/bin/vddtool.exe client/moonlight/Moonlight.exe; do
  [ -f "$INSTALL/$f" ] || { echo "$INSTALL/$f is missing. Build the parts and run scripts/assemble-install.ps1 (see docs/building.md)."; exit 1; }
done
source "$ROOT/tests/_shared-data.sh"; reset_shared_data "$WORK"; mkdir -p "$WORK/out"
WW=$(cygpath -m "$WORK")
export LANDOCK_DATA_DIR="$WW/data" LANDOCK_INSTALL_DIR=$(cygpath -m "$INSTALL")
APP="$INSTALL/LANDock.exe"

cleanup() {
  log "== stopping streams and the host"
  "$APP" client stop > "$WORK/stop.log" 2>&1
  "$APP" host down > "$WORK/host-down.log" 2>&1
  for i in $(seq 1 30); do tasklist //FI "IMAGENAME eq LANDock.exe" 2>/dev/null | grep -q LANDock.exe || break; sleep 1; done
}
trap cleanup EXIT

log "== host up"
("$APP" host up --pin-file "$WW/pin.txt" > "$WORK/host-up.log" 2>&1 < /dev/null &)
for i in $(seq 1 120); do grep -q "Host is ready\|ERROR\|already running" "$WORK/host-up.log" 2>/dev/null && break; sleep 2; done
grep -q "Host is ready" "$WORK/host-up.log" || { log "host did not come up:"; tail -5 "$WORK/host-up.log"; exit 2; }
PIN=$(tr -d '[:space:]' < "$WORK/pin.txt")
CPORT=$(grep -o "Control port (desktop PCs connect here): [0-9]*" "$WORK/host-up.log" | grep -o "[0-9]*$")
[ -n "$CPORT" ] || { log "no control port in the host output"; tail -5 "$WORK/host-up.log"; exit 2; }

# the desktop PC side: add the laptop over the control port (the fingerprint is trusted here: this is the same PC),
# pair, put the three displays on the laptop screen, then start (the handshake runs first)
log "== client: add the laptop (control port $CPORT) and pair"
"$APP" client add-host 127.0.0.1 --port "$CPORT" --pin "$PIN" --trust --name loopback > "$WORK/pair.log" 2>&1 || { log "pairing failed:"; tail -8 "$WORK/pair.log"; exit 3; }
"$APP" client assign loopback M1 1 --mode windowed --rect 10,90,630,400 >> "$WORK/pair.log" 2>&1
"$APP" client assign loopback M2 1 --mode windowed --rect 650,90,630,400 >> "$WORK/pair.log" 2>&1
"$APP" client assign loopback TV 1 --mode windowed --rect 1290,90,630,400 >> "$WORK/pair.log" 2>&1
grep -E "paired|NOT" "$WORK/pair.log" | cut -c1-160

log "== client: start (handshake, then the displays)"
"$APP" client start --host loopback > "$WORK/start.log" 2>&1 || { log "starting the displays failed:"; tail -8 "$WORK/start.log"; exit 3; }
grep -E "agreed|: on monitor" "$WORK/start.log" | cut -c1-200
sleep 5

# the three virtual displays, left to right = M1, M2, TV
VDD="$INSTALL/host/bin/vddtool.exe"
mapfile -t DISP < <("$VDD" screens | grep -v primary | sed -E 's/^\\\\\.\\(DISPLAY[0-9]+) pos=(-?[0-9]+).*/\2 \1/' | sort -n | awk '{print $2}')
if [ "${#DISP[@]}" -ne 3 ]; then log "expected 3 virtual displays, found ${#DISP[@]}"; "$VDD" screens; exit 4; fi
M1='\\.\'"${DISP[0]}"; M2='\\.\'"${DISP[1]}"; TV='\\.\'"${DISP[2]}"
log "displays: M1=$M1 M2=$M2 TV=$TV"

# the scenarios need one Notepad window to open new windows from (Windows 11 Notepad merges files into one window)
STARTED_NOTEPAD=0
if ! powershell -NoProfile -Command "if (Get-Process notepad -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"; then (notepad.exe > /dev/null 2>&1 &); STARTED_NOTEPAD=1; sleep 3; fi

FAIL=0
for t in scenario moves; do
  [ "$MODE" = all ] || [ "$MODE" = "$t" ] || continue
  log "== running $t"
  mkdir -p "$WORK/out/$t"
  OUTW=$(cygpath -w "$WORK/out/$t")
  timeout 600 powershell -NoProfile -ExecutionPolicy Bypass -File "$RW/tests/$t.ps1" -M1 "$M1" -M2 "$M2" -TV "$TV" -Out "$OUTW" < /dev/null > "$WORK/out/$t.console.txt" 2>&1
  LOGF="$WORK/out/$t/scenario.log"
  [ -f "$LOGF" ] || { log "$t: no log produced"; tail -5 "$WORK/out/$t.console.txt"; FAIL=1; continue; }
  grep -E "matched exactly|mouse sweep|SUMMARY|STILL MISMATCHED|FAIL" "$LOGF" | cut -c1-200
  grep -q "STILL MISMATCHED\|=> FAIL" "$LOGF" && FAIL=1
  [ "$t" = scenario ] && { grep -q "mouse sweep: .* 0 mismatches" "$LOGF" || FAIL=1; }
  [ "$t" = moves ] && { grep -q "SUMMARY: .* 0 failures" "$LOGF" || FAIL=1; }
done
[ $STARTED_NOTEPAD = 1 ] && powershell -NoProfile -Command "Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force" > /dev/null 2>&1

if [ $FAIL = 0 ]; then log "ALL TESTS PASSED (screenshots in build/test-run/out/)"; else log "SOME TESTS FAILED (see build/test-run/out/)"; fi
exit $FAIL
