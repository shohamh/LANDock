#!/bin/bash
# release-notes.sh <version> : prints the CHANGELOG.md section for that version (used as the GitHub release text).
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
VER=${1:?usage: release-notes.sh <version>}
awk -v ver="$VER" '
  $0 ~ "^## \\[" ver "\\]" { on = 1; next }
  on && /^## \[/ { exit }
  on { print }
' "$ROOT/CHANGELOG.md" | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'
