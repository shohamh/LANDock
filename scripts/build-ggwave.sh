#!/bin/bash
# build-ggwave.sh : builds ggwave (MIT, https://github.com/ggerganov/ggwave) at the commit pinned in scripts/versions.env
# into build/ggwave/ggwave.dll. Needs MSYS2 UCRT64 (g++). The DLL is copied next to LANDock.exe by the app build and the installer.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
set -a; source "$ROOT/scripts/versions.env"; set +a
SRC="$ROOT/build/ggwave-src"
OUT="$ROOT/build/ggwave"
if [ ! -d "$SRC/.git" ]; then
  rm -rf "$SRC"
  git clone -q "$GGWAVE_REPO" "$SRC"
fi
git -C "$SRC" fetch -q origin "$GGWAVE_COMMIT" 2>/dev/null || git -C "$SRC" fetch -q origin
git -C "$SRC" checkout -q "$GGWAVE_COMMIT"
mkdir -p "$OUT"
# one DLL with the MinGW runtime linked in statically: it needs only the Windows system libraries
g++ -std=c++17 -O2 -shared -static -DGGWAVE_SHARED -DGGWAVE_BUILD -I"$SRC/include" \
    -o "$OUT/ggwave.dll" "$SRC/src/ggwave.cpp" -Wl,--out-implib,"$OUT/libggwave.dll.a"
echo "built $OUT/ggwave.dll from ggwave $GGWAVE_COMMIT"
