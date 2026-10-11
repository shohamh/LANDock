#!/bin/bash
# run-mirror-displays.sh : four displays on ONE machine (host and desktop on this PC, loopback), each on a virtual monitor.
# Each new screen is a virtual display that the previous display created; M4 is the first display past the fixed three, so the
# laptop starts its instance on demand. Needs an assembled install (build/run). Stops the host at the end.
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
INST=$ROOT/build/run
W=$ROOT/build/test-data
APP="$INST/LANDock.exe"
VDD="$INST/host/bin/vddtool.exe"
# leftovers of an earlier run would keep files of the test folder open
LANDOCK_DATA_DIR="$(cygpath -m "$W")/data" LANDOCK_INSTALL_DIR=$(cygpath -m "$INST") "$APP" host down > /dev/null 2>&1
for i in $(seq 1 30); do tasklist //FI "IMAGENAME eq LANDock.exe" 2>/dev/null | grep -q LANDock.exe || break; sleep 1; done
source "$ROOT/tests/_shared-data.sh"; reset_shared_data "$W"
WW=$(cygpath -m "$W")
export LANDOCK_DATA_DIR="$WW/data" LANDOCK_INSTALL_DIR=$(cygpath -m "$INST")
log() { echo "[$(date +%T)] $*"; }

cleanup() {
  log "== stopping"
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

log "== desktop: add the laptop and pair (M4 is off: this PC has one screen yet)"
"$APP" client add-host 127.0.0.1 --port "$CPORT" --pin "$PIN" --trust --name loopback > "$W/pair.log" 2>&1 || { log "pairing failed:"; tail -8 "$W/pair.log"; exit 3; }
"$APP" client assign loopback M1 1 --mode windowed --rect 10,90,630,400 >> "$W/pair.log" 2>&1
"$APP" client assign loopback M2 1 --mode windowed --rect 650,90,630,400 >> "$W/pair.log" 2>&1
"$APP" client assign loopback TV 1 --mode windowed --rect 1290,90,630,400 >> "$W/pair.log" 2>&1
"$APP" client assign loopback M4 1 --mode windowed --rect 10,500,630,400 >> "$W/pair.log" 2>&1
"$APP" client status --host loopback > "$W/status0.log" 2>&1; cat "$W/status0.log" | head -12 | cut -c1-160

step() {   # step NAME MONITOR --> start that display only, after its screen exists
  log "== start $1 on monitor $2"
  "$APP" client assign loopback "$1" "$2" $3 > "$W/assign-$1.log" 2>&1
  "$APP" client start --host loopback --only "$1" > "$W/start-$1.log" 2>&1; echo "  start exit $?"
  if grep -q "not paired with Moonlight" "$W/start-$1.log"; then
    # a display turned on after the pairing needs its own Moonlight pairing: the laptop's PIN pairs the enabled streams
    log "  $1 is not paired with Moonlight yet: pairing with the PIN"
    "$APP" client pair --host loopback --pin "$PIN" > "$W/pair-$1.log" 2>&1; echo "  pair exit $?"
    "$APP" client start --host loopback --only "$1" > "$W/start-$1.log" 2>&1; echo "  start again exit $?"
  fi
  grep -E "agreed|: on monitor|problem|could not" "$W/start-$1.log" | cut -c1-200 | head -4
  sleep 25
  log "  screens:"; "$VDD" screens | sed 's/^/    /'
}
step M1 1 "--mode windowed --rect 10,90,630,400"
step M2 2 "--mode windowed --rect 650,90,630,400"
step TV 3 "--mode windowed --rect 1290,90,630,400"
"$APP" client assign loopback M4 4 --on --mode windowed --rect 10,500,630,400 > "$W/assign-M4on.log" 2>&1
step M4 4 "--mode windowed --rect 10,500,630,400"

log "== laptop log"
grep -E "reported|agreed|layout|started instance|up on port|started for a desktop|ERROR" "$W/data/logs/landock.log" | cut -c1-250 | tail -40
