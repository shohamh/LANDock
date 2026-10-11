#!/bin/bash
# probe: do virtual displays keep their monitor device id when Windows renames them? (creates two displays, stops them, creates them again)
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd); INST=$ROOT/build/run; W=$ROOT/build/probe-run; APP="$INST/LANDock.exe"
PS1="$(cygpath -w "$ROOT/build/monid.ps1")"; cp "$ROOT/build/monid.ps1" "$ROOT/build/monid.ps1" 2>/dev/null
rm -rf "$W"; mkdir -p "$W/data"; WW=$(cygpath -m "$W")
export LANDOCK_DATA_DIR="$WW/data" LANDOCK_INSTALL_DIR=$(cygpath -m "$INST")
cleanup() { "$APP" client stop > /dev/null 2>&1; "$APP" host down > /dev/null 2>&1; sleep 5; }
trap cleanup EXIT
("$APP" host up --pin-file "$WW/pin.txt" > "$W/up.log" 2>&1 < /dev/null &)
for i in $(seq 1 150); do grep -q "Host is ready" "$W/up.log" 2>/dev/null && break; sleep 2; done
PIN=$(tr -d '[:space:]' < "$W/pin.txt"); CP=$(grep -o "port (desktop PCs connect here): [0-9]*" "$W/up.log" | grep -o "[0-9]*$")
"$APP" client add-host 127.0.0.1 --port "$CP" --pin "$PIN" --trust --name loopback > "$W/pair.log" 2>&1
"$APP" client assign loopback M1 1 --mode windowed --rect 10,90,630,400 > /dev/null 2>&1
"$APP" client assign loopback M2 1 --mode windowed --rect 650,90,630,400 > /dev/null 2>&1
echo "== round 1: M1 on the real screen"; "$APP" client start --host loopback --only M1 > "$W/s1.log" 2>&1; sleep 25
"$APP" client assign loopback M2 2 --mode windowed --rect 650,90,630,400 > /dev/null 2>&1
"$APP" client start --host loopback --only M2 > "$W/s2.log" 2>&1; sleep 25
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PS1" | tr -d '\r' | sed 's/^/  /'
echo "== stop both"; "$APP" client stop --host loopback > /dev/null 2>&1; sleep 25
echo "== round 2: the same two again"; "$APP" client start --host loopback --only M1 > "$W/s3.log" 2>&1; sleep 25
"$APP" client start --host loopback --only M2 > "$W/s4.log" 2>&1; sleep 25
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PS1" | tr -d '\r' | sed 's/^/  /'
