#!/bin/bash
# run-follow-screens.sh : live screens on ONE machine (host and desktop on this PC, loopback), plan-monitor-layout.md step 6.
#  1. The laptop is paired (all six of its displays are paired), M1 is started on the PC's screen.
#  2. "client follow" watches the PC's screens. Each new screen (a virtual monitor created by the display before it) gets a display.
#  3. M1 is stopped: its screen goes, and the displays on the screens that went are stopped after the grace period.
# Needs an assembled install (build/run). It takes a while (pairing six displays, then each display starts). Stops everything at the end.
set -u
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
INST=$ROOT/build/run
W=$ROOT/build/test-data
APP="$INST/LANDock.exe"
VDD="$INST/host/bin/vddtool.exe"
log() { echo "[$(date +%T)] $*"; }

LANDOCK_DATA_DIR="$(cygpath -m "$W")/data" LANDOCK_INSTALL_DIR=$(cygpath -m "$INST") "$APP" host down > /dev/null 2>&1
for i in $(seq 1 30); do tasklist //FI "IMAGENAME eq LANDock.exe" 2>/dev/null | grep -q LANDock.exe || break; sleep 1; done
source "$ROOT/tests/_shared-data.sh"; reset_shared_data "$W"
WW=$(cygpath -m "$W")
export LANDOCK_DATA_DIR="$WW/data" LANDOCK_INSTALL_DIR=$(cygpath -m "$INST")
FOLLOW_PID=""

cleanup() {
  log "== stopping"
  [ -n "$FOLLOW_PID" ] && kill "$FOLLOW_PID" 2>/dev/null
  "$APP" client stop > "$W/stop.log" 2>&1
  "$APP" host down > "$W/host-down.log" 2>&1
  for i in $(seq 1 30); do tasklist //FI "IMAGENAME eq LANDock.exe" 2>/dev/null | grep -q LANDock.exe || break; sleep 1; done
  log "== screens after cleanup"; "$VDD" screens
}
trap cleanup EXIT

log "== screens before"; "$VDD" screens
log "== host up"
("$APP" host up --pin-file "$WW/pin.txt" > "$W/host-up.log" 2>&1 < /dev/null &)
for i in $(seq 1 150); do grep -q "Host is ready\|ERROR\|already running" "$W/host-up.log" 2>/dev/null && break; sleep 2; done
grep -q "Host is ready" "$W/host-up.log" || { log "host did not come up:"; tail -5 "$W/host-up.log"; exit 2; }
PIN=$(tr -d '[:space:]' < "$W/pin.txt")
CPORT=$(grep -o "Control port (desktop PCs connect here): [0-9]*" "$W/host-up.log" | grep -o "[0-9]*$")
log "control port $CPORT"

log "== desktop: add the laptop (all six displays are paired)"
"$APP" client add-host 127.0.0.1 --port "$CPORT" --pin "$PIN" --trust --name loopback > "$W/pair.log" 2>&1 || { log "pairing failed:"; tail -8 "$W/pair.log"; exit 3; }
grep -c "paired" "$W/pair.log" | sed 's/^/  lines saying paired: /'
"$APP" client assign loopback M1 1 --mode windowed --rect 10,90,630,400 >> "$W/pair.log" 2>&1

log "== follow the screens of this PC (in the background)"
"$APP" client follow --host loopback > "$W/follow.out" 2>&1 < /dev/null &
FOLLOW_PID=$!
sleep 5

log "== start M1 on the PC's screen; the new screens should each get a display"
"$APP" client start --host loopback --only M1 > "$W/start-M1.log" 2>&1; echo "  start exit $?"

log "== waiting for the displays to follow the screens (up to 15 minutes)"
for i in $(seq 1 90); do
  n=$(grep -c "shown on .* now" "$W/data/logs/landock.log" 2>/dev/null); n=${n:-0}
  [ "$n" -ge 5 ] && break
  sleep 10
done
log "displays shown on new screens: $n"
log "screens now:"; "$VDD" screens | sed 's/^/  /'

log "== stop M1: its screen goes; the displays on the screens that went stop after the grace period"
"$APP" client stop --host loopback --only M1 > "$W/stop-M1.log" 2>&1
for i in $(seq 1 60); do
  g=$(grep -c "has been gone for" "$W/data/logs/landock.log" 2>/dev/null); g=${g:-0}
  [ "$g" -ge 1 ] && break
  sleep 10
done
sleep 60
log "grace expiries logged: $(grep -c 'has been gone for' "$W/data/logs/landock.log")"
log "screens after the removal:"; "$VDD" screens | sed 's/^/  /'

log "== desktop log: screens and displays"
grep -E "screens changed|shown on|has been gone|no longer following|the laptop knows|is new" "$W/data/logs/landock.log" | cut -c1-220 | tail -40
log "== laptop log: reports"
grep -E "screens changed|reported|started for a desktop" "$W/data/logs/landock.log" | cut -c1-200 | tail -20
