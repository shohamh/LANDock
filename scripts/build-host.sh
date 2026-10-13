#!/bin/bash
# build-host.sh : build Apollo (our fork) and our helper tool from source, and put the result in host/bin/.
#
# Run inside an MSYS2 UCRT64 shell with the build dependencies installed (see docs/building.md), for example:
#   C:\msys64\usr\bin\bash.exe -lc "MSYSTEM=UCRT64 source /etc/profile; ./scripts/build-host.sh"
# GitHub Actions runs the same script (.github/workflows/build.yml).
#
# Source: the fork and commit pinned in scripts/versions.env. To build something else (for example a branch after pulling
# upstream changes) override before running:   APOLLO_REF=landock-next APOLLO_COMMIT= ./scripts/build-host.sh
#                                              APOLLO_REPO=https://github.com/someone/Apollo.git APOLLO_REF=main APOLLO_COMMIT= ...
#
# Steps: fetch the pinned commit (+submodules) -> fetch Boost (hash-checked) -> cmake + ninja -> build tools/vddtool.cpp
#        -> copy sunshine.exe, assets/ and vddtool.exe to host/bin/.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# environment overrides win over versions.env
OV_REPO=${APOLLO_REPO:-}; OV_REF=${APOLLO_REF:-}; OV_COMMIT=${APOLLO_COMMIT+set}; OV_COMMIT_VAL=${APOLLO_COMMIT:-}
# shellcheck source=versions.env
set -a; source "$ROOT/scripts/versions.env"; set +a
[ -n "$OV_REPO" ] && APOLLO_REPO=$OV_REPO
[ -n "$OV_REF" ] && APOLLO_REF=$OV_REF
[ -n "$OV_COMMIT" ] && APOLLO_COMMIT=$OV_COMMIT_VAL
WORK=${WORK:-$ROOT/build}
APOLLO=$WORK/apollo
export PROCESSOR_ARCHITECTURE=${PROCESSOR_ARCHITECTURE:-AMD64}     # CMake picks the prebuilt FFmpeg from this
say() { echo "[build-host] $*"; }
mkdir -p "$WORK"
command -v npm >/dev/null 2>&1 || export PATH="$PATH:/c/Program Files/nodejs"   # the web UI build needs Node

WANT=${APOLLO_COMMIT:-$APOLLO_REF}
say "1/5 Apollo from $APOLLO_REPO @ $WANT"
rm -rf "$APOLLO"; mkdir -p "$APOLLO"
git -C "$APOLLO" init -q
git -C "$APOLLO" config core.autocrlf false
git -C "$APOLLO" remote add origin "$APOLLO_REPO"
git -C "$APOLLO" fetch -q --depth 1 origin "$WANT"
git -C "$APOLLO" checkout -q FETCH_HEAD
HEAD_COMMIT=$(git -C "$APOLLO" rev-parse HEAD)
if [ -n "${APOLLO_COMMIT:-}" ] && [ "$HEAD_COMMIT" != "$APOLLO_COMMIT" ]; then
  echo "ERROR: fetched $HEAD_COMMIT, expected $APOLLO_COMMIT" >&2; exit 1
fi
say "  at $HEAD_COMMIT: $(git -C "$APOLLO" log -1 --format=%s)"
git -C "$APOLLO" submodule update --init --recursive --depth 1 -q

say "2/5 Boost $BOOST_VERSION"
BOOST_TAR=$WORK/boost-$BOOST_VERSION-cmake.tar.xz
BOOST_DIR=$WORK/boost-$BOOST_VERSION
if [ ! -d "$BOOST_DIR" ]; then
  [ -f "$BOOST_TAR" ] || curl -sSL -o "$BOOST_TAR" "$BOOST_URL"
  GOT=$(sha256sum "$BOOST_TAR" | cut -d' ' -f1)
  if [ "$GOT" != "$BOOST_SHA256" ]; then echo "ERROR: Boost hash $GOT, expected $BOOST_SHA256" >&2; exit 1; fi
  mkdir -p "$WORK/boost-extract" && tar -xf "$BOOST_TAR" -C "$WORK/boost-extract"
  mv "$WORK/boost-extract/boost-$BOOST_VERSION" "$BOOST_DIR" && rmdir "$WORK/boost-extract"
fi

say "3/5 configure and build Apollo (this takes a while)"
cmake -B "$APOLLO/build" -G Ninja -S "$APOLLO" -DFETCHCONTENT_SOURCE_DIR_BOOST="$(cygpath -m "$BOOST_DIR")"
ninja -C "$APOLLO/build"

say "4/5 vddtool"
g++ -std=gnu++23 -O2 -static -static-libgcc -static-libstdc++ -I "$APOLLO" -I "$APOLLO/third-party" "$ROOT/tools/vddtool.cpp" -o "$WORK/vddtool.exe" -lsetupapi

say "5/5 host/bin"
mkdir -p "$ROOT/host/bin"
cp "$APOLLO/build/sunshine.exe" "$ROOT/host/bin/sunshine.exe"
rm -rf "$ROOT/host/bin/assets" && cp -r "$APOLLO/build/assets" "$ROOT/host/bin/assets"
cp "$WORK/vddtool.exe" "$ROOT/host/bin/vddtool.exe"
echo "$HEAD_COMMIT" > "$ROOT/host/bin/APOLLO-COMMIT.txt"
say "done: $ROOT/host/bin/{sunshine.exe,assets,vddtool.exe}  (Apollo $HEAD_COMMIT)"
